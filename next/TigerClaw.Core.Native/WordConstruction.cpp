#include "WordConstruction.h"
#include "TextElements.h"
#include "CodeCase.h"
#include "DotNetLetterTable.h"
#include <algorithm>
#include <iterator>
#include <vector>

namespace tiger::core
{
    static int ConstructPriority(std::u16string_view code)
    {
        auto c = code.front(); // Callers pass normalized, nonempty code.
        auto match = std::lower_bound(std::begin(LetterRanges), std::end(LetterRanges), c,
            [](const LetterRange& range, char16_t value) { return range.last < value; });
        if (match == std::end(LetterRanges) || c < match->first) return 0;
        return c == u'o' ? 1 : c == u'z' ? 2 : 3;
    }
    ConstructCodeMap BuildConstructCodeMap(std::span<const LexiconRow> explicitRows, std::span<const CompactLexicon::Entry> lexicon)
    {
        ConstructCodeMap result;
        for (const auto& row : explicitRows)
        {
            auto code = NormalizeCode(row.code);
            auto text = CandidateCommitText(row.text);
            if (code.empty() || TextElementStarts(text).size() != 1) continue;
            result.emplace(std::u16string(text), std::move(code));
        }
        ConstructCodeMap inferred;
        for (const auto& [rawCode, candidates] : lexicon)
        {
            auto code = NormalizeCode(rawCode);
            if (code.size() < 2) continue;
            for (const auto& candidate : candidates)
            {
                auto text = std::u16string(CandidateCommitText(candidate));
                if (result.contains(text) || TextElementStarts(text).size() != 1) continue;
                auto [existing, inserted] = inferred.emplace(text, code);
                if (!inserted)
                {
                    auto oldRank = ConstructPriority(existing->second), newRank = ConstructPriority(code);
                    if (newRank > oldRank || (newRank == oldRank && code < existing->second)) existing->second = code;
                }
            }
        }
        result.insert(inferred.begin(), inferred.end());
        return result;
    }
    void AppendInferredRows(std::span<const LexiconRow> uncoded, const ConstructCodeMap& lookup, std::vector<LexiconRow>& coded)
    {
        for (const auto& row : uncoded)
        {
            if (row.text.empty()) continue;
            auto code = NormalizeCode(ConstructWordCode(CandidateCommitText(row.text), lookup));
            if (!code.empty()) coded.push_back({std::move(code), row.text, row.frequency});
        }
    }
    std::u16string ConstructWordCode(std::u16string_view word, const ConstructCodeMap& lookup)
    {
        std::u16string source(word);
        // Preserve the C# ordered token-removal list, notably doubled ellipsis
        // and em dash tokens rather than deleting their singleton forms.
        std::u16string_view tokens[] = {u"\n",u"\r",u"\t",u" ",u"=",u"\uff0c",u"-",u"\u3002",u"\u00b7",u"\u3010",u"\u3001",u"\u3011",u"\uff1b",
            u"\uff09",u"\uff01",u"@",u"#",u"\uffe5",u"%",u"\u2026\u2026",u"&",u"*",u"\uff08",u"+",u"\u300a",u"\u2014\u2014",u"\u300b",u"~",u"{",u"|",u"}",u"\uff1f",u"\uff1a",
            u",",u".",u"`",u"[",u"\\",u"]",u"/",u";",u"'",u")",u"!",u"$",u"^",u"<",u"_",u">",u"?",u"\""};
        for (auto token : tokens)
            for (auto at = source.find(token); at != source.npos; at = source.find(token, at)) source.erase(at, token.size());
        auto starts = TextElementStarts(source);
        std::vector<std::u16string> codes;
        for (std::size_t i = 0; i < starts.size(); ++i)
        {
            auto end = i + 1 < starts.size() ? starts[i+1] : source.size();
            auto element = source.substr(starts[i], end - starts[i]);
            auto found = lookup.find(element);
            if (found != lookup.end() && !found->second.empty()) { codes.push_back(found->second); continue; }
            if (element.size() == 1)
            {
                auto c = element[0];
                if (c >= u'A' && c <= u'Z') c = char16_t(c + 32);
                if (c >= u'a' && c <= u'z') codes.emplace_back(2, c);
            }
        }
        if (codes.empty()) return {};
        if (codes.size() == 1) return codes[0];
        if (codes.size() == 2)
            return codes[0].size() >= 2 && codes[1].size() >= 2 ? codes[0].substr(0,2) + codes[1].substr(0,2) : std::u16string{};
        if (codes.size() == 3)
            return codes[2].size() >= 2 ? codes[0].substr(0,1) + codes[1].substr(0,1) + codes[2].substr(0,2) : std::u16string{};
        return codes[0].substr(0,1) + codes[1].substr(0,1) + codes[2].substr(0,1) + codes.back().substr(0,1);
    }
}
