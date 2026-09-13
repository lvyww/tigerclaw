#pragma once
#include "CompactLexicon.h"
#include "CodeCase.h"
#include <unordered_map>
namespace tiger::core
{
    using TextMap = std::unordered_map<std::u16string, std::u16string>;
    class CodeSet
    {
        TextMap keys_;
    public:
        void Add(std::u16string_view code) { keys_.try_emplace(FoldOrdinalCode(code), code); }
        bool Contains(std::u16string_view code) const { return keys_.contains(FoldOrdinalCode(code)); }
        std::vector<std::u16string> Keys() const;
    };
    struct LexiconMetadata
    {
        TextMap comments, splits, fullCodes;
        CodeSet unique, nonTerminal, autoShort;
        bool shortSemicolon = false, shortSlash = false, shortBracket = false, shortZ = false;
    };
    TextMap LoadTextMetadata(const std::filesystem::path& directory, bool splits);
    LexiconMetadata BuildLexiconMetadata(std::span<const CompactLexicon::Entry> entries);
}
