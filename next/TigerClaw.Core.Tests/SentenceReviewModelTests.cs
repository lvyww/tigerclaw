using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static readonly int[] ReviewTokens = { 0, 2, 3, 0x4e00, 0x4e01, 0x4e2d, 0x4e59, 0x56fd, 0x7532, 0x20000 };
        private static ulong ReviewPair(int a, int b) => ((ulong)a << 21) | (uint)b;
        private static ulong ReviewTriple(int a, int b, int c) => ((ulong)a << 42) | ReviewPair(b, c);
        private static float ReviewUnigram(int a) => a == 0 ? 0.01f : 0.02f;
        private static float ReviewBigram(int a, int b) => (float)((a + b) % 5 * .001);
        private static float ReviewTrigram(int a, int b, int c) => (float)((a + b + c) % 7 * .0001);

        private static void ReviewWriteModel(string path)
        {
            using var writer = new BinaryWriter(File.Create(path));
            var tokens = ReviewTokens;
            writer.Write(Encoding.ASCII.GetBytes("TCSKNM01")); writer.Write(1);
            writer.Write(tokens.Length);
            foreach (int a in tokens) { writer.Write(a); writer.Write(ReviewUnigram(a)); }
            writer.Write((long)tokens.Length * tokens.Length);
            foreach (int a in tokens) foreach (int b in tokens) { writer.Write(ReviewPair(a, b)); writer.Write(ReviewBigram(a, b)); }
            writer.Write(tokens.Length);
            foreach (int a in tokens) { writer.Write(a); writer.Write(.7f); }
            writer.Write((long)tokens.Length * tokens.Length * tokens.Length);
            foreach (int a in tokens) foreach (int b in tokens) foreach (int c in tokens)
            { writer.Write(ReviewTriple(a, b, c)); writer.Write(ReviewTrigram(a, b, c)); }
            writer.Write((long)tokens.Length * tokens.Length);
            foreach (int a in tokens) foreach (int b in tokens) { writer.Write(ReviewPair(a, b)); writer.Write(.8f); }
        }
        private static double ReviewExpectedScore(int a, int b, int c, bool includeUnigram)
        {
            double bigram = ReviewBigram(b, c);
            bigram += (double).7f * (includeUnigram ? ReviewUnigram(c) : 0);
            double trigram = ReviewTrigram(a, b, c);
            trigram += (double).8f * bigram;
            return Math.Log(Math.Max(trigram, 1e-300));
        }

        private static void ReviewMappedModel(string folder, string productionModel)
        {
            string path = Path.Combine(folder, "synthetic-ngram.bin");
            ReviewWriteModel(path);
            using var model = SentenceNgramModel.Load(path);
            using var retained = model.CreateQuerySession();
            Parallel.For(0, 8, worker =>
            {
                using var session = model.CreateQuerySession();
                var random = new Random(1357 + worker);
                for (int i = 0; i < 4096; i++)
                {
                    int a = ReviewTokens[random.Next(ReviewTokens.Length)], b = ReviewTokens[random.Next(ReviewTokens.Length)], c = ReviewTokens[random.Next(ReviewTokens.Length)];
                    string x = char.ConvertFromUtf32(a), y = char.ConvertFromUtf32(b), z = char.ConvertFromUtf32(c);
                    bool unigram = i % 2 == 0;
                    ReviewNumber(session.LogProbability(x, y, z, unigram), ReviewExpectedScore(a, b, c, unigram), "concurrent mapped query");
                    Review(session.HasObservedBigram(x, y), "observed record independent of zero probability");
                    ReviewNumber(model.LogProbability(x, y, z, unigram), ReviewExpectedScore(a, b, c, unigram), "direct concurrent mapped query");
                }
            });
            ReviewFuzz(model);
            model.Dispose();
            Review(!model.MappingClosed, "retired mapping remains alive during lease");
            ReviewNumber(retained.LogProbability("\0", "\0", "\0"), ReviewExpectedScore(0, 0, 0, true), "old decoder lease remains readable");
            bool rejected = false;
            try { model.CreateQuerySession(); } catch (ObjectDisposedException) { rejected = true; }
            Review(rejected, "retired model rejects new leases");
            retained.Dispose();
            Review(model.MappingClosed, "last lease releases mapping");
            if (productionModel != null)
            {
                Review(File.Exists(productionModel), "explicit production model exists");
                using var production = SentenceNgramModel.Load(productionModel);
                ReviewFuzz(production);
            }
        }
    }
}
