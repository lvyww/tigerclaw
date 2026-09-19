using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Independent fixture writer: repack a small legacy image without rounding floats.
        private static void ReviewMobileFixture(string legacy, string mobile)
        {
            using var reader = new BinaryReader(File.OpenRead(legacy));
            reader.ReadBytes(12);
            int uniCount = reader.ReadInt32();
            byte[] unigrams = reader.ReadBytes(uniCount * 8);
            var sections = new List<(List<(ulong, float)> Entries, List<(ulong, float)> Contexts)>();
            for (int order = 0; order < 2; order++)
            {
                long entries = reader.ReadInt64();
                var values = new List<(ulong, float)>();
                for (long i = 0; i < entries; i++) values.Add((reader.ReadUInt64(), reader.ReadSingle()));
                long contexts = order == 0 ? reader.ReadInt32() : reader.ReadInt64();
                var lambdas = new List<(ulong, float)>();
                for (long i = 0; i < contexts; i++) lambdas.Add((order == 0 ? reader.ReadUInt32() : reader.ReadUInt64(), reader.ReadSingle()));
                sections.Add((values, lambdas));
            }
            using var writer = new BinaryWriter(File.Create(mobile));
            writer.Write(new byte[104]); writer.Write(unigrams);
            var offsets = new List<(long Blocks, long Index, int IndexCount)>();
            foreach (var section in sections)
            {
                long blocks = writer.BaseStream.Position;
                var index = new List<(ulong, long)>();
                int entry = 0;
                for (int i = 0; i < section.Contexts.Count; i++)
                {
                    var (key, lambda) = section.Contexts[i];
                    if (i % 16 == 0) index.Add((key, writer.BaseStream.Position));
                    int start = entry;
                    while (entry < section.Entries.Count && section.Entries[entry].Item1 >> 21 == key) entry++;
                    writer.Write(key); writer.Write(lambda); writer.Write(entry - start);
                    for (int n = start; n < entry; n++)
                    {
                        writer.Write((uint)(section.Entries[n].Item1 & 0x1fffff)); writer.Write(section.Entries[n].Item2);
                    }
                }
                Review(entry == section.Entries.Count, "fixture contexts cover all entries");
                long at = writer.BaseStream.Position;
                foreach (var (key, position) in index) { writer.Write(key); writer.Write(position); }
                offsets.Add((blocks, at, index.Count));
            }
            long size = writer.BaseStream.Position;
            writer.BaseStream.Position = 0;
            writer.Write(Encoding.ASCII.GetBytes("TCSKNM02")); writer.Write(1); writer.Write(104);
            writer.Write(size); writer.Write(16); writer.Write(0); writer.Write(uniCount); writer.Write(0); writer.Write(104L);
            writer.Write(sections[0].Contexts.Count); writer.Write(offsets[0].IndexCount);
            writer.Write(offsets[0].Blocks); writer.Write(offsets[0].Index);
            writer.Write((long)sections[1].Contexts.Count); writer.Write(offsets[1].IndexCount); writer.Write(0);
            writer.Write(offsets[1].Blocks); writer.Write(offsets[1].Index);
        }

        private static void ReviewMobileModels(string folder)
        {
            string legacy = Path.Combine(folder, "mobile-reference.bin"), mobile = Path.Combine(folder, "mobile-test.bin");
            ReviewWriteModel(legacy); ReviewMobileFixture(legacy, mobile);
            using var a = SentenceNgramModel.Load(legacy);
            using var b = SentenceNgramModel.Load(mobile);
            ReviewFormatQueries(a, b);
            ReviewFuzz(b);
            using var retained = b.CreateQuerySession();
            b.Dispose();
            Review(!b.MappingClosed, "mobile mapping leased after owner disposal");
            ReviewNumber(retained.LogProbability("\0", "\0", "\0"), a.LogProbability("\0", "\0", "\0"), "mobile retired query");
            retained.Dispose(); Review(b.MappingClosed, "mobile last lease closes mapping");

            // Empty contexts are meaningful even without successors; missing contexts use lambda=1.
            string emptyLegacy = Path.Combine(folder, "empty-context.bin"), emptyMobile = Path.Combine(folder, "empty-mobile.bin");
            using (var writer = new BinaryWriter(File.Create(emptyLegacy)))
            {
                writer.Write(Encoding.ASCII.GetBytes("TCSKNM01")); writer.Write(1);
                writer.Write(2); writer.Write(0); writer.Write(.01f); writer.Write(0x20000); writer.Write(.02f);
                writer.Write(1L); writer.Write(ReviewPair(2, 3)); writer.Write(0f);
                writer.Write(2); writer.Write(2); writer.Write(.5f); writer.Write(3); writer.Write(.3f);
                writer.Write(0L); writer.Write(1L); writer.Write(ReviewPair(2, 3)); writer.Write(.25f);
            }
            ReviewMobileFixture(emptyLegacy, emptyMobile);
            using var x = SentenceNgramModel.Load(emptyLegacy);
            using var y = SentenceNgramModel.Load(emptyMobile);
            ReviewFormatQueries(x, y);
            Review(y.HasObservedBigram("\u0002", "\u0003"), "mobile zero-valued observation");
            Review(!y.HasObservedBigram("\u0003", "\u0002"), "mobile missing observation");

            string unigramLegacy = Path.Combine(folder, "unigram-only.bin"), unigramMobile = Path.Combine(folder, "unigram-only-mobile.bin");
            using (var writer = new BinaryWriter(File.Create(unigramLegacy)))
            {
                writer.Write(Encoding.ASCII.GetBytes("TCSKNM01")); writer.Write(1);
                writer.Write(1); writer.Write(0); writer.Write(.01f);
                writer.Write(0L); writer.Write(0); writer.Write(0L); writer.Write(0L);
            }
            ReviewMobileFixture(unigramLegacy, unigramMobile);
            using (var u = SentenceNgramModel.Load(unigramLegacy))
            using (var v = SentenceNgramModel.Load(unigramMobile)) ReviewFormatQueries(u, v);

            string available = Path.Combine(folder, "available"); Directory.CreateDirectory(Path.Combine(available, "Models"));
            string preferred = Path.Combine(available, "Models", "sentence-ngram-mobile.bin");
            File.Copy(emptyMobile, preferred); File.Copy(legacy, Path.Combine(available, "sentence-ngram-v2.bin"));
            using (var selected = SentenceNgramModel.LoadAvailable(available))
                ReviewNumber(selected.LogProbability("\u0002", "\u0003", "\0"), y.LogProbability("\u0002", "\u0003", "\0"), "mobile preferred over legacy");
            File.WriteAllBytes(preferred, new byte[12]);
            using (var selected = SentenceNgramModel.LoadAvailable(available))
                ReviewNumber(selected.LogProbability("\u0002", "\u0003", "\0"), a.LogProbability("\u0002", "\u0003", "\0"), "bad mobile falls back to legacy");

            byte[] valid = File.ReadAllBytes(mobile);
            foreach (int offset in new[] { 8, 12, 16, 24, 28, 32, 40, 48, 52, 56, 64, 72, 80, 84, 88, 96 })
            {
                var invalid = (byte[])valid.Clone(); Array.Fill(invalid, (byte)255, offset, 4);
                ReviewBadMobile(folder, invalid, false);
            }
            ReviewBadMobile(folder, valid[..^1], false);
            var badIndex = (byte[])valid.Clone(); long indexAt = BitConverter.ToInt64(valid, 64);
            Array.Copy(BitConverter.GetBytes(long.MaxValue), 0, badIndex, indexAt + 8, 8);
            ReviewBadMobile(folder, badIndex, false);
            var badContext = File.ReadAllBytes(emptyMobile); long blocksAt = BitConverter.ToInt64(badContext, 56);
            Array.Copy(BitConverter.GetBytes(uint.MaxValue), 0, badContext, blocksAt + 12, 4);
            ReviewBadMobile(folder, badContext, true);
            foreach (float invalidFloat in new[] { float.NaN, float.PositiveInfinity, -1f })
            {
                var invalid = File.ReadAllBytes(emptyMobile);
                Array.Copy(BitConverter.GetBytes(invalidFloat), 0, invalid, blocksAt + 8, 4);
                ReviewBadMobile(folder, invalid, true);
                invalid = File.ReadAllBytes(emptyMobile);
                Array.Copy(BitConverter.GetBytes(invalidFloat), 0, invalid, blocksAt + 20, 4);
                ReviewBadMobile(folder, invalid, true);
            }
        }

        private static void ReviewBadMobile(string folder, byte[] bytes, bool query)
        {
            string path = Path.Combine(folder, "bad-mobile.bin"); File.WriteAllBytes(path, bytes);
            bool rejected = false;
            try
            {
                using var model = SentenceNgramModel.Load(path);
                if (query) model.LogProbability("\u0002", "\u0002", "\u0003");
            }
            catch (InvalidDataException) { rejected = true; }
            Review(rejected, "malformed mobile rejected");
            // Load failure must not leak the file/mapping handle.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        private static void ReviewFormatQueries(SentenceNgramModel a, SentenceNgramModel b)
        {
            int[] tokens = ReviewTokens.Concat(new[] { 0x10ffff, 0x30000 }).ToArray();
            Parallel.For(0, 4, worker =>
            {
                using var sa = a.CreateQuerySession(); using var sb = b.CreateQuerySession();
                foreach (int first in tokens) foreach (int second in tokens) foreach (int third in tokens)
                {
                    string x = char.ConvertFromUtf32(first), y = char.ConvertFromUtf32(second), z = char.ConvertFromUtf32(third);
                    foreach (bool unigram in new[] { false, true })
                        ReviewNumber(sa.LogProbability(x, y, z, unigram), sb.LogProbability(x, y, z, unigram), "mobile bit-exact score");
                    Review(sa.HasObservedBigram(y, z) == sb.HasObservedBigram(y, z), "mobile observed parity");
                    ReviewNumber(b.LogProbability(x, y, z), a.LogProbability(x, y, z), "mobile direct query parity");
                }
            });
        }
    }
}
