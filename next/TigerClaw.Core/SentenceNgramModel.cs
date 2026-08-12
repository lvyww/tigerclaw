using System;
using System.Collections.Generic;
using System.IO;

namespace TigerClaw.Core
{
    internal sealed class SentenceNgramModel : ISentenceLanguageModel
    {
        private const string Magic = "TCSNGRM1";
        private readonly Dictionary<string, int> _tokenIds;
        private readonly long[] _unigramCounts;
        private readonly long _unigramTotal;
        private readonly ulong[] _bigramKeys;
        private readonly int[] _bigramCounts;
        private readonly long[] _bigramContexts;
        private readonly ulong[] _trigramKeys;
        private readonly int[] _trigramCounts;
        private readonly ulong[] _trigramContextKeys;
        private readonly long[] _trigramContextCounts;

        private SentenceNgramModel(
            Dictionary<string, int> tokenIds,
            long[] unigramCounts,
            long unigramTotal,
            ulong[] bigramKeys,
            int[] bigramCounts,
            long[] bigramContexts,
            ulong[] trigramKeys,
            int[] trigramCounts,
            ulong[] trigramContextKeys,
            long[] trigramContextCounts)
        {
            _tokenIds = tokenIds;
            _unigramCounts = unigramCounts;
            _unigramTotal = unigramTotal;
            _bigramKeys = bigramKeys;
            _bigramCounts = bigramCounts;
            _bigramContexts = bigramContexts;
            _trigramKeys = trigramKeys;
            _trigramCounts = trigramCounts;
            _trigramContextKeys = trigramContextKeys;
            _trigramContextCounts = trigramContextCounts;
        }

        public static ISentenceLanguageModel LoadOrNeutral(string baseDirectory)
        {
            foreach (string path in CandidatePaths(baseDirectory))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return Load(path);
                    }
                }
                catch
                {
                }
            }

            return NeutralSentenceLanguageModel.Instance;
        }

        public static SentenceNgramModel Load(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false))
            {
                string magic = new string(reader.ReadChars(Magic.Length));
                if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Invalid sentence n-gram model magic.");
                }

                int version = reader.ReadInt32();
                if (version != 1)
                {
                    throw new InvalidDataException("Unsupported sentence n-gram model version.");
                }

                int tokenCount = ReadCount(reader, "token");
                var tokenIds = new Dictionary<string, int>(tokenCount, StringComparer.Ordinal);
                for (int i = 0; i < tokenCount; i++)
                {
                    string token = reader.ReadString();
                    tokenIds[token] = i;
                }

                var unigramCounts = new long[tokenCount];
                long unigramTotal = 0;
                for (int i = 0; i < tokenCount; i++)
                {
                    long count = reader.ReadInt64();
                    unigramCounts[i] = count;
                    unigramTotal += count;
                }

                int bigramCount = ReadCount(reader, "bigram");
                ulong[] bigramKeys = ReadUInt64Array(reader, bigramCount);
                int[] bigramCounts = ReadInt32Array(reader, bigramCount);
                long[] bigramContexts = ReadInt64Array(reader, tokenCount);

                int trigramCount = ReadCount(reader, "trigram");
                ulong[] trigramKeys = ReadUInt64Array(reader, trigramCount);
                int[] trigramCounts = ReadInt32Array(reader, trigramCount);

                int trigramContextCount = ReadCount(reader, "trigram context");
                ulong[] trigramContextKeys = ReadUInt64Array(reader, trigramContextCount);
                long[] trigramContextCounts = ReadInt64Array(reader, trigramContextCount);

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Sentence n-gram model has trailing data.");
                }

                return new SentenceNgramModel(
                    tokenIds,
                    unigramCounts,
                    unigramTotal,
                    bigramKeys,
                    bigramCounts,
                    bigramContexts,
                    trigramKeys,
                    trigramCounts,
                    trigramContextKeys,
                    trigramContextCounts);
            }
        }

        public double LogProbability(string previous2, string previous1, string target)
        {
            int targetId = ResolveToken(target);
            double vocabularySize = Math.Max(_unigramCounts.Length, 1);
            double unigramProbability = ((_unigramCounts[targetId]) + 0.1) /
                                        (_unigramTotal + 0.1 * vocabularySize);

            int previous1Id = ResolveToken(previous1);
            ulong bigramKey = PackPair(previous1Id, targetId);
            int bigramIndex = Array.BinarySearch(_bigramKeys, bigramKey);
            int bigramCount = bigramIndex >= 0 ? _bigramCounts[bigramIndex] : 0;
            double bigramProbability = (bigramCount + 5.0 * unigramProbability) /
                                        (_bigramContexts[previous1Id] + 5.0);

            int previous2Id = ResolveToken(previous2);
            ulong trigramKey = PackTriple(previous2Id, previous1Id, targetId);
            int trigramIndex = Array.BinarySearch(_trigramKeys, trigramKey);
            int trigramCount = trigramIndex >= 0 ? _trigramCounts[trigramIndex] : 0;
            ulong contextKey = PackPair(previous2Id, previous1Id);
            int contextIndex = Array.BinarySearch(_trigramContextKeys, contextKey);
            long contextCount = contextIndex >= 0 ? _trigramContextCounts[contextIndex] : 0;
            double trigramProbability = (trigramCount + 5.0 * bigramProbability) /
                                        (contextCount + 5.0);
            return Math.Log(Math.Max(trigramProbability, 1e-300));
        }

        private int ResolveToken(string token)
        {
            if (token != null && _tokenIds.TryGetValue(token, out int id))
            {
                return id;
            }

            return 0;
        }

        private static IEnumerable<string> CandidatePaths(string baseDirectory)
        {
            string root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            yield return Path.Combine(root, "Models", "sentence-ngram.bin");
            yield return Path.Combine(root, "sentence-ngram.bin");
        }

        private static int ReadCount(BinaryReader reader, string name)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000000)
            {
                throw new InvalidDataException("Invalid " + name + " count.");
            }

            return count;
        }

        private static ulong[] ReadUInt64Array(BinaryReader reader, int count)
        {
            var values = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadUInt64();
            }
            return values;
        }

        private static int[] ReadInt32Array(BinaryReader reader, int count)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt32();
            }
            return values;
        }

        private static long[] ReadInt64Array(BinaryReader reader, int count)
        {
            var values = new long[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt64();
            }
            return values;
        }

        private static ulong PackPair(int first, int second)
        {
            return ((ulong)(uint)first << 32) | (uint)second;
        }

        private static ulong PackTriple(int first, int second, int third)
        {
            const int bits = 21;
            return (uint)first | ((ulong)(uint)second << bits) | ((ulong)(uint)third << (bits * 2));
        }
    }
}
