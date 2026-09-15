using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunSentenceReviewBenchmark(string baseline, string modelPath, int trial)
        {
            var context = new AssemblyLoadContext("sentence-benchmark", true);
            Assembly old = context.LoadFromAssemblyPath(Path.GetFullPath(baseline));
            Type indexType = old.GetType("TigerClaw.Core.SentenceLexiconIndex", true);
            Type decoderType = old.GetType("TigerClaw.Core.SentenceInputDecoder", true);
            Type penaltyType = old.GetType("TigerClaw.Core.SentenceIsolationPenalty", true);
            object oldModel = modelPath == null ? null : old.GetType("TigerClaw.Core.SentenceNgramModel", true)
                .GetMethod("Load").Invoke(null, new object[] { modelPath });
            using var model = modelPath == null ? null : SentenceNgramModel.Load(modelPath);
            var lexicon = new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" } };
            var build = indexType.GetMethod("Build");
            var parameters = build.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            parameters[0] = lexicon;
            object index = build.Invoke(null, parameters);
            var ctor = decoderType.GetConstructors().Single();
            var options = new Dictionary<string, object> { ["lexicon"] = index, ["languageModel"] = oldModel,
                ["beamWidth"] = 2000, ["emittedCharacterReward"] = 2.0, ["wholeInputSingleCharacterReward"] = 5.0,
                ["isolationPenalty"] = penaltyType.GetField("None").GetValue(null), ["allowDuplicateSingleCharacters"] = true };
            object previous = ctor.Invoke(ctor.GetParameters().Select(p => options.TryGetValue(p.Name, out var x) ? x : p.DefaultValue).ToArray());
            using var current = ReviewDecoder(lexicon, model);
            object oldSeed = ((IEnumerable)RevisionProperty(RevisionCall(previous, "DecodeFull", new() { ["rawCode"] = "aa" }), "Candidates")).Cast<object>().First();
            var seed = current.DecodeFull("aa").Candidates[0];
            object oldLock = Activator.CreateInstance(old.GetType("TigerClaw.Core.SentenceLockedPrefix", true),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { (object)"aa", RevisionProperty(oldSeed, "Text"), RevisionProperty(oldSeed, "Boundary") }, CultureInfo.InvariantCulture);
            var prefix = new SentenceLockedPrefix("aa", seed.Text, seed.Boundary);
            object Decode(string revision, string raw) => RevisionCall(revision == "old" ? previous : current, "Decode",
                new() { ["rawCode"] = raw, ["candidateLimit"] = 20, ["includeEarlyCommitEvidence"] = true,
                    ["lockedPrefix"] = revision == "old" ? oldLock : prefix });
            try
            {
                foreach (int length in new[] { 80, 128 })
                {
                    string raw = new('a', length);
                    Review(RevisionSnapshot(Decode("old", raw)) == RevisionSnapshot(Decode("new", raw)), "benchmark score parity");
                    foreach (string operation in new[] { "repeat", "append", "backspace" })
                    {
                        string before = operation == "backspace" ? raw + "a" : raw;
                        string after = operation == "append" ? raw + "a" : raw;
                        for (int sample = 0; sample < 5; sample++)
                        {
                            foreach (string revision in (trial + sample) % 2 == 0 ? new[] { "old", "new" } : new[] { "new", "old" })
                            {
                                Decode(revision, before);
                                long allocated = GC.GetAllocatedBytesForCurrentThread();
                                long start = Stopwatch.GetTimestamp();
                                object result = Decode(revision, after);
                                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                                Console.WriteLine(JsonSerializer.Serialize(new { test = "sentence_review_benchmark", trial,
                                    revision, length, operation, sample, milliseconds, allocated_bytes = bytes,
                                    candidates = ((IEnumerable)RevisionProperty(result, "Candidates")).Cast<object>().Count(),
                                    production_model = modelPath != null, physical_tsf = false,
                                    reflection_wrapper_included = true }));
                            }
                        }
                        Review(RevisionSnapshot(Decode("old", after)) == RevisionSnapshot(Decode("new", after)), "benchmark operation parity");
                    }
                }
                return 0;
            }
            finally
            {
                (oldModel as IDisposable)?.Dispose();
                context.Unload();
            }
        }
    }
}
