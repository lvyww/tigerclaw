using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceNgramModel
    {
        // Cache metadata only, never copy model pages into managed memory.
        // Each QuerySession owns its cache; the mapping retains the existing leases.
        private readonly record struct MobileContext(long Successors, int Count, float Lambda);

        private sealed class MobileLayout
        {
            private const int HeaderSize = 104;
            private readonly MemoryMappedViewAccessor _view;
            private readonly int _stride;
            private readonly Section _bigram, _trigram;
            internal int UnigramCount { get; }
            internal long UnigramOffset { get; }
            private readonly record struct Section(long Blocks, long End, long Count, long Index, int IndexCount);

            internal MobileLayout(MemoryMappedViewAccessor view, long length)
            {
                _view = view;
                if (length < HeaderSize || view.ReadInt32(8) != 1 || view.ReadInt32(12) != HeaderSize ||
                    view.ReadInt64(16) != length || view.ReadInt32(28) != 0 ||
                    view.ReadInt32(36) != 0 || view.ReadInt32(84) != 0)
                    throw Invalid("header");
                _stride = view.ReadInt32(24);
                UnigramCount = view.ReadInt32(32);
                UnigramOffset = view.ReadInt64(40);
                _bigram = new(view.ReadInt64(56), view.ReadInt64(64), view.ReadInt32(48), view.ReadInt64(64), view.ReadInt32(52));
                _trigram = new(view.ReadInt64(88), view.ReadInt64(96), view.ReadInt64(72), view.ReadInt64(96), view.ReadInt32(80));
                if (_stride < 16 || _stride > 65536 || UnigramCount <= 0 || UnigramOffset != HeaderSize ||
                    UnigramOffset + (long)UnigramCount * 8 != _bigram.Blocks)
                    throw Invalid("unigrams/stride");
                ValidateSection(_bigram, _trigram.Blocks, length);
                ValidateSection(_trigram, length, length);
            }

            private static InvalidDataException Invalid(string part) => new("Invalid mobile n-gram " + part + ".");

            private void ValidateSection(Section section, long next, long length)
            {
                if (section.Blocks < HeaderSize || section.End < section.Blocks || section.End > length ||
                    section.Count < 0 || section.Count > (section.End - section.Blocks) / 16 ||
                    section.IndexCount < 0 || section.IndexCount != (section.Count + _stride - 1) / _stride ||
                    section.IndexCount > (length - section.End) / 16 ||
                    section.End + (long)section.IndexCount * 16 != next ||
                    (section.Count == 0 && section.Blocks != section.End))
                    throw Invalid("section bounds");
                ulong previousKey = 0;
                long previousOffset = -1;
                for (int i = 0; i < section.IndexCount; i++)
                {
                    long at = section.Index + (long)i * 16;
                    ulong key = _view.ReadUInt64(at);
                    long offset = _view.ReadInt64(at + 8);
                    if ((i > 0 && key <= previousKey) || offset < section.Blocks || offset > section.End - 16 ||
                        offset <= previousOffset || (i == 0 && offset != section.Blocks))
                        throw Invalid("sparse index");
                    previousKey = key;
                    previousOffset = offset;
                }
            }

            internal (float Lambda, float Probability, bool Observed) Lookup(
                bool trigram, ulong key, int target, FixedSizeCache<MobileContext> cache)
            {
                if (!cache.TryGetValue(key, out MobileContext context))
                {
                    context = FindContext(trigram ? _trigram : _bigram, key, cache);
                    cache.Set(key, context);
                }
                int low = 0, high = context.Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    uint value = _view.ReadUInt32(context.Successors + (long)middle * 8);
                    if (value < (uint)target) low = middle + 1;
                    else high = middle;
                }
                if (low < context.Count)
                {
                    long at = context.Successors + (long)low * 8;
                    if (_view.ReadUInt32(at) == (uint)target)
                    {
                        float probability = _view.ReadSingle(at + 4);
                        if (!float.IsFinite(probability) || probability < 0) throw Invalid("successor probability");
                        // Zero-valued observed records must remain distinct from missing records.
                        return (context.Lambda, probability, true);
                    }
                }
                return (context.Lambda, 0, false);
            }

            private MobileContext FindContext(Section section, ulong key, FixedSizeCache<MobileContext> cache)
            {
                int low = 0, high = section.IndexCount;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (_view.ReadUInt64(section.Index + (long)middle * 16) <= key) low = middle + 1;
                    else high = middle;
                }
                int page = low - 1;
                if (page < 0) return new(0, 0, 1);
                long position = _view.ReadInt64(section.Index + (long)page * 16 + 8);
                long end = page + 1 < section.IndexCount
                    ? _view.ReadInt64(section.Index + (long)(page + 1) * 16 + 8) : section.End;
                long count = Math.Min(_stride, section.Count - (long)page * _stride);
                ulong previousKey = 0;
                var result = new MobileContext(0, 0, 1);
                for (long i = 0; i < count; i++)
                {
                    if (position > end - 16) throw Invalid("truncated context");
                    ulong current = _view.ReadUInt64(position);
                    float lambda = _view.ReadSingle(position + 8);
                    uint successors = _view.ReadUInt32(position + 12);
                    if (!float.IsFinite(lambda) || lambda < 0 || successors > (end - position - 16) / 8 ||
                        (i > 0 && current <= previousKey) ||
                        (i == 0 && current != _view.ReadUInt64(section.Index + (long)page * 16)))
                        throw Invalid("context");
                    var found = new MobileContext(position + 16, (int)successors, lambda);
                    // A beam often queries several nearby contexts from the same sparse
                    // block. Cache the block's metadata in one scan rather than restarting
                    // that scan for each key; the fixed-size cache remains bounded.
                    cache.Set(current, found);
                    if (current == key) result = found;
                    previousKey = current;
                    position += 16 + (long)successors * 8;
                }
                if (position != end) throw Invalid("trailing context data");
                return result;
            }
        }
    }
}
