#pragma once
#include <string>
#include <string_view>
#include <vector>
#include <utility>
#include <unordered_set>

namespace tiger::core
{
    class SentenceCharacterRanks
    {
    public:
        static constexpr int UnknownRank = 20001;
        static SentenceCharacterRanks FromText(std::u16string_view text);
        static const SentenceCharacterRanks& Default();
        int GetRank(std::u16string_view text) const;
        std::unordered_set<std::u16string> TakeTop(int count) const;
        std::size_t Size() const { return _entries.size(); }
    private:
        // Sorted immutable storage retains source ranks without a second owned
        // string pool or per-lookup string allocations.
        std::vector<std::pair<std::u16string, int>> _entries;
    };
}
