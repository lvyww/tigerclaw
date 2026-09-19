using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceNgramModel : ISentenceBoundaryLanguageModel, IDisposable
    {
        private const string Magic = "TCSKNM01";
        private const int Version = 1;
        private const int ScalarBits = 21;
        private const int ScalarMask = (1 << ScalarBits) - 1;
        private const long MaximumModelLength = 4L * 1024 * 1024 * 1024;
        private const int LogProbabilityCacheSize = 1 << 18;
        private const int ObservedBigramCacheSize = 1 << 16;
        private const ulong NoUnigramCacheKeyFlag = 1UL << 63;

        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly long _length;
        private readonly long _unigramOffset;
        private readonly int _unigramCount;
        private readonly long _bigramOffset;
        private readonly long _bigramCount;
        private readonly long _bigramContextOffset;
        private readonly int _bigramContextCount;
        private readonly long _trigramOffset;
        private readonly long _trigramCount;
        private readonly long _trigramContextOffset;
        private readonly long _trigramContextCount;
        private readonly float _unknownProbability;
        private readonly MobileLayout _mobile;
        private bool _disposed;

        private SentenceNgramModel(MemoryMappedFile mapping, long length)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
            if (length <= 0 || length > MaximumModelLength)
            {
                throw new InvalidDataException("Invalid sentence n-gram V2 model length.");
            }

            _length = length;
            _view = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            try
            {
                long position = 0;
                string magic = ReadAscii(ref position, Magic.Length);
                if (string.Equals(magic, "TCSKNM02", StringComparison.Ordinal))
                {
                    _mobile = new MobileLayout(_view, _length);
                    _unigramCount = _mobile.UnigramCount;
                    _unigramOffset = _mobile.UnigramOffset;
                    position = _length;
                }
                else
                {
                    if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Invalid sentence n-gram V2 model magic.");
                    }
                    if (ReadInt32(ref position) != Version)
                    {
                        throw new InvalidDataException("Unsupported sentence n-gram V2 model version.");
                    }

                    _unigramCount = ReadNonNegativeInt32(ref position, "unigram");
                    _unigramOffset = ReserveSection(ref position, _unigramCount, 8, "unigram");

                    _bigramCount = ReadNonNegativeInt64(ref position, "bigram");
                    _bigramOffset = ReserveSection(ref position, _bigramCount, 12, "bigram");

                    _bigramContextCount = ReadNonNegativeInt32(ref position, "bigram context");
                    _bigramContextOffset = ReserveSection(
                        ref position,
                        _bigramContextCount,
                        8,
                        "bigram context");

                    _trigramCount = ReadNonNegativeInt64(ref position, "trigram");
                    _trigramOffset = ReserveSection(ref position, _trigramCount, 12, "trigram");

                    _trigramContextCount = ReadNonNegativeInt64(ref position, "trigram context");
                    _trigramContextOffset = ReserveSection(
                        ref position,
                        _trigramContextCount,
                        12,
                        "trigram context");
                }

                if (position != _length || _unigramCount == 0)
                {
                    throw new InvalidDataException("Sentence n-gram V2 model has invalid trailing data.");
                }

                _unknownProbability = LookupInt32(
                    _unigramOffset,
                    _unigramCount,
                    0,
                    0.0f);
                if (!IsProbability(_unknownProbability) || _unknownProbability <= 0.0f)
                {
                    throw new InvalidDataException("Sentence n-gram V2 model has no unknown probability.");
                }
            }
            catch
            {
                _view.Dispose();
                throw;
            }
        }

        public static SentenceNgramModel LoadAvailable(string baseDirectory)
        {
            foreach (string path in CandidatePaths(baseDirectory))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    return Load(path);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Failed to load sentence n-gram V2 model '{0}': {1}", path, ex.Message);
                }
            }

            return null;
        }

        public static SentenceNgramModel Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Sentence n-gram V2 model path is required.", nameof(path));
            }

            MemoryMappedFile mapping = null;
            try
            {
                long length;
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                try
                {
                    length = stream.Length;
                    mapping = MemoryMappedFile.CreateFromFile(
                        stream,
                        null,
                        0,
                        MemoryMappedFileAccess.Read,
                        HandleInheritability.None,
                        false);
                    stream = null;
                }
                finally
                {
                    stream?.Dispose();
                }

                var model = new SentenceNgramModel(mapping, length);
                mapping = null;
                return model;
            }
            finally
            {
                mapping?.Dispose();
            }
        }

        public double LogProbability(string previous2, string previous1, string target)
        {
            return LogProbability(previous2, previous1, target, includeUnigram: true);
        }

        private double ComputeLogProbability(string previous2, string previous1, string target, bool includeUnigram, QueryCache cache)
        {
            int first = ResolveScalar(previous2);
            int second = ResolveScalar(previous1);
            int third = ResolveScalar(target);
            double unigram = includeUnigram
                ? LookupInt32(
                    _unigramOffset,
                    _unigramCount,
                    third,
                    _unknownProbability)
                : 0.0;
            if (_mobile != null)
            {
                var bi = _mobile.Lookup(false, (ulong)second, third, cache.MobileContexts(false));
                double bigramScore = bi.Probability;
                bigramScore += (double)bi.Lambda * unigram;
                var tri = _mobile.Lookup(true, PackPair(first, second), third, cache.MobileContexts(true));
                double trigramScore = tri.Probability;
                trigramScore += (double)tri.Lambda * bigramScore;
                return Math.Log(Math.Max(trigramScore, 1e-300));
            }
            double bigram = LookupUInt64(
                _bigramOffset,
                _bigramCount,
                PackPair(second, third),
                0.0f);
            double bigramLambda = LookupInt32(
                _bigramContextOffset,
                _bigramContextCount,
                second,
                1.0f);
            bigram += bigramLambda * unigram;

            ulong context = PackPair(first, second);
            double trigram = LookupUInt64(
                _trigramOffset,
                _trigramCount,
                PackTriple(first, second, third),
                0.0f);
            double trigramLambda = LookupUInt64(
                _trigramContextOffset,
                _trigramContextCount,
                context,
                1.0f);
            trigram += trigramLambda * bigram;
            double result = Math.Log(Math.Max(trigram, 1e-300));
            return result;
        }

        private static int ResolveScalar(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return 0;
            }
            if (token.Length == 1)
            {
                return token[0];
            }
            if (token.Length == 2 && char.IsSurrogatePair(token, 0))
            {
                return char.ConvertToUtf32(token, 0);
            }
            return 0;
        }

        private static IEnumerable<string> CandidatePaths(string baseDirectory)
        {
            string root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            yield return Path.Combine(root, "Models", "sentence-ngram-mobile.bin");
            yield return Path.Combine(root, "sentence-ngram-mobile.bin");
            yield return Path.Combine(root, "Models", "sentence-ngram-v2.bin");
            yield return Path.Combine(root, "sentence-ngram-v2.bin");
        }

        private string ReadAscii(ref long position, int count)
        {
            EnsureAvailable(position, count, "header");
            var bytes = new byte[count];
            _view.ReadArray(position, bytes, 0, bytes.Length);
            position += count;
            return Encoding.ASCII.GetString(bytes);
        }

        private int ReadInt32(ref long position)
        {
            EnsureAvailable(position, 4, "header");
            int value = _view.ReadInt32(position);
            position += 4;
            return value;
        }

        private long ReadInt64(ref long position)
        {
            EnsureAvailable(position, 8, "header");
            long value = _view.ReadInt64(position);
            position += 8;
            return value;
        }

        private int ReadNonNegativeInt32(ref long position, string name)
        {
            int value = ReadInt32(ref position);
            if (value < 0)
            {
                throw new InvalidDataException("Invalid " + name + " count.");
            }
            return value;
        }

        private long ReadNonNegativeInt64(ref long position, string name)
        {
            long value = ReadInt64(ref position);
            if (value < 0)
            {
                throw new InvalidDataException("Invalid " + name + " count.");
            }
            return value;
        }

        private long ReserveSection(ref long position, long count, int recordSize, string name)
        {
            if (count > (_length - position) / recordSize)
            {
                throw new InvalidDataException("Invalid " + name + " section length.");
            }
            long offset = position;
            position += count * recordSize;
            return offset;
        }

        private void EnsureAvailable(long position, long count, string name)
        {
            if (position < 0 || count < 0 || position > _length - count)
            {
                throw new InvalidDataException("Truncated sentence n-gram V2 " + name + ".");
            }
        }

        private float LookupInt32(long offset, int count, int key, float fallback)
        {
            int low = 0;
            int high = count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                int value = _view.ReadInt32(offset + (long)middle * 8);
                if (value < key)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (low >= count)
            {
                return fallback;
            }
            long position = offset + (long)low * 8;
            return _view.ReadInt32(position) == key
                ? _view.ReadSingle(position + 4)
                : fallback;
        }

        private float LookupUInt64(long offset, long count, ulong key, float fallback)
        {
            long low = 0;
            long high = count;
            while (low < high)
            {
                long middle = low + ((high - low) / 2);
                ulong value = _view.ReadUInt64(offset + middle * 12);
                if (value < key)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (low >= count)
            {
                return fallback;
            }
            long position = offset + low * 12;
            return _view.ReadUInt64(position) == key
                ? _view.ReadSingle(position + 8)
                : fallback;
        }

        private bool ContainsUInt64(long offset, long count, ulong key)
        {
            long low = 0;
            long high = count;
            while (low < high)
            {
                long middle = low + ((high - low) / 2);
                ulong value = _view.ReadUInt64(offset + middle * 12);
                if (value < key)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (low >= count)
            {
                return false;
            }

            return _view.ReadUInt64(offset + low * 12) == key;
        }

        private static bool IsProbability(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0.0f && value <= 1.0f;
        }

        private static ulong PackPair(int first, int second)
        {
            return ((ulong)(uint)first << ScalarBits) | ((uint)second & ScalarMask);
        }

        private static ulong PackTriple(int first, int second, int third)
        {
            return ((ulong)(uint)first << (ScalarBits * 2)) |
                   ((ulong)(uint)second << ScalarBits) |
                   ((uint)third & ScalarMask);
        }

        private sealed class FixedSizeCache<T>
        {
            private readonly Entry[] _entries;
            private readonly int _mask;

            internal FixedSizeCache(int size)
            {
                if (size <= 0 || (size & (size - 1)) != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(size));
                }

                _entries = new Entry[size];
                _mask = size - 1;
            }

            internal bool TryGetValue(ulong key, out T value)
            {
                Entry entry = _entries[GetIndex(key)];
                if (entry.Occupied && entry.Key == key)
                {
                    value = entry.Value;
                    return true;
                }

                value = default(T);
                return false;
            }

            internal void Set(ulong key, T value)
            {
                // The owning query session serializes access. A different
                // decoder owns a different cache, while mapped data is shared.
                _entries[GetIndex(key)] = new Entry
                {
                    Key = key,
                    Value = value,
                    Occupied = true
                };
            }

            private int GetIndex(ulong key)
            {
                unchecked
                {
                    key ^= key >> 33;
                    key *= 0xff51afd7ed558ccdUL;
                    key ^= key >> 33;
                    key *= 0xc4ceb9fe1a85ec53UL;
                    key ^= key >> 33;
                    return (int)key & _mask;
                }
            }

            private struct Entry
            {
                internal ulong Key;
                internal T Value;
                internal bool Occupied;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SentenceNgramModel));
            }
        }
    }
}
