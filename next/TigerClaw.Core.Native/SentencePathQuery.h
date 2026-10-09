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
        bool groupEligibleOnly = false, const SentenceLockedPrefix* locked = nullptr, int autoSelectMinCodeLength = 3)
    {
        auto normalized = NormalizeSentenceRaw(raw);
        if (!HasSentenceLetter(normalized)) return false;
        bool firstOnly = groupEligibleOnly && !std::any_of(normalized.begin(), normalized.end(),
            [](char16_t unit) { return SentenceDigit(unit) || unit == u';' || unit == u'\''; });
        using Progress = std::pair<std::size_t, std::size_t>;
        std::vector<std::set<Progress>> states(normalized.size() + 1);
        constexpr auto mismatch = std::u16string_view::npos;
        std::size_t begin = 0, matched = 0, excludedMatch = 0;
        if (locked)
        {
            auto lockedRaw = NormalizeSentenceRaw(locked->raw);
            if (lockedRaw.empty() || !normalized.starts_with(lockedRaw)) return false;
            auto length = std::min(required.size(), locked->text.size());
            if (required.substr(0, length) != std::u16string_view(locked->text).substr(0, length)) return false;
            matched = length; begin = lockedRaw.size();
            if (excluded) excludedMatch = excluded->starts_with(locked->text) ? locked->text.size() : mismatch;
            if (begin == normalized.size()) return matched == required.size() && (!excluded || excludedMatch != excluded->size());
        }
        states[begin].emplace(matched, excludedMatch);
        for (std::size_t position = begin; position < normalized.size(); ++position)
        {
            if (states[position].empty()) continue;
            for (const auto& edge : SentenceEdges(lexicon, normalized, position, duplicateSingles, -1, autoSelectMinCodeLength))
            {
                if (firstOnly && edge.candidate->rank > 1 && !(duplicateSingles && autoSelectMinCodeLength > 0 &&
                    edge.codeLength >= static_cast<std::size_t>(std::clamp(autoSelectMinCodeLength, 0, 128)) &&
                    edge.candidate->elements.size() == 1)) continue;
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
    inline std::size_t CompetingSentenceBoundaryEnd(const SentenceLexicon& lexicon, std::u16string_view input,
        std::size_t committed, std::size_t proposed, std::size_t elements)
    {
        auto raw = NormalizeSentenceRaw(input);
        if (proposed <= committed || proposed > raw.size() || !elements) return proposed;
        std::vector<std::vector<bool>> reachable(raw.size() + 1, std::vector<bool>(elements + 1));
        reachable[committed][0] = true;
        auto furthest = proposed;
        for (auto start = committed; start < raw.size(); ++start)
            for (std::size_t count = 0; count < elements; ++count)
            {
                if (!reachable[start][count]) continue;
                for (auto length : lexicon.CodeLengths())
                {
                    if (start + length > raw.size()) continue;
                    auto candidates = lexicon.Candidates(std::u16string_view(raw).substr(start, length));
                    if (!candidates) continue;
                    for (const auto& candidate : *candidates)
                    {
                        auto next = count + candidate.elements.size();
                        if (next == elements) furthest = std::max(furthest, start + length);
                        else if (next < elements) reachable[start + length][next] = true;
                    }
                }
            }
        return furthest;
    }

}
