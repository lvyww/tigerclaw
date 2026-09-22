using System;
using System.Linq;
using System.Threading;
using TigerClaw.Pinyin;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void RunPinyinRerankerTests()
        {
            var model = new PinyinTestModel();
            using var decoder = new TigerClaw.Pinyin.Decoder(PinyinTestLexicon(), model, 200);
            var original = decoder.Decode("nihaoma");
            using var identity = new PinyinReranker(tokens =>
            {
                string a = "\u0002", b = a; double value = 0;
                foreach (var t in tokens) { value += model.LogProbability(a, b, t); a = b; b = t; }
                return value + model.LogProbability(a, b, "\u0003");
            });
            var same = identity.Rank(original);
            True(same.Candidates.Select(c => c.Text).SequenceEqual(original.Candidates.Select(c => c.Text)), "rerank identity order");
            True(same.Candidates.Zip(original.Candidates).All(p => Math.Abs(p.First.Score - p.Second.Score) < 1e-10), "rerank identity scores");
            // A retained prefix must enter the full-state scorer exactly once.
            using var reverse = new PinyinReranker(tokens => tokens[0] == "泥/ni" ? 10 : -10);
            var ranked = reverse.Rank(original);
            Equal("泥好吗", ranked.Candidates[0].Text, "fivegram replaces LM ordering");
            var adjusted = original with { Candidates = original.Candidates.Select(c => c with { Segments = c.Segments.Select((s, i) => i == 0 ? s with { WordBonus = 9, SpellingPenalty = 2.5 } : s).ToArray() }).ToArray() };
            var withPriors = reverse.Rank(adjusted);
            True(withPriors.Candidates.Zip(ranked.Candidates).All(p => Math.Abs(p.First.Score - p.Second.Score - 6.5) < 1e-10), "user-word reward and spelling cost preserved");
            var chosen = ranked.Candidates[0];
            var locked = chosen with { Text = chosen.Segments[0].Text, Segments = chosen.Segments.Take(1).ToArray() };
            var continuation = decoder.Decode("nihaoma", prefix: locked);
            var reranked = reverse.Rank(continuation);
            True(reranked.Candidates.All(c => c.Text.StartsWith(locked.Text)), "rerank respects locked prefix");
            True(Math.Abs(reranked.Candidates[0].Score - chosen.Score) < 1e-10, "prefix score is recomputed once");
            using var failure = new PinyinReranker(_ => double.NaN);
            True(ReferenceEquals(original, failure.Rank(original)) && failure.Error != null, "atomic nonfinite fallback");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool canceled = false;
            try { reverse.Rank(original, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            True(canceled && reverse.Error == null, "cancellation does not disable model");
            var session = new PinyinSession(); session.Reset("nihaoma");
            session.Apply(session.Generation, ranked, (_, text) => text == "你好吗" ? 100 : 0);
            Equal("你好吗", session.Sentences[0].Text, "learning follows fivegram scoring");
        }
    }
}
