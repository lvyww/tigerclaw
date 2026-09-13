#pragma once
#include "SentenceSupplement.h"
#include <filesystem>
#include <unordered_map>

namespace tiger::core
{
    class SentenceSupplementParser
    {
    public:
        void Consume(std::u16string_view raw);
        const std::vector<SentenceSupplementEntry>& Entries() const { return _entries; }
    private:
        std::vector<SentenceSupplementEntry> _entries;
        std::unordered_map<std::u16string, std::size_t> _indices;
    };
    std::vector<SentenceSupplementEntry> LoadSentenceSupplements(const std::filesystem::path& directory);
}
