#pragma once
#include "CompactLexicon.h"
#include "CodeCase.h"
#include "TextElements.h"
#include <unordered_map>
#include <unordered_set>
#include <set>
#include <cmath>

namespace tiger::core
{
    struct SentenceLexiconCandidate
    {
        std::u16string text;
        std::size_t rank;
        double logRank;
        std::vector<std::u16string> elements;
        bool optimalSingle;
    };
    class SentenceLexicon
    {
    public:
        using CharacterSet = std::unordered_set<std::u16string>;
        static SentenceLexicon Build(std::span<const CompactLexicon::Entry> source,
            const CharacterSet& common = {}, const CharacterSet& whitelist = {})
        {
            std::vector<CompactLexicon::Entry> exact;
            std::unordered_map<std::u16string, std::size_t> indices;
            for (const auto& [rawCode, rawTexts] : source)
            {
                auto code = NormalizeCode(rawCode);
                if (code.empty()) continue;
                std::vector<std::u16string> texts;
                CharacterSet seen;
                for (const auto& text : rawTexts)
                    if (!text.empty() && seen.insert(text).second) texts.push_back(text);
                if (texts.empty()) continue; // empty later source does not erase earlier code
                auto [entry, inserted] = indices.emplace(FoldOrdinalCode(code), exact.size());
                if (inserted) exact.emplace_back(std::move(code), std::move(texts));
                else exact[entry->second].second = std::move(texts);
            }
            struct Codes { std::u16string optimal, first, any; };
            std::unordered_map<std::u16string, Codes> characters;
            auto shorter = [](std::u16string& current, const std::u16string& code)
            { if (current.empty() || code.size() < current.size()) current = code; };
            for (const auto& [code, texts] : exact)
                for (std::size_t rank = 0; rank < texts.size(); ++rank)
                    if (TextElementStarts(texts[rank]).size() == 1)
                    {
                        auto& codes = characters[texts[rank]];
                        shorter(codes.optimal, code);
                        if (code.size() >= 2)
                        {
                            shorter(codes.any, code);
                            if (rank == 0) shorter(codes.first, code);
                        }
                    }
            SentenceLexicon result;
            std::set<std::size_t> lengths;
            for (const auto& [code, texts] : exact)
            {
                std::vector<SentenceLexiconCandidate> allowed;
                for (std::size_t index = 0; index < texts.size(); ++index)
                {
                    const auto& text = texts[index];
                    auto starts = TextElementStarts(text);
                    auto found = characters.find(text);
                    bool single = starts.size() == 1;
                    bool allow = code.size() == 1 || !single || !common.contains(text) || whitelist.contains(text);
                    if (!allow && found != characters.end())
                    {
                        const auto& primary = found->second.first.empty() ? found->second.any : found->second.first;
                        allow = FoldOrdinalCode(primary) == FoldOrdinalCode(code);
                    }
                    if (!allow) continue;
                    std::vector<std::u16string> elements;
                    for (std::size_t i = 0; i < starts.size(); ++i)
                    {
                        auto end = i + 1 < starts.size() ? starts[i + 1] : text.size();
                        elements.push_back(text.substr(starts[i], end - starts[i]));
                    }
                    bool optimal = found != characters.end() && FoldOrdinalCode(found->second.optimal) == FoldOrdinalCode(code);
                    allowed.push_back({text, index + 1, std::log(index + 1.0), std::move(elements), optimal});
                }
                if (allowed.empty()) continue;
                auto folded = FoldOrdinalCode(code);
                result._candidates.emplace(folded, std::move(allowed));
                lengths.insert(code.size());
                for (std::size_t length = 1; length < code.size(); ++length)
                    result._prefixes.insert(FoldOrdinalCode(std::u16string_view(code).substr(0, length)));
            }
            result._lengths.assign(lengths.begin(), lengths.end());
            return result;
        }
        const std::vector<SentenceLexiconCandidate>* Candidates(std::u16string_view code) const
        {
            auto found = _candidates.find(FoldOrdinalCode(code));
            return found == _candidates.end() ? nullptr : &found->second;
        }
        bool IsProperPrefix(std::u16string_view code) const { return !code.empty() && _prefixes.contains(FoldOrdinalCode(code)); }
        const std::vector<std::size_t>& CodeLengths() const { return _lengths; }
    private:
        std::unordered_map<std::u16string, std::vector<SentenceLexiconCandidate>> _candidates;
        CharacterSet _prefixes;
        std::vector<std::size_t> _lengths;
    };
}
