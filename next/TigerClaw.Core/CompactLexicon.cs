using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TigerClaw.Core
{
    // Immutable, little-endian binary image. Neither code strings nor candidate
    // Lists survive compilation. Insertion order is separate from lookup order.
    internal sealed class CompactLexicon : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private const int Magic = 0x584C4354; // TCLX
        private const int Version = 1;
        private const int HeaderSize = 20;
        private readonly byte[] _image;
        private readonly int _codeIds, _sortedCodes, _starts, _candidateIds, _textOffsets, _textData;
        private readonly int _textCount;
        private int _compactBaselineSize;
        // Bounded cache, not permanent materialization of all queried text.
        private readonly CachedText[] _cache = new CachedText[256];
        private sealed record CachedText(int Id, string Text);

        public static CompactLexicon Empty { get; } = Build(new Dictionary<string, List<string>>());
        public int Count { get; }
        public int BinarySize => _image.Length;
        public IEnumerable<string> Keys
        {
            get { for (int i = 0; i < Count; i++) yield return Text(Int(_codeIds + i * 4)); }
        }
        public IEnumerable<IReadOnlyList<string>> Values
        {
            get { for (int i = 0; i < Count; i++) yield return Slice(i); }
        }
        public IReadOnlyList<string> this[string code] => TryGetValue(code, out var value)
            ? value : throw new KeyNotFoundException(code);

        private CompactLexicon(byte[] image)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Little-endian storage required");
            _image = image;
            _compactBaselineSize = image.Length;
            if (image.Length < HeaderSize || Int(0) != Magic || Int(4) != Version)
                throw new InvalidDataException("Invalid compact lexicon header");
            Count = Int(8);
            int candidates = Int(12);
            _textCount = Int(16);
            if (Count < 0 || candidates < 0 || _textCount < 0)
                throw new InvalidDataException("Negative compact lexicon count");
            long data = HeaderSize + 4L * (3L * Count + 1 + candidates + (long)_textCount + 1);
            if (data > image.Length || ((image.Length - data) & 1) != 0)
                throw new InvalidDataException("Truncated compact lexicon");
            _codeIds = HeaderSize;
            _sortedCodes = _codeIds + Count * 4;
            _starts = _sortedCodes + Count * 4;
            _candidateIds = _starts + (Count + 1) * 4;
            _textOffsets = _candidateIds + candidates * 4;
            _textData = (int)data;
            ValidateOffsets(_starts, Count, candidates);
            ValidateOffsets(_textOffsets, _textCount, (image.Length - _textData) / 2);
            for (int i = 0; i < Count; i++) ValidateTextId(Int(_codeIds + i * 4));
            for (int i = 0; i < candidates; i++) ValidateTextId(Int(_candidateIds + i * 4));
            // Strictly increasing keys prove uniqueness and a complete permutation
            // without allocating a second index on load.
            for (int i = 0; i < Count; i++)
            {
                int code = Int(_sortedCodes + i * 4);
                if ((uint)code >= (uint)Count) throw new InvalidDataException("Invalid lookup index");
                if (i > 0 && CodeSpan(Int(_sortedCodes + (i - 1) * 4)).CompareTo(CodeSpan(code), StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("Unsorted or duplicate code");
            }
        }

        private int Int(int offset) => BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan(offset, 4));
        private void ValidateTextId(int id)
        {
            if ((uint)id >= (uint)_textCount) throw new InvalidDataException("Invalid text id");
        }
        private void ValidateOffsets(int offset, int count, int end)
        {
            if (Int(offset) != 0 || Int(offset + count * 4) != end)
                throw new InvalidDataException("Invalid offset endpoints");
            int previous = 0;
            for (int i = 1; i <= count; i++)
            {
                int current = Int(offset + i * 4);
                if (current < previous || current > end) throw new InvalidDataException("Invalid offset order");
                previous = current;
            }
        }
        private ReadOnlySpan<char> TextSpan(int id)
        {
            int start = Int(_textOffsets + id * 4), end = Int(_textOffsets + (id + 1) * 4);
            return MemoryMarshal.Cast<byte, char>(_image.AsSpan(_textData + start * 2, (end - start) * 2));
        }
        private ReadOnlySpan<char> CodeSpan(int index) => TextSpan(Int(_codeIds + index * 4));
        private string Text(int id)
        {
            int slot = id & (_cache.Length - 1);
            var cached = Volatile.Read(ref _cache[slot]);
            if (cached != null && cached.Id == id) return cached.Text;
            var span = TextSpan(id);
            string text = new string(span);
            if (span.Length <= 256) Volatile.Write(ref _cache[slot], new CachedText(id, text));
            return text;
        }
        private CandidateSlice Slice(int index) => new CandidateSlice(this,
            Int(_starts + index * 4), Int(_starts + (index + 1) * 4));
        private readonly struct CandidateSlice : IReadOnlyList<string>
        {
            private readonly CompactLexicon _owner;
            private readonly int _start;
            public int Count { get; }
            public CandidateSlice(CompactLexicon owner, int start, int end)
            { _owner = owner; _start = start; Count = end - start; }
            public string this[int index] => (uint)index < (uint)Count
                ? _owner.Text(_owner.Int(_owner._candidateIds + (_start + index) * 4))
                : throw new ArgumentOutOfRangeException(nameof(index));
            public IEnumerator<string> GetEnumerator()
            { for (int i = 0; i < Count; i++) yield return this[i]; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        public bool ContainsKey(string code) => TryGetValue(code, out _);
        public bool TryGetValue(string code, out IReadOnlyList<string> values)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            int low = 0, high = Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int index = Int(_sortedCodes + middle * 4);
                int comparison = CodeSpan(index).CompareTo(code.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (comparison == 0) { values = Slice(index); return true; }
                if (comparison < 0) low = middle + 1; else high = middle - 1;
            }
            values = null;
            return false;
        }
        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return new KeyValuePair<string, IReadOnlyList<string>>(Text(Int(_codeIds + i * 4)), Slice(i));
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static CompactLexicon Build(IDictionary<string, List<string>> source) => Build(
            source.Select(pair => new KeyValuePair<string, IReadOnlyList<string>>(pair.Key, pair.Value)));

        public static CompactLexicon Build(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> source)
        {
            var rows = source.ToArray();
            var strings = new List<string>();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(string value)
            {
                if (value == null) throw new ArgumentException("Null lexicon text");
                if (ids.TryGetValue(value, out int id)) return id;
                id = strings.Count; strings.Add(value); ids.Add(value, id); return id;
            }
            var codeIds = new int[rows.Length];
            var starts = new int[rows.Length + 1];
            var candidates = new List<int>();
            for (int i = 0; i < rows.Length; i++)
            {
                codeIds[i] = Id(rows[i].Key);
                starts[i] = candidates.Count;
                if (rows[i].Value != null) foreach (string text in rows[i].Value) candidates.Add(Id(text));
            }
            starts[rows.Length] = candidates.Count;
            int[] sorted = Enumerable.Range(0, rows.Length).OrderBy(i => rows[i].Key, StringComparer.OrdinalIgnoreCase).ToArray();
            long characters = strings.Sum(s => (long)s.Length);
            long size = HeaderSize + 4L * (3L * rows.Length + 1 + candidates.Count + (long)strings.Count + 1) + characters * 2;
            if (size > Array.MaxLength) throw new InvalidDataException("Compact lexicon too large");
            byte[] image = new byte[(int)size];
            int position = 0;
            void Write(int value)
            { BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(position, 4), value); position += 4; }
            Write(Magic); Write(Version); Write(rows.Length); Write(candidates.Count); Write(strings.Count);
            foreach (int id in codeIds) Write(id);
            foreach (int index in sorted) Write(index);
            foreach (int start in starts) Write(start);
            foreach (int id in candidates) Write(id);
            int textOffset = 0;
            foreach (string text in strings) { Write(textOffset); textOffset = checked(textOffset + text.Length); }
            Write(textOffset);
            foreach (string text in strings)
            {
                foreach (char character in text)
                { BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(position, 2), character); position += 2; }
            }
            return new CompactLexicon(image);
        }

        public CompactLexicon WithCandidates(string code, IReadOnlyList<string> candidates)
        {
            ArgumentNullException.ThrowIfNull(code);
            ArgumentNullException.ThrowIfNull(candidates);
            int existing = -1;
            for (int i = 0; i < Count; i++)
                if (CodeSpan(i).Equals(code.AsSpan(), StringComparison.OrdinalIgnoreCase)) { existing = i; break; }
            int count = Count + (existing < 0 ? 1 : 0);
            int oldStart = existing < 0 ? Int(_starts + Count * 4) : Int(_starts + existing * 4);
            int oldEnd = existing < 0 ? oldStart : Int(_starts + (existing + 1) * 4);
            int candidateCount = checked(Int(_starts + Count * 4) - (oldEnd - oldStart) + candidates.Count);
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = oldStart; i < oldEnd; i++)
            {
                int id = Int(_candidateIds + i * 4);
                ids.TryAdd(Text(id), id);
            }
            var added = new List<string>();
            int Id(string text)
            {
                ArgumentNullException.ThrowIfNull(text);
                if (ids.TryGetValue(text, out int id)) return id;
                id = checked(_textCount + added.Count); added.Add(text); ids.Add(text, id); return id;
            }
            int newCodeId = existing < 0 ? Id(code) : -1;
            int[] values = candidates.Select(Id).ToArray();
            var retained = new HashSet<int>(values);
            long removedCharacters = 0;
            for (int i = oldStart; i < oldEnd; i++)
            {
                int id = Int(_candidateIds + i * 4);
                if (!retained.Contains(id)) removedCharacters += TextSpan(id).Length;
            }
            int texts = checked(_textCount + added.Count);
            long size = HeaderSize + 4L * (3L * count + 1 + candidateCount + (long)texts + 1) +
                (_image.Length - _textData) + 2L * added.Sum(text => (long)text.Length);
            // Most rank edits simply copy packed arrays. Periodic compaction
            // bounds dead strings left by repeated user additions/deletions.
            if (size <= Array.MaxLength && size <= (long)_compactBaselineSize + Math.Max(65536, _compactBaselineSize / 4) &&
                removedCharacters * 2 <= Math.Max(65536, _image.Length / 4))
            {
                byte[] image = new byte[(int)size];
                int position = 0;
                void Write(int value)
                { BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(position, 4), value); position += 4; }
                Write(Magic); Write(Version); Write(count); Write(candidateCount); Write(texts);
                for (int i = 0; i < Count; i++) Write(Int(_codeIds + i * 4));
                if (existing < 0) Write(newCodeId);
                bool inserted = existing >= 0;
                for (int i = 0; i < Count; i++)
                {
                    int index = Int(_sortedCodes + i * 4);
                    if (!inserted && code.AsSpan().CompareTo(CodeSpan(index), StringComparison.OrdinalIgnoreCase) < 0)
                    { Write(Count); inserted = true; }
                    Write(index);
                }
                if (!inserted) Write(Count);
                int start = 0;
                for (int i = 0; i < count; i++)
                {
                    Write(start);
                    start += i == existing || i == Count ? values.Length : Int(_starts + (i + 1) * 4) - Int(_starts + i * 4);
                }
                Write(start);
                for (int i = 0; i < count; i++)
                {
                    if (i == existing || i == Count) { foreach (int id in values) Write(id); }
                    else for (int j = Int(_starts + i * 4); j < Int(_starts + (i + 1) * 4); j++) Write(Int(_candidateIds + j * 4));
                }
                for (int i = 0; i < _textCount; i++) Write(Int(_textOffsets + i * 4));
                int offset = Int(_textOffsets + _textCount * 4);
                foreach (string text in added) { Write(offset); offset += text.Length; }
                Write(offset);
                _image.AsSpan(_textData).CopyTo(image.AsSpan(position));
                position += _image.Length - _textData;
                foreach (string text in added)
                    foreach (char character in text)
                    { BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(position, 2), character); position += 2; }
                return new CompactLexicon(image) { _compactBaselineSize = _compactBaselineSize };
            }
            IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> Updated()
            {
                bool found = false;
                foreach (var pair in this)
                {
                    if (string.Equals(pair.Key, code, StringComparison.OrdinalIgnoreCase))
                    { found = true; yield return new(pair.Key, candidates); }
                    else yield return pair;
                }
                if (!found) yield return new(code, candidates);
            }
            return Build(Updated());
        }
        public void WriteTo(Stream stream) => stream.Write(_image);
        public static CompactLexicon FromBinary(ReadOnlySpan<byte> image) => new CompactLexicon(image.ToArray());
    }
}
