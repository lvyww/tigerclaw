using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TigerClaw.Core
{
    // Compact final-stage word-existence evidence. The immutable TCSLEX01
    // Bloom filter stores no words or frequencies and is consulted only for
    // already-ranked display candidates.
    internal sealed class SentenceLexicalPrior
    {
        internal const string ResourceName = "TigerClaw.Core.Data.sentence_lexical.bin";
        private const int HeaderSize = 32;
        private const ulong Modulus = 4294967291UL;
        private readonly byte[] _bits;

        private SentenceLexicalPrior(byte[] bits, uint bitCount, int hashCount,
            int entryCount, int minimumLength, int maximumLength)
        {
            _bits = bits;
            BitCount = bitCount;
            HashCount = hashCount;
            EntryCount = entryCount;
            MinimumLength = minimumLength;
            MaximumLength = maximumLength;
        }

        public uint BitCount { get; }
        public int HashCount { get; }
        public int EntryCount { get; }
        public int MinimumLength { get; }
        public int MaximumLength { get; }
        public int ByteCount => HeaderSize + (_bits?.Length ?? 0);
        public bool IsEmpty => _bits == null || _bits.Length == 0;

        private static readonly Lazy<SentenceLexicalPrior> Embedded =
            new Lazy<SentenceLexicalPrior>(LoadEmbeddedCore, true);

        public static SentenceLexicalPrior LoadEmbedded() => Embedded.Value;

        internal static SentenceLexicalPrior Load(byte[] data)
        {
            if (data == null || data.Length < HeaderSize ||
                Encoding.ASCII.GetString(data, 0, 8) != "TCSLEX01")
            {
                throw new InvalidDataException("Not a TCSLEX01 lexical prior.");
            }

            uint version = ReadUInt32(data, 8);
            uint entries = ReadUInt32(data, 12);
            uint bitCount = ReadUInt32(data, 16);
            uint hashCount = ReadUInt32(data, 20);
            uint minimumLength = ReadUInt32(data, 24);
            uint maximumLength = ReadUInt32(data, 28);
            if (version != 1 || entries == 0 || bitCount < 8 || bitCount % 8 != 0 ||
                hashCount == 0 || hashCount > 32 || minimumLength < 2 ||
                maximumLength < minimumLength || maximumLength > 16 ||
                data.Length != HeaderSize + bitCount / 8)
            {
                throw new InvalidDataException("Invalid TCSLEX01 lexical prior header.");
            }

            var bits = new byte[checked((int)(bitCount / 8))];
            Buffer.BlockCopy(data, HeaderSize, bits, 0, bits.Length);
            return new SentenceLexicalPrior(bits, bitCount, checked((int)hashCount),
                checked((int)entries), checked((int)minimumLength), checked((int)maximumLength));
        }

        internal bool Contains(string text, IDictionary<string, bool> cache = null)
        {
            if (IsEmpty || string.IsNullOrEmpty(text)) return false;
            if (cache != null && cache.TryGetValue(text, out bool cached)) return cached;

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            ulong first = 2166136261UL;
            ulong second = 16777619UL;
            foreach (byte value in bytes)
            {
                first = (first * 131UL + value + 17UL) % Modulus;
                second = (second * 137UL + value + 53UL) % Modulus;
            }
            if (second == 0) second = 1;

            bool found = true;
            for (int index = 0; index < HashCount; index++)
            {
                ulong bit = (first + (ulong)index * second +
                    (ulong)index * (ulong)index * 97UL) % BitCount;
                if ((_bits[checked((int)(bit / 8))] & (1 << (int)(bit % 8))) == 0)
                {
                    found = false;
                    break;
                }
            }
            if (cache != null) cache[text] = found;
            return found;
        }

        internal double Score(string text, IDictionary<string, bool> lookupCache = null)
        {
            if (IsEmpty || string.IsNullOrEmpty(text)) return 0.0;
            string[] elements = SplitTextElements(text);
            var best = new double[elements.Length + 1];
            var builder = new StringBuilder();
            for (int finish = 1; finish <= elements.Length; finish++)
            {
                best[finish] = best[finish - 1];
                for (int length = MinimumLength; length <= MaximumLength; length++)
                {
                    int start = finish - length;
                    if (start < 0) break;
                    builder.Clear();
                    for (int index = start; index < finish; index++) builder.Append(elements[index]);
                    if (!Contains(builder.ToString(), lookupCache)) continue;
                    double value = best[start] + 1.0 + 0.2 * (length - 2);
                    if (value > best[finish]) best[finish] = value;
                }
            }
            return best[elements.Length];
        }

        private static SentenceLexicalPrior LoadEmbeddedCore()
        {
            Assembly assembly = typeof(SentenceLexicalPrior).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
                throw new InvalidDataException("Missing embedded sentence lexical prior.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Load(memory.ToArray());
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
        }

        private static string[] SplitTextElements(string text)
        {
            var elements = new List<string>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
            return elements.ToArray();
        }
    }
}
