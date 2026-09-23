using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace TigerClaw.Core
{
    // TCSKNM03 v1/Q16 and v2/Q8. Pages remain OS-managed; query-local caches
    // retain only offsets and scores. No KenLM DLL or second model is needed.
    internal sealed unsafe class SentenceFivegramModel : ISentenceLanguageModel, IDisposable
    {
        internal const string FileName = "sentence-fivegram-mobile.bin";
        private readonly object _gate = new();
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private byte* _pointer;
        private readonly long _length;
        private readonly int _quantBytes, _stride;
        private readonly ushort _bos, _eos, _unknown;
        private readonly Dictionary<string, ushort> _tokens = new(StringComparer.Ordinal);
        private readonly double[] _unigrams;
        private readonly Quant[] _quant = new Quant[6];
        private readonly Bucket[,] _buckets = new Bucket[6, 256];
        private int _leases;
        private bool _disposed, _closed;
        private static readonly double Ln10 = Math.Log(10);
        private readonly record struct Quant(double Minimum, double Step, double BackoffMinimum, double BackoffStep);
        private readonly record struct Bucket(long Start, long End, long Index, int IndexCount, uint BlockCount);
        private readonly record struct Block(long Successors, int Count, double Backoff);
        internal int Version { get; }
        internal bool MappingClosed { get { lock (_gate) return _closed; } }

        internal SentenceFivegramModel(string path)
        {
            _length = new FileInfo(path).Length;
            if (_length < 256 || _length > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid TCS fivegram length");
            _mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            try
            {
                _view = _mapping.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _pointer = pointer + _view.PointerOffset;
                if (Encoding.ASCII.GetString(Span(0, 8)) != "TCSKNM03") Invalid("magic");
                Version = (int)U32(8);
                if ((Version != 1 && Version != 2) || U32(12) != 256 || Offset(16) != _length || U32(24) != 5 || U32(32) != 256)
                    Invalid("header");
                _quantBytes = Version == 2 ? 1 : 2;
                uint vocabulary = U32(28);
                if (vocabulary == 0 || vocabulary > 65536 || U32(36) == 0 || U32(36) > 65536) Invalid("vocabulary/stride");
                _stride = (int)U32(36);
                _unknown = U16(56); _bos = U16(58); _eos = U16(60);
                if (_unknown >= vocabulary || _bos >= vocabulary || _eos >= vocabulary) Invalid("special tokens");
                for (int order = 1; order <= 5; order++)
                {
                    long at = 160 + (order - 1) * 16;
                    double scale = Version == 2 ? 1e9 : 1e12;
                    _quant[order] = new(I32(at) / 1e7, U32(at + 4) / scale, I32(at + 8) / 1e7, U32(at + 12) / scale);
                }
                _unigrams = new double[vocabulary];
                long pos = Offset(40), vocabBytes = Offset(48);
                Range(pos, vocabBytes);
                long vocabEnd = pos + vocabBytes;
                var utf8 = new UTF8Encoding(false, true);
                for (uint id = 0; id < vocabulary; id++)
                {
                    int count = U16(pos); pos += 2;
                    if (count == 0 || pos + count + 2 * _quantBytes > vocabEnd) Invalid("vocabulary record");
                    string token = utf8.GetString(Span(pos, count)); pos += count;
                    if (!_tokens.TryAdd(token, (ushort)id)) Invalid("duplicate token");
                    _unigrams[id] = Probability(1, Q(pos)); pos += 2 * _quantBytes;
                }
                if (pos != vocabEnd || !_tokens.TryGetValue("<s>", out ushort bos) || bos != _bos ||
                    !_tokens.TryGetValue("</s>", out ushort eos) || eos != _eos ||
                    !_tokens.TryGetValue("<unk>", out ushort unknown) || unknown != _unknown) Invalid("special vocabulary");
                for (int order = 2; order <= 5; order++)
                {
                    long section = 64 + (order - 2) * 24, directory = Offset(section);
                    Range(directory, 256 * 40);
                    ulong blocks = 0, records = 0;
                    for (int bucket = 0; bucket < 256; bucket++)
                    {
                        long at = directory + bucket * 40;
                        long start = Offset(at), bytes = Offset(at + 8), index = Offset(at + 16);
                        uint indices = U32(at + 24), count = U32(at + 28);
                        if (indices != (count + (ulong)_stride - 1) / (ulong)_stride || indices > int.MaxValue) Invalid("index count");
                        Range(start, bytes); Range(index, (long)indices * 16);
                        if (index != start + bytes) Invalid("block/index boundary");
                        long previous = -1;
                        for (int i = 0; i < indices; i++)
                        {
                            long ip = index + i * 16, offset = Offset(ip + 8);
                            if (offset < start || offset >= index || offset <= previous || (i == 0 && offset != start)) Invalid("index offset");
                            for (int j = 0; j < order - 1; j++)
                            {
                                ushort id = U16(ip + j * 2);
                                if (id >= vocabulary || (j == 0 && id % 256 != bucket) || U16(offset + j * 2) != id) Invalid("index key");
                            }
                            if (i > 0 && CompareKeys(ip - 16, ip, order - 1) >= 0) Invalid("index order");
                            previous = offset;
                        }
                        if (count == 0 && (bytes != 0 || U64(at + 32) != 0)) Invalid("empty bucket");
                        _buckets[order, bucket] = new(start, index, index, (int)indices, count);
                        blocks += count; records = checked(records + U64(at + 32));
                    }
                    if (blocks != U64(section + 8) || records != U64(section + 16)) Invalid("section totals");
                }
            }
            catch
            {
                CloseMapping();
                throw;
            }
        }

        internal static ISentenceLanguageModel LoadAvailable(string baseDirectory)
        {
            string root = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            foreach (string path in new[] { Path.Combine(root, "Models", FileName), Path.Combine(root, FileName) })
            {
                if (!File.Exists(path)) continue;
                try { return new SentenceFivegramModel(path); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Failed to load TCS fivegram '{0}': {1}", path, ex.Message); }
            }
            return null;
        }

        private static void Invalid(string field) => throw new InvalidDataException("Invalid TCS fivegram " + field);
        private void Range(long offset, long bytes)
        {
            if (offset < 0 || bytes < 0 || offset > _length || bytes > _length - offset) Invalid("range");
        }
        private ReadOnlySpan<byte> Span(long offset, int count) { Range(offset, count); return new(_pointer + offset, count); }
        private ushort U16(long p) => BinaryPrimitives.ReadUInt16LittleEndian(Span(p, 2));
        private uint U32(long p) => BinaryPrimitives.ReadUInt32LittleEndian(Span(p, 4));
        private int I32(long p) => BinaryPrimitives.ReadInt32LittleEndian(Span(p, 4));
        private ulong U64(long p) => BinaryPrimitives.ReadUInt64LittleEndian(Span(p, 8));
        private long Offset(long p) { ulong value = U64(p); if (value > (ulong)_length) Invalid("offset"); return (long)value; }
        private int Q(long p) => _quantBytes == 1 ? Span(p, 1)[0] : U16(p);
        private double Probability(int order, int q) => _quant[order].Minimum + q * _quant[order].Step;
        private double Backoff(int order, int q) => q == 0 ? 0 : _quant[order].BackoffMinimum + (q - 1) * _quant[order].BackoffStep;
        private static uint TokenAt(SentenceLmHistory h, int reverse) => reverse switch { 0 => h.A, 1 => h.B, 2 => h.C, _ => h.D };
        private int Compare(long at, SentenceLmHistory history, int length)
        {
            for (int i = 0; i < length; i++)
            {
                int compared = ((uint)U16(at + i * 2)).CompareTo(TokenAt(history, length - i - 1));
                if (compared != 0) return compared;
            }
            return 0;
        }
        private int CompareKeys(long a, long b, int length)
        {
            for (int i = 0; i < length; i++) { int c = U16(a + i * 2).CompareTo(U16(b + i * 2)); if (c != 0) return c; }
            return 0;
        }
        private Block FindBlock(SentenceLmHistory history, int length)
        {
            var bucket = _buckets[length + 1, TokenAt(history, length - 1) % 256];
            int lo = 0, hi = bucket.IndexCount;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (Compare(bucket.Index + mid * 16, history, length) <= 0) lo = mid + 1; else hi = mid;
            }
            if (lo == 0) return default;
            int entry = lo - 1;
            long pos = Offset(bucket.Index + entry * 16 + 8);
            long end = entry + 1 < bucket.IndexCount ? Offset(bucket.Index + (entry + 1) * 16 + 8) : bucket.End;
            int headerBytes = length * 2 + _quantBytes + 2;
            for (int i = 0; pos < end && i < _stride; i++)
            {
                if (end - pos < headerBytes) Invalid("block header");
                int compared = Compare(pos, history, length);
                int bow = Q(pos + length * 2), count = U16(pos + length * 2 + _quantBytes);
                long successors = pos + headerBytes, next = successors + (long)count * (2 + _quantBytes);
                if (next > end) Invalid("successors");
                if (compared == 0) return new(successors, count, Backoff(length, bow));
                if (compared > 0) return default;
                pos = next;
            }
            if (pos < end) Invalid("page block count");
            return default;
        }
        private bool FindProbability(Block block, ushort token, int order, out double probability)
        {
            int lo = 0, hi = block.Count, width = 2 + _quantBytes;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (U16(block.Successors + mid * width) < token) lo = mid + 1; else hi = mid;
            }
            if (lo < block.Count && U16(block.Successors + lo * width) == token)
            {
                probability = Probability(order, Q(block.Successors + lo * width + 2)); return true;
            }
            probability = 0; return false;
        }
        private bool TryToken(string value, out ushort id)
        {
            if (value == "\u0002") { id = _bos; return true; }
            if (value == "\u0003") { id = _eos; return true; }
            return _tokens.TryGetValue(value, out id);
        }
        internal QuerySession CreateQuerySession()
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); var session = new QuerySession(this); _leases++; return session; }
        }
        public double LogProbability(string a, string b, string c) => throw new InvalidOperationException("Fivegram search requires full history");
        public bool HasObservedBigram(string a, string b)
        {
            using var session = CreateQuerySession(); return session.HasObservedBigram(a, b);
        }
        public void Dispose()
        {
            lock (_gate) { _disposed = true; if (_leases == 0) CloseMapping(); }
            GC.SuppressFinalize(this);
        }
        ~SentenceFivegramModel() { CloseMapping(); }
        private void CloseMapping()
        {
            if (_closed) return;
            _closed = true;
            if (_pointer != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _pointer = null; }
            _view?.Dispose(); _mapping?.Dispose();
        }
        private void ReleaseLease() { lock (_gate) { _leases--; if (_disposed && _leases == 0) CloseMapping(); } }

        internal sealed class QuerySession : ISentenceHistoryLanguageModel, IDisposable
        {
            private readonly object _gate = new();
            private SentenceFivegramModel _model;
            private readonly Dictionary<SentenceLmHistory, Block> _contexts = new();
            private readonly Dictionary<(SentenceLmHistory, ushort), double> _scores = new();
            internal QuerySession(SentenceFivegramModel model) { _model = model; BeginHistory = new(model._bos, 0, 0, 0, 1); }
            public SentenceLmHistory BeginHistory { get; }
            private Block Context(SentenceLmHistory h, int n)
            {
                var key = new SentenceLmHistory(h.A, n >= 2 ? h.B : 0, n >= 3 ? h.C : 0, n >= 4 ? h.D : 0, n);
                if (_contexts.TryGetValue(key, out var block)) return block;
                block = _model.FindBlock(h, n);
                if (_contexts.Count >= 8192) _contexts.Clear();
                _contexts.Add(key, block); return block;
            }
            public double Step(SentenceLmHistory history, string target, out SentenceLmHistory next)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_model == null, this);
                    if (history.Count < 1 || history.Count > 4) throw new ArgumentOutOfRangeException(nameof(history));
                    ushort token = _model.TryToken(target, out ushort id) ? id : _model._unknown;
                    next = history.Append(token);
                    var key = (history, token);
                    if (_scores.TryGetValue(key, out double cached)) return cached;
                    double score = 0; bool found = false;
                    for (int n = history.Count; n > 0; n--)
                    {
                        var block = Context(history, n);
                        if (_model.FindProbability(block, token, n + 1, out double probability)) { score += probability; found = true; break; }
                        score += block.Backoff;
                    }
                    if (!found) score += _model._unigrams[token];
                    score *= Ln10;
                    if (_scores.Count >= 8192) _scores.Clear();
                    _scores.Add(key, score); return score;
                }
            }
            public bool HasObservedBigram(string a, string b)
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_model == null, this);
                    return _model.TryToken(a, out ushort x) && _model.TryToken(b, out ushort y) &&
                        _model.FindProbability(Context(new(x, 0, 0, 0, 1), 1), y, 2, out _);
                }
            }
            public double LogProbability(string a, string b, string c) => throw new InvalidOperationException("Fivegram search requires full history");
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_model == null) return;
                    var owner = _model; _model = null; _contexts.Clear(); _scores.Clear(); owner.ReleaseLease();
                }
                GC.SuppressFinalize(this);
            }
            ~QuerySession() { Dispose(); }
        }
    }
}
