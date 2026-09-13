#pragma once
#include "SentenceLattice.h"
#include <optional>
#include <set>

namespace tiger::core
{
    // Exact reachability independent of Beam/model scores. States track only
    // prefix progress and equality with an excluded surface text, so distinct
    // segmentations of that same output never imply candidate ambiguity.
    inline bool HasCompleteSentenceCandidate(const SentenceLexicon& lexicon,
        std::u16string_view raw, bool duplicateSingles,
        std::u16string_view required = {}, std::optional<std::u16string_view> excluded = {},
        bool groupEligibleOnly = false)
    {
        auto normalized = NormalizeSentenceRaw(raw);
        if (!HasSentenceLetter(normalized)) return false;
        bool firstOnly = groupEligibleOnly && !std::any_of(normalized.begin(), normalized.end(),
            [](char16_t unit) { return SentenceDigit(unit) || unit == u';' || unit == u'\''; });
        using Progress = std::pair<std::size_t, std::size_t>;
        std::vector<std::set<Progress>> states(normalized.size() + 1);
        states[0].emplace(0, 0);
        constexpr auto mismatch = std::u16string_view::npos;
        for (std::size_t position = 0; position < normalized.size(); ++position)
        {
            if (states[position].empty()) continue;
            for (const auto& edge : SentenceEdges(lexicon, normalized, position, duplicateSingles))
            {
                if (firstOnly && edge.candidate->rank > 1) continue;
                std::u16string_view text = edge.candidate->text;
                for (auto [matchedRequired, matchedExcluded] : states[position])
                {
                    if (matchedRequired < required.size())
                    {
                        auto length = std::min(text.size(), required.size() - matchedRequired);
                        if (!length || required.substr(matchedRequired, length) != text.substr(0, length)) continue;
                        matchedRequired += length;
                    }
                    if (excluded && matchedExcluded != mismatch)
                    {
                        if (text.size() > excluded->size() - matchedExcluded || excluded->substr(matchedExcluded, text.size()) != text)
                            matchedExcluded = mismatch;
                        else matchedExcluded += text.size();
                    }
                    if (edge.end == normalized.size() && matchedRequired == required.size() &&
                        (!excluded || matchedExcluded != excluded->size())) return true;
                    states[edge.end].emplace(matchedRequired, matchedExcluded);
                }
            }
        }
        return false;
    }
}
