#pragma once
#include <algorithm>
#include "SentenceLexicon.h"
#include "DotNetDigitTable.h"
#include <limits>
#include <stdexcept>

namespace tiger::core
{
    inline bool SentenceDigit(char16_t value)
    {
        auto found = std::lower_bound(std::begin(DigitRanges), std::end(DigitRanges), value,
            [](const DigitRange& range, char16_t code) { return range.last < code; });
        return found != std::end(DigitRanges) && found->first <= value;
    }
    struct SentenceCodeSuffix { std::size_t end; int rank; };
    inline SentenceCodeSuffix ReadSentenceCodeSuffix(std::u16string_view raw, std::size_t end)
    {
        if (end > raw.size()) throw std::out_of_range("Code end exceeds raw input");
        if (end == raw.size()) return {end, 0};
        if (raw[end] == u';') return {end + 1, 2};
        if (raw[end] == u'\'') return {end + 1, 3};
        if (!SentenceDigit(raw[end])) return {end, 0};
        auto first = end;
        while (end < raw.size() && SentenceDigit(raw[end])) ++end;
        if (end - first == 1 && raw[first] == u'0') return {end, 10};
        int rank = 0;
        for (auto i = first; i < end; ++i)
        {
            // char.IsDigit scans all Nd, but invariant int.Parse(None) only
            // accepts ASCII digits. Retain the reference failure semantics.
            if (raw[i] < u'0' || raw[i] > u'9') throw std::invalid_argument("Non-ASCII sentence rank");
            int digit = raw[i] - u'0';
            if (rank > (std::numeric_limits<int>::max() - digit) / 10) throw std::overflow_error("Sentence rank overflow");
            rank = rank * 10 + digit;
        }
        return {end, rank}; // "00" is rank zero, not rank ten
    }
    struct SentenceEdge
    {
        const SentenceLexiconCandidate* candidate;
        std::size_t end;
        int selectedRank;
        bool wholeInput;
        std::size_t codeLength = 0;
    };
    // Returned candidate pointers borrow the immutable index. Raw must already
    // be normalized by the decoder; ordering is code length then lexicon rank.
    inline std::vector<SentenceEdge> SentenceEdges(const SentenceLexicon& lexicon,
        std::u16string_view raw, std::size_t position, bool duplicateSingles,
        std::ptrdiff_t minimumConsumedEndExclusive = -1)
    {
        std::vector<SentenceEdge> result;
        if (position >= raw.size()) return result;
        if (position && (raw[position] == u';' || raw[position] == u'/' || raw[position] == u'[')) return result;
        for (auto length : lexicon.CodeLengths())
        {
            if (length > raw.size() - position) continue;
            auto candidates = lexicon.Candidates(raw.substr(position, length));
            if (!candidates) continue;
            auto suffix = ReadSentenceCodeSuffix(raw, position + length);
            bool whole = position == 0 && suffix.end == raw.size();
            if (minimumConsumedEndExclusive >= 0 && suffix.end <= static_cast<std::size_t>(minimumConsumedEndExclusive)) continue;
            if (raw.size() > 1 && suffix.end - position < 2) continue;
            for (const auto& candidate : *candidates)
            {
                bool allowed = suffix.rank > 0 ? candidate.rank == static_cast<std::size_t>(suffix.rank)
                    : candidate.rank == 1 || whole || (duplicateSingles && candidate.elements.size() == 1);
                if (allowed) result.push_back({&candidate, suffix.end, suffix.rank, whole, length});
            }
        }
        return result;
    }
}
