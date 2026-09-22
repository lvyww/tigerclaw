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
        [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr joint_load_fivegram([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
        private static extern double joint_sentence_score(IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string tokens);
        [DllImport("jointkenlm", CallingConvention = CallingConvention.Cdecl)]
        private static extern void joint_free(IntPtr model);

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

        private static int RunShapeFivegramEvaluation(string modelPath, string priorPath, string fixture, string casesPath, string output)
        {
            var lexicon = SentenceLexiconIndex.Build(LoadSentenceLexiconSource(Path.Combine(fixture, "tiger_sentence.codes.txt")),
                SentenceCharacterRanks.TakeTop(1500), CoreRuntimeState.ParseCharacterSet(File.ReadAllText(Path.Combine(fixture, "tiger_sentence.full_code_whitelist.txt"))));
            var supplements = File.ReadLines(Path.Combine(fixture, "tiger_sentence.supplement.txt"))
                .Select(x => x.Split('#')[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .Where(x => x.Length > 0).Select(x => SentenceSupplementEntry.Create(x[0], x.Length > 1 ? long.Parse(x[1]) : 1000));
            var matcher = SentenceSupplementMatcher.Build(supplements);
            using var prior = SentenceNgramModel.Load(priorPath);
            using var five = new SentenceFivegramModel(modelPath, SentenceNgramModel.Load(priorPath));
            SentenceInputDecoder Create(ISentenceLanguageModel m) => new(lexicon, m,
                emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5,
                supplementMatcher: matcher, allowDuplicateSingleCharacters: true,
                canonicalCodeReward: 2, canonicalIsolationFactor: 0, canonicalIsolationMinCodeLength: 4,
                lexicalPrior: SentenceLexicalPrior.LoadEmbedded(), lexicalPriorWeight: 0.1, lexicalCandidateLimit: 5);
            using var a = Create(prior); using var b = Create(five);
            using var writer = new StreamWriter(output);
            writer.WriteLine("id\tsource\tcode\ttarget\ttrigram\tfivegram\tfivegram_rank");
            int count = 0;
            foreach (string line in File.ReadLines(casesPath))
            {
                var fields = line.Split('\t');
                a.ResetDecodeCache(); b.ResetDecodeCache();
                var x = a.Decode(fields[2], 20).Candidates; var y = b.Decode(fields[2], 20).Candidates;
                writer.WriteLine(line + "\t" + x.FirstOrDefault()?.Text + "\t" + y.FirstOrDefault()?.Text + "\t" + (Array.FindIndex(y, c => c.Text == fields[3]) + 1));
                if (++count % 250 == 0) { writer.Flush(); Console.WriteLine("rows " + count); }
            }
            return 0;
        }

        private static int RunShapeFivegramTests(string modelPath = null, string priorPath = null)
        {
            _reviewChecks = _reviewSnapshots = 0;
            ReviewHistoryDecoder(new HistoryTestModel());
            string folder = Path.Combine(Path.GetTempPath(), "tiger-shape-fivegram-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Models"));
            string priorFixture = Path.Combine(folder, "Models", "sentence-ngram-v2.bin");
            try
            {
                ReviewWriteModel(priorFixture);
                var absent = SentenceFivegramModel.LoadAvailable(folder);
                Review(absent is SentenceNgramModel, "absent fivegram uses trigram"); ((IDisposable)absent).Dispose();
                File.WriteAllText(Path.Combine(folder, "Models", SentenceFivegramModel.FileName), "invalid model");
                var corrupt = SentenceFivegramModel.LoadAvailable(folder);
                Review(corrupt is SentenceNgramModel, "corrupt fivegram or absent DLL uses trigram"); ((IDisposable)corrupt).Dispose();
                if (modelPath != null)
                {
                    var prior = SentenceNgramModel.Load(priorPath ?? priorFixture);
                    using var model = new SentenceFivegramModel(modelPath, prior);
                    ReviewHistoryDecoder(model);
                    ReviewFuzz(model);
                    // Full-state KenLM is an independent reference for the history ABI.
                    IntPtr reference = joint_load_fivegram(modelPath); Review(reference != IntPtr.Zero, "reference load");
                    try
                    {
                        Parallel.For(0, 8, worker => {
                            using var session = model.CreateQuerySession();
                            foreach (string text in new[] { "", "我", "马云雷军入选大亨名单", "新人上午来面试", "甲一二三五", "乙一二三五", "𠀀🙂的测试", "这是一个用于检查上下文状态的较长句子" })
                            {
                                var history = session.BeginHistory; double score = 0;
                                var tokens = new List<string>();
                                var e = StringInfo.GetTextElementEnumerator(text);
                                while (e.MoveNext()) { string t = e.GetTextElement(); tokens.Add(t); score += session.Step(history, t, out history); }
                                score += session.Step(history, "\u0003", out _);
                                double expected = joint_sentence_score(reference, string.Join(" ", tokens)) * Math.Log(10);
                                Review(Math.Abs(score - expected) < 1e-9, "full-state probability parity including BOS/EOS/OOV");
                                Review(session.HasObservedBigram("一", "国") == prior.HasObservedBigram("一", "国"), "original isolation prior retained");
                            }
                        });
                    }
                    finally { joint_free(reference); }
                    using var retained = model.CreateQuerySession();
                    var begin = retained.BeginHistory;
                    double before = retained.Step(begin, "我", out _);
                    model.Dispose();
                    ReviewNumber(before, retained.Step(begin, "我", out _), "retired native model kept alive by decoder lease");
                    bool rejected = false;
                    try { model.CreateQuerySession(); } catch (ObjectDisposedException) { rejected = true; }
                    Review(rejected, "disposed owner rejects new queries");
                    retained.Dispose();
                    rejected = false;
                    try { retained.Step(begin, "我", out _); } catch (ObjectDisposedException) { rejected = true; }
                    Review(rejected && prior.MappingClosed, "last lease closes native and prior resources");
                }
                Console.WriteLine($"Shape fivegram passed: {_reviewChecks} checks, {_reviewSnapshots} snapshots; real model={modelPath != null}");
                return 0;
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
