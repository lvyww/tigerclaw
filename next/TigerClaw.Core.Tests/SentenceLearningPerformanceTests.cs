using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static double _learningBenchmarkSink;
        private const long LearningBenchmarkTime = 1700000000;

        private static void LearningPerformance()
        {
            LearningCheck(ReferenceEquals(SentenceLearningSnapshot.Empty,
                SentenceLearningSnapshot.Build(Array.Empty<SentenceLearningEvent>())), "unchanged empty snapshot identity");
            LearningIndexEquivalence();
            LearningPrefixBoundary();
            foreach (int count in new[] { 1000, SentenceLearningStore.MaximumEvents })
                LearningIndexStress(count, distinctTexts: false);
            LearningIndexStress(SentenceLearningStore.MaximumEvents, distinctTexts: true);
        }

        private static SentenceLearningEvent IndexEvent(string mode, string code, string text, string context, long time) =>
            new() { Mode = mode, Code = code, Text = text, Context = context, Time = time };

        private static void LearningIndexEquivalence()
        {
            var random = new Random(412321);
            string[] modes = { "test-v1", "other-v1", "TEST-v1" };
            string[] codes = { "aa", "aabb", "aabc", "aa2bb", "aa;bb", "aa'bb", "ABcd", "bbcc", "aabbcc" };
            string[] texts = { "甲", "甲乙", "甲丙", "乙甲", "𰻞甲", "甲𰻞", "𰻞" };
            string[] contexts = { "", "前甲", "后乙", "𰻞", "甲", "未见" };
            string[] codeQueries = codes.Concat(new[] { "", "a", "aab", "aa2", "A", "not-found" }).ToArray();
            string[] textQueries = texts.Concat(new[] { "", "乙", "𰻞", "\ud883", "not-found" }).ToArray();
            int comparisons = 0;
            for (int fixture = 0; fixture < 20; fixture++)
            {
                var events = new List<SentenceLearningEvent>();
                for (int i = 0; i < 600; i++)
                    events.Add(IndexEvent(modes[random.Next(modes.Length)], codes[random.Next(codes.Length)],
                        texts[random.Next(texts.Length)], contexts[random.Next(contexts.Length - 1)],
                        LearningBenchmarkTime - random.Next(-2, 180) * 86400L));
                // Invalid entries and future/out-of-order event times must keep
                // the original rules. Unknown contexts must not aid generalization.
                events.Add(IndexEvent("", "aa", "甲乙", "", LearningBenchmarkTime));
                events.Add(IndexEvent("test-v1", "", "甲乙", "", LearningBenchmarkTime));
                events.Add(IndexEvent("test-v1", "aa", "{日期}", "", LearningBenchmarkTime));
                events.Add(IndexEvent("test-v1", "aa", "\ud800", "", LearningBenchmarkTime));
                events.Add(IndexEvent("test-v1", "aa", "甲乙", "三个字", LearningBenchmarkTime));
                events.Add(IndexEvent("test-v1", "aa", "甲乙", "\ud800", LearningBenchmarkTime));
                var before = SentenceLearningReference.Build(events, LearningBenchmarkTime);
                var after = SentenceLearningSnapshot.Build(events, LearningBenchmarkTime);
                for (int query = 0; query < 640; query++)
                {
                    string mode = modes[random.Next(modes.Length)], code = codeQueries[random.Next(codeQueries.Length)],
                        text = textQueries[random.Next(textQueries.Length)], context = contexts[random.Next(contexts.Length)];
                    double expected = before.Score(mode, code, text, context), actual = after.Score(mode, code, text, context);
                    LearningCheck(Math.Abs(expected - actual) < 1e-12, "indexed exact score equals frozen PR oracle");
                    expected = before.PrefixScore(mode, code, text, context); actual = after.PrefixScore(mode, code, text, context);
                    LearningCheck(Math.Abs(expected - actual) < 1e-12, "indexed prefix score equals frozen PR oracle");
                    comparisons += 2;
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { test = "learning_index_equivalence", comparisons, tolerance = 1e-12 }));
        }

        private static void LearningPrefixBoundary()
        {
            var events = new List<SentenceLearningEvent>();
            for (int i = 0; i < 70; i++)
                events.Add(IndexEvent(i < 64 ? "other-v1" : "test-v1", "aa" + i.ToString("D3", CultureInfo.InvariantCulture),
                    "甲乙", "前甲", LearningBenchmarkTime));
            var snapshot = SentenceLearningSnapshot.Build(events, LearningBenchmarkTime);
            LearningCheck(snapshot.PrefixScore("test-v1", "aa", "甲", "前甲") == 0,
                "64 rows count skipped modes, not only matching modes");
            events[63].Mode = "test-v1";
            snapshot = SentenceLearningSnapshot.Build(events, LearningBenchmarkTime);
            LearningCheck(snapshot.PrefixScore("test-v1", "aa", "甲", "前甲") == 9, "64th row included");
            events.Add(IndexEvent("other-v1", "aa", "乙丙", "前甲", LearningBenchmarkTime));
            snapshot = SentenceLearningSnapshot.Build(events, LearningBenchmarkTime);
            LearningCheck(snapshot.PrefixScore("test-v1", "aa", "甲", "前甲") == 0, "equal-code row still consumes limit");
            LearningCheck(snapshot.PrefixScore("test-v1", "aa063", "甲", "前甲") == 0, "exact code not prefix hint");
            LearningCheck(snapshot.PrefixScore("test-v1", "aa06", "甲乙", "前甲") == 0, "exact text not prefix hint");
            LearningCheck(snapshot.PrefixScore("test-v1", "AA06", "甲", "前甲") == 0, "ordinal case-sensitive matching");
            var scalar = SentenceLearningSnapshot.Build(new[] { IndexEvent("test-v1", "aabb", "𰻞甲", "", LearningBenchmarkTime) }, LearningBenchmarkTime);
            var oracle = SentenceLearningReference.Build(new[] { IndexEvent("test-v1", "aabb", "𰻞甲", "", LearningBenchmarkTime) }, LearningBenchmarkTime);
            LearningCheck(Math.Abs(scalar.PrefixScore("test-v1", "aa", "𰻞".Substring(0, 1), "") -
                oracle.PrefixScore("test-v1", "aa", "𰻞".Substring(0, 1), "")) < 1e-12, "UTF-16 ordinal prefix compatibility");
        }

        private static double LearningMedianUs(Func<double> operation, int iterations, int batches = 5)
        {
            _learningBenchmarkSink += operation();
            var times = new double[batches];
            for (int batch = 0; batch < batches; batch++)
            {
                double sum = 0;
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) sum += operation();
                times[batch] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
                _learningBenchmarkSink += sum;
            }
            Array.Sort(times);
            return times[times.Length / 2];
        }

        private static void LearningIndexStress(int count, bool distinctTexts)
        {
            var events = Enumerable.Range(0, count).Select(i => IndexEvent("test-v1", "aabb",
                distinctTexts ? "甲" + (char)(0x3400 + i) : "甲乙", "前" + (char)(0x3400 + i), LearningBenchmarkTime)).ToArray();
            long buildStart = Stopwatch.GetTimestamp();
            var snapshot = SentenceLearningSnapshot.Build(events, LearningBenchmarkTime);
            double buildMs = Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
            var before = SentenceLearningReference.Build(events, LearningBenchmarkTime);
            string context = events[0].Context, text = events[0].Text;
            Func<double> indexed = () => snapshot.PrefixScore("test-v1", "aa", "甲", context);
            Func<double> reference = () => before.PrefixScore("test-v1", "aa", "甲", context);
            double expectedStress = distinctTexts ? 9 : 24;
            LearningCheck(indexed() == expectedStress && indexed() == reference(), "stress prefix result equals oracle");

            // Deterministic complexity guard, not a wall-clock CI threshold:
            // the old nested Score path allocates HashSets, the indexed path
            // must allocate nothing after warmup, for exact and prefix queries.
            for (int i = 0; i < 128; i++)
            {
                _learningBenchmarkSink += indexed();
                _learningBenchmarkSink += snapshot.Score("test-v1", "aabb", text, context);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            double sink = 0;
            for (int i = 0; i < 512; i++)
            {
                sink += indexed();
                sink += snapshot.Score("test-v1", "aabb", text, context);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            _learningBenchmarkSink += sink;
            LearningCheck(allocated == 0, "indexed hot queries allocate zero bytes regardless of history size");
            long oldAllocated = GC.GetAllocatedBytesForCurrentThread();
            _learningBenchmarkSink += reference();
            oldAllocated = GC.GetAllocatedBytesForCurrentThread() - oldAllocated;
            // Snapshot readers are concurrent; there is no mutable query cache.
            var parallel = new double[64];
            Parallel.For(0, parallel.Length, i => parallel[i] = indexed());
            LearningCheck(parallel.All(value => value == expectedStress), "concurrent immutable query results");

            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["aa"] = new() { "甲" }, ["bb"] = new() { "乙" }
            });
            var decoder = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                beamWidth: 1, isolationPenalty: SentenceIsolationPenalty.None);
            decoder.SetLearning(snapshot, "test-v1");
            Func<double> fullDecode = () =>
            {
                var result = decoder.DecodeFull("aa", 1, true); // Never time a cached candidate.
                if (result.Candidates.Length != 1 || result.Candidates[0].Text != "甲")
                    throw new Exception("Two-key, one-candidate stress result changed");
                return result.Candidates[0].FinalScore;
            };
            double decodeUs = LearningMedianUs(fullDecode, 100);
            double prefixUs = LearningMedianUs(indexed, 2000);
            double oldPrefixUs = LearningMedianUs(reference, 1, 3);
            LearningCheck(snapshot.Score("test-v1", "aabb", text, context) == expectedStress, "build preserves level score under pressure");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = "learning_index_stress", records = count, distinctTexts, build_ms = buildMs,
                indexed_prefix_us = prefixUs, reference_prefix_us = oldPrefixUs, full_two_key_decode_us = decodeUs,
                indexed_allocated_bytes_1024_queries = allocated, reference_allocated_bytes_one_prefix = oldAllocated,
                physicalTsfTested = false
            }));
        }
    }
}
