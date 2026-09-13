#pragma once
#include <string>
#include <string_view>
#include <unordered_map>
#include "LexiconText.h"
#include "CompactLexicon.h"
namespace tiger::core
{
    using ConstructCodeMap = std::unordered_map<std::u16string, std::u16string>;
    std::u16string ConstructWordCode(std::u16string_view word, const ConstructCodeMap& lookup);
    ConstructCodeMap BuildConstructCodeMap(std::span<const LexiconRow> explicitRows,
        std::span<const CompactLexicon::Entry> lexicon);
    void AppendInferredRows(std::span<const LexiconRow> uncoded, const ConstructCodeMap& lookup, std::vector<LexiconRow>& coded);
}
