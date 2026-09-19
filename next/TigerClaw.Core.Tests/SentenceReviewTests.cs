using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static long _reviewChecks, _reviewSnapshots;
        private static void Review(bool value, string name)
        {
            Interlocked.Increment(ref _reviewChecks);
            if (!value) throw new InvalidOperationException("sentence review: " + name);
        }
        private static void ReviewNumber(double a, double b, string name) =>
            Review(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), name + $" ({a:R}, {b:R})");
        private static SentenceInputDecoder ReviewDecoder(Dictionary<string, List<string>> lexicon,
            ISentenceLanguageModel model = null, int beam = 2000, bool duplicates = true) =>
            new(SentenceLexiconIndex.Build(lexicon), model ?? NeutralSentenceLanguageModel.Instance,
                beamWidth: beam, isolationPenalty: SentenceIsolationPenalty.None,
                emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5, allowDuplicateSingleCharacters: duplicates);

        private static void ReviewBoundary(SentencePathBoundary a, SentencePathBoundary b)
        {
            while (a != null && b != null)
            {
                Review(a.TextLength == b.TextLength && a.RawLength == b.RawLength, "boundary offsets");
                ReviewNumber(a.LearningScore, b.LearningScore, "boundary learning");
                a = a.Previous; b = b.Previous;
            }
            Review(a == null && b == null, "boundary chain length");
        }
        private static void ReviewSame(SentenceDecodeResult a, SentenceDecodeResult b)
        {
            _reviewSnapshots++;
            Review(a.RawCode == b.RawCode && a.LearningAffected == b.LearningAffected && a.LearningMode == b.LearningMode, "result identity/learning");
            var aa = a.Candidates ?? Array.Empty<SentenceCandidate>();
            var bb = b.Candidates ?? Array.Empty<SentenceCandidate>();
            Review(aa.Length == bb.Length, "candidate count " + a.RawCode);
            for (int i = 0; i < aa.Length; i++)
            {
                Review(aa[i].Text == bb[i].Text && aa[i].SegmentedCode == bb[i].SegmentedCode && aa[i].MaxLexiconRank == bb[i].MaxLexiconRank, "candidate order/text/rank");
                ReviewNumber(aa[i].BaseScore, bb[i].BaseScore, "base");
                ReviewNumber(aa[i].FinalScore, bb[i].FinalScore, "final");
                ReviewNumber(aa[i].ConfidenceScore, bb[i].ConfidenceScore, "mass");
                ReviewNumber(aa[i].SupplementScore, bb[i].SupplementScore, "supplement");
                ReviewNumber(aa[i].LearningScore, bb[i].LearningScore, "learning");
                ReviewBoundary(aa[i].Boundary, bb[i].Boundary);
            }
            var ac = a.ConfidenceCandidates ?? Array.Empty<SentenceConfidenceCandidate>();
            var bc = b.ConfidenceCandidates ?? Array.Empty<SentenceConfidenceCandidate>();
            Review(ac.Count == bc.Count, "complete confidence count");
            for (int i = 0; i < ac.Count; i++)
            {
                Review(ac[i].Text == bc[i].Text && ac[i].MaxLexiconRank == bc[i].MaxLexiconRank, "complete confidence order");
                ReviewNumber(ac[i].ConfidenceScore, bc[i].ConfidenceScore, "complete confidence mass");
                ReviewBoundary(ac[i].Boundary, bc[i].Boundary);
            }
            var x = a.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            var y = b.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            Review(x.ConfidenceTruncated == y.ConfidenceTruncated && x.MergedIncompleteTail == y.MergedIncompleteTail &&
                x.NeutralIncompleteTail == y.NeutralIncompleteTail && x.NeutralLowConfidence == y.NeutralLowConfidence &&
                x.IgnoreNeuralConstraint == y.IgnoreNeuralConstraint && x.Proposal == y.Proposal, "evidence flags/proposal");
            ReviewNumber(x.ProposalShare, y.ProposalShare, "proposal share");
            var xp = x.Prefixes ?? Array.Empty<SentencePrefixEvidence>();
            var yp = y.Prefixes ?? Array.Empty<SentencePrefixEvidence>();
            Review(xp.Length == yp.Length, "prefix count");
            for (int i = 0; i < xp.Length; i++)
            {
                Review(xp[i].Text == yp[i].Text && xp[i].RawLength == yp[i].RawLength && xp[i].BoundaryClosed == yp[i].BoundaryClosed, "prefix identity");
                ReviewNumber(xp[i].Share, yp[i].Share, "prefix share");
                ReviewNumber(xp[i].BoundaryShare, yp[i].BoundaryShare, "boundary share");
            }
            Review(x.RawLengths.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(
                y.RawLengths.OrderBy(p => p.Key, StringComparer.Ordinal)), "prefix raw-length lookup");
        }

        private static int RunSentenceReviewTests(string modelPath = null)
        {
            _reviewChecks = _reviewSnapshots = 0;
            string folder = Path.Combine(Path.GetTempPath(), "tigerclaw-sentence-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                ReviewCorrectness();
                ReviewCacheAndCancellation();
                ReviewFuzz(null);
                ReviewLearningAccumulator();
                ReviewJournal(folder);
                ReviewMappedModel(folder, modelPath);
                ReviewMobileModels(folder);
                ReviewRerankCancellation(folder);
                Console.WriteLine(JsonSerializer.Serialize(new { test = "sentence_review", status = "passed",
                    checks = _reviewChecks, snapshots = _reviewSnapshots, production_model = modelPath != null,
                    physical_tsf = false }));
                return 0;
            }
            finally { ReviewCleanupDirectory(folder); }
        }

        private sealed class ReviewCountingModel : ISentenceLanguageModel
        {
            internal int Calls;
            internal Func<string, string, string, double> Score = (_, _, _) => 0;
            public double LogProbability(string a, string b, string c)
            {
                Interlocked.Increment(ref Calls);
                return Score(a, b, c);
            }
            public bool HasObservedBigram(string a, string b) => false;
        }

        private static void ReviewCorrectness()
        {
            var words = Enumerable.Range(0, 20).Select(i => ((char)(0x4E10 + i)).ToString()).ToList();
            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" }, ["bb"] = words });
            var model = new ReviewCountingModel { Score = (_, _, c) => c == "甲" ? 0.1 : 0 };
            using var decoder = new SentenceInputDecoder(lexicon, model, rankPenalty: 0, isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true);
            var small = decoder.Decode("aabb", 20, true);
            var all = decoder.Decode("aabb", 40, true);
            Review(small.Candidates.Length == 20 && small.ConfidenceCandidates.Count == 40, "menu is not confidence pool");
            double share = small.EarlyCommitEvidence.Prefixes.Single(p => p.Text == "甲").Share;
            Review(Math.Abs(share - 1 / (1 + Math.Exp(-0.1))) < 1e-12, "hidden candidate denominator");
            ReviewNumber(share, all.EarlyCommitEvidence.Prefixes.Single(p => p.Text == "甲").Share, "display limit independent");
            var exposed = decoder.Decode("aabb", 20, true);
            exposed.EarlyCommitEvidence.Prefixes[0].Share = 123;
            exposed.EarlyCommitEvidence.RawLengths.Clear();
            var isolated = decoder.Decode("aabb", 20, true);
            Review(isolated.EarlyCommitEvidence.Prefixes[0].Share <= 1 && isolated.EarlyCommitEvidence.RawLengths.Count > 0, "evidence consumer cannot mutate cached snapshot");
            // Only the ancestor exceeds the beam; the final bucket has one state.
            using var narrow = ReviewDecoder(new Dictionary<string, List<string>>
            { ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "国" } }, model, beam: 1);
            Review(narrow.Decode("aabb", 20, true).EarlyCommitEvidence.ConfidenceTruncated, "ancestor truncation propagated");
            var longCodes = new Dictionary<string, List<string>> { ["abcde"] = new() { "甲", "乙丙" }, ["fg"] = new() { "丁" } };
            using var incremental = ReviewDecoder(longCodes, duplicates: false);
            using var fresh = ReviewDecoder(longCodes, duplicates: false);
            incremental.Decode("abcde");
            var longResult = incremental.Decode("abcdefg", 20, true);
            ReviewSame(longResult, fresh.DecodeFull("abcdefg", 20, true));
            Review(longResult.Candidates.Length == 1 && longResult.Candidates[0].FinalScore == 4, "whole-input bonus and word eligibility");
            var selectors = new Dictionary<string, List<string>> { ["aa"] = new() { "甲" }, ["bb"] = new() { "乙" }, ["cc"] = words };
            using var selected = ReviewDecoder(selectors);
            using var selectedFresh = ReviewDecoder(selectors);
            foreach (string raw in new[] { "aabbcc123", "aabbcc12", "aabbcc1", "aabbcc", "aabbcc;", "aabbcc", "aabbcc'", "aabbcc" })
                ReviewSame(selected.Decode(raw, 20, true), selectedFresh.DecodeFull(raw, 20, true));
            var learning = SentenceLearningSnapshot.Build(new[] { new SentenceLearningEvent { Time = 1700000000, Mode = "review", Code = "bb", Text = "乙", Context = "甲" } }, 1700000000);
            selected.SetLearning(learning, "review"); selectedFresh.SetLearning(learning, "review");
            Review(selected.Decode("aabb").LearningAffected, "learning reached");
            ReviewSame(selected.Decode("aa", 20, true), selectedFresh.DecodeFull("aa", 20, true));
        }

        private static void ReviewCacheAndCancellation()
        {
            var lex = new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "国" } };
            var model = new ReviewCountingModel();
            using var decoder = ReviewDecoder(lex, model, beam: 32);
            using var oracle = ReviewDecoder(lex, beam: 32);
            var first = decoder.Decode("aa").Candidates[0];
            var locked = new SentenceLockedPrefix("aa", first.Text, first.Boundary);
            string raw = new('a', 80);
            var original = decoder.Decode(raw, 20, true, null, locked);
            int calls = model.Calls;
            original.Candidates[0].FinalScore = 1e100;
            original.Candidates[0].Text = "mutated menu";
            var repeat = decoder.Decode(raw, 20, true, null, locked);
            Review(model.Calls == calls, "identical locked request performs no scoring");
            ReviewSame(repeat, oracle.Decode(raw, 20, true, null, locked));
            foreach (string input in new[] { raw + "a", raw, raw + "aa", raw, raw[..^2] })
            {
                oracle.ResetDecodeCache();
                ReviewSame(decoder.Decode(input, 20, true, null, locked), oracle.Decode(input, 20, true, null, locked));
            }
            decoder.CompleteComposition();
            Review(decoder.RetainedStatePositions == 0, "completed composition releases lattice");
            using var cancellation = new CancellationTokenSource();
            int count = 0;
            model.Score = (_, _, _) => { if (++count == 40) cancellation.Cancel(); return 0; };
            bool canceled = false;
            try { decoder.Decode(raw, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Review(canceled && count < 200 && decoder.RetainedStatePositions == 0, "cooperative cancel discards partial work early");
            model.Score = (_, _, _) => 0;
            ReviewSame(decoder.Decode(raw, 20, true), oracle.DecodeFull(raw, 20, true));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int blocked = 0;
            model.Score = (_, _, _) => { if (Interlocked.Exchange(ref blocked, 1) == 0) { entered.Set(); release.Wait(); } return 0; };
            decoder.ResetDecodeCache();
            var running = Task.Run(() => decoder.Decode("aabb"));
            try
            {
                Review(entered.Wait(5000), "background decoder started");
                Review(Task.Run(decoder.CompleteComposition).Wait(2000), "completion does not wait for decoder");
            }
            finally { release.Set(); }
            Review(running.Wait(5000) && decoder.RetainedStatePositions == 0, "deferred composition cleanup");
            var single = new Dictionary<string, List<string>> { ["aa"] = new() { "甲" } };
            using var retained = ReviewDecoder(single);
            using var full = ReviewDecoder(single);
            for (int length = 2; length <= 768; length += 2)
            {
                string text = new('a', length);
                ReviewSame(retained.Decode(text, 20, true), full.DecodeFull(text, 20, true));
                retained.RequestHistoryTrim(text, Math.Max(0, length - 4));
            }
            Review(retained.RetainedStatePositions < 80, "old history positions released in bounded batches");
            ReviewSame(retained.Decode("aaaa", 20, true), full.DecodeFull("aaaa", 20, true));
            for (int length = 6; length <= 150; length += 2)
                ReviewSame(retained.Decode(new string('a', length), 20, true), full.DecodeFull(new string('a', length), 20, true));
            Review(retained.RetainedStatePositions == 151, "old commit checkpoint does not trim rebuilt composition");
        }

        private static void ReviewFuzz(ISentenceLanguageModel model)
        {
            var lex = new Dictionary<string, List<string>>
            {
                ["aa"] = new() { "甲", "乙", "a\u0301", "\U00020000" }, ["bb"] = new() { "国", "中" },
                ["ab"] = new() { "丙" }, ["ba"] = new() { "丁" }, ["aaa"] = new() { "甲乙" },
                ["aab"] = new() { "国" }, ["aabb"] = new() { "甲乙", "乙甲" }
            };
            foreach (bool duplicates in new[] { false, true })
            foreach (int beam in new[] { 1, 7, 32 })
            {
                using var incremental = ReviewDecoder(lex, model, beam, duplicates);
                using var full = ReviewDecoder(lex, model, beam, duplicates);
                var random = new Random(7021 + beam);
                string raw = "";
                for (int i = 0; i < 192; i++)
                {
                    if (raw.Length > 30 || (raw.Length > 0 && random.Next(4) == 0)) raw = raw[..random.Next(raw.Length)];
                    else if (raw.Length > 2 && random.Next(6) == 0) raw = raw[..random.Next(raw.Length)] + "ab";
                    else raw += new[] { "a", "b", "aa", "bb", "ab", "ba", "aaa", ";", "12", "'" }[random.Next(10)];
                    ReviewSame(incremental.Decode(raw, 5, true), full.DecodeFull(raw, 5, true));
                    ReviewSame(incremental.Decode(raw, 20, false), full.DecodeFull(raw, 20, false));
                }
            }
        }
    }
}
