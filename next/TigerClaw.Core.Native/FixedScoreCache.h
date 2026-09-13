#pragma once
#include <cstdint>
#include <vector>
#include <stdexcept>
#include <algorithm>

namespace tiger::core
{
    // Single serialized decoder owner, matching the C# decode-lock contract.
    // No allocations on Set, direct replacement on collision, key 0 is valid.
    template<class T>
    class FixedScoreCache
    {
        struct Entry { std::uint64_t key = 0; T value{}; bool occupied = false; };
    public:
        explicit FixedScoreCache(std::size_t size)
        {
            if (!size || (size & (size - 1))) throw std::invalid_argument("Cache size must be a power of two");
            _entries.resize(size);
        }
        bool Get(std::uint64_t key, T& value) const
        {
            const auto& entry = _entries[Index(key)];
            if (!entry.occupied || entry.key != key) return false;
            value = entry.value;
            return true;
        }
        void Set(std::uint64_t key, T value) { _entries[Index(key)] = {key, value, true}; }
        void Clear() { std::fill(_entries.begin(), _entries.end(), Entry{}); }
        std::size_t Capacity() const { return _entries.size(); }
    private:
        std::size_t Index(std::uint64_t key) const
        {
            key ^= key >> 33; key *= 0xff51afd7ed558ccdULL;
            key ^= key >> 33; key *= 0xc4ceb9fe1a85ec53ULL;
            key ^= key >> 33;
            return static_cast<std::size_t>(key) & (_entries.size() - 1);
        }
        std::vector<Entry> _entries;
    };
}
