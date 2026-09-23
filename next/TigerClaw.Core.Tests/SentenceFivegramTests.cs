using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private sealed class HistoryTestModel : ISentenceHistoryLanguageModel
        {
            public SentenceLmHistory BeginHistory => new(2, 0, 0, 0, 1);
            public double LogProbability(string a, string b, string c) => throw new Exception("History truncated to trigram");
            public bool HasObservedBigram(string a, string b) => false;
            public double Step(SentenceLmHistory h, string target, out SentenceLmHistory next)
            {
                uint token = target[0]; next = h.Append(token);
                return target == "五" ? (h.Count == 4 && h.D == '乙' ? 100 : -100) : 0;
            }
        }
        private static void ReviewHistoryDecoder(ISentenceLanguageModel model)
        {
            var lex = new Dictionary<string, List<string>> {
                ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "一" },
                ["cc"] = new() { "二" }, ["dd"] = new() { "三" }, ["ee"] = new() { "四", "五" },
                ["ff"] = new() { "𠀀", "好" }, ["gg"] = new() { "的人" }
            };
            using var decoder = ReviewDecoder(lex, model);
            using var full = ReviewDecoder(lex, model);
            string raw = "aabbccddeeffgg";
            for (int i = 1; i <= raw.Length; i++) ReviewSame(decoder.Decode(raw[..i], 20, true), full.DecodeFull(raw[..i], 20, true));
            for (int i = raw.Length; i > 0; i--) ReviewSame(decoder.Decode(raw[..i], 20, true), full.DecodeFull(raw[..i], 20, true));
            if (model is HistoryTestModel)
                Review(decoder.Decode("aabbccddee").Candidates[0].Text == "乙一二三五", "fourth history token changes Beam winner");
            var chosen = decoder.Decode("aabb").Candidates[0];
            var locked = new SentenceLockedPrefix("aabb", chosen.Text, chosen.Boundary);
            foreach (string input in new[] { raw, raw[..^1], raw[..^2], raw, "aabbcc", raw })
            {
                full.ResetDecodeCache();
                ReviewSame(decoder.Decode(input, 20, true, null, locked), full.Decode(input, 20, true, null, locked));
                foreach (var c in decoder.Decode(input, 20, true, null, locked).Candidates)
                    Review(c.Text.StartsWith(chosen.Text, StringComparison.Ordinal), "locked text retained");
            }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool cancelled = false;
            try { decoder.Decode(raw, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Review(cancelled, "fivegram cancellation");
            ReviewSame(decoder.Decode(raw, 20, true), full.DecodeFull(raw, 20, true));
            decoder.RequestHistoryTrim(raw, 6);
            ReviewSame(decoder.Decode(raw + "aa", 20, true), full.DecodeFull(raw + "aa", 20, true));
        }

        private static int RunShapeFivegramEvaluation(string modelPath, string fixture, string casesPath, string output)
        {
            var lexicon = SentenceLexiconIndex.Build(LoadSentenceLexiconSource(Path.Combine(fixture, "tiger_sentence.codes.txt")),
                SentenceCharacterRanks.TakeTop(1500), CoreRuntimeState.ParseCharacterSet(File.ReadAllText(Path.Combine(fixture, "tiger_sentence.full_code_whitelist.txt"))));
            var supplements = File.ReadLines(Path.Combine(fixture, "tiger_sentence.supplement.txt"))
                .Select(x => x.Split('#')[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .Where(x => x.Length > 0).Select(x => SentenceSupplementEntry.Create(x[0], x.Length > 1 ? long.Parse(x[1]) : 1000));
            var matcher = SentenceSupplementMatcher.Build(supplements);
            using var five = new SentenceFivegramModel(modelPath);
            SentenceInputDecoder Create(ISentenceLanguageModel m) => new(lexicon, m,
                emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5,
                supplementMatcher: matcher, allowDuplicateSingleCharacters: true,
                canonicalCodeReward: 2, canonicalIsolationFactor: 0, canonicalIsolationMinCodeLength: 4,
                lexicalPrior: SentenceLexicalPrior.LoadEmbedded(), lexicalPriorWeight: 0.1, lexicalCandidateLimit: 5);
            using var b = Create(five);
            using var writer = new StreamWriter(output);
            writer.WriteLine("id\tsource\tcode\ttarget\tfivegram\tfivegram_rank");
            int count = 0;
            foreach (string line in File.ReadLines(casesPath))
            {
                var fields = line.Split('\t');
                b.ResetDecodeCache();
                var y = b.Decode(fields[2], 20).Candidates;
                writer.WriteLine(line + "\t" + y.FirstOrDefault()?.Text + "\t" + (Array.FindIndex(y, c => c.Text == fields[3]) + 1));
                if (++count % 250 == 0) { writer.Flush(); Console.WriteLine("rows " + count); }
            }
            return 0;
        }

        private static int RunShapeFivegramScores(string path, string queries, string output)
        {
            using var model = new SentenceFivegramModel(path);
            using var session = model.CreateQuerySession();
            using var writer = new StreamWriter(output);
            foreach (string line in File.ReadLines(queries))
            {
                var h = session.BeginHistory; double score = 0;
                foreach (string token in line.Split('\t')) score = session.Step(h, token, out h);
                writer.WriteLine(score.ToString("R", CultureInfo.InvariantCulture));
            }
            return 0;
        }

        private static int RunShapeFivegramTests(string modelPath = null)
        {
            _reviewChecks = _reviewSnapshots = 0;
            ReviewHistoryDecoder(new HistoryTestModel());
            string folder = Path.Combine(Path.GetTempPath(), "tiger-shape-fivegram-" + Guid.NewGuid().ToString("N"));
            string models = Path.Combine(folder, "Models"); Directory.CreateDirectory(models);
            try
            {
                Review(SentenceFivegramModel.LoadAvailable(folder) == null, "missing Q8 has no model");
                foreach (string name in new[] { "sentence-fivegram.klm", "sentence-ngram-v2.bin", "sentence-ngram-mobile.bin" })
                    File.WriteAllText(Path.Combine(models, name), "retired format must not be opened");
                Review(SentenceFivegramModel.LoadAvailable(folder) == null, "retired paths ignored");
                string bad = Path.Combine(models, SentenceFivegramModel.FileName);
                File.WriteAllBytes(bad, new byte[256]);
                Review(SentenceFivegramModel.LoadAvailable(folder) == null, "corrupt Q8 has no model; no legacy fallback");
                if (modelPath != null)
                {
                    using var model = new SentenceFivegramModel(modelPath);
                    ReviewHistoryDecoder(model); ReviewFuzz(model);
                    double Score()
                    {
                        using var q = model.CreateQuerySession(); var h = q.BeginHistory; double sum = 0;
                        foreach (string t in new[] { "我", "𠀀", "🙂", "的", "测试", "\u0003" }) sum += q.Step(h, t, out h);
                        return sum;
                    }
                    double reference = Score();
                    Parallel.For(0, 8, _ => ReviewNumber(reference, Score(), "concurrent sessions and OOV/BOS/EOS"));
                    using var retained = model.CreateQuerySession();
                    var begin = retained.BeginHistory; double before = retained.Step(begin, "我", out _);
                    Review(!retained.HasObservedBigram("not-in-vocabulary", "我"), "OOV is not an observed bigram");
                    model.Dispose();
                    Review(!model.MappingClosed, "retired mapping retained by query lease");
                    ReviewNumber(before, retained.Step(begin, "我", out _), "retained lease scores after owner disposal");
                    bool rejected = false;
                    try { model.CreateQuerySession(); } catch (ObjectDisposedException) { rejected = true; }
                    Review(rejected, "disposed owner rejects new sessions");
                    retained.Dispose();
                    Review(model.MappingClosed, "last lease closes mapping");
                    rejected = false;
                    try { retained.Step(begin, "我", out _); } catch (ObjectDisposedException) { rejected = true; }
                    Review(rejected, "disposed session rejects access");
                }
                Console.WriteLine($"Shape Q8 passed: {_reviewChecks} checks, {_reviewSnapshots} snapshots; real model={modelPath != null}");
                return 0;
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
