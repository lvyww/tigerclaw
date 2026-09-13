#pragma once
#include "SentenceBeam.h"
#include "LexiconText.h"
#include "DotNetLetterTable.h"

namespace tiger::core
{
    struct SentenceLatticeResult
    {
        std::u16string raw;
        std::vector<SentenceBeamBucket> states;
        std::vector<SentenceBeamState> candidates; // scores include EOS/isolation; boundaries preserved
        std::size_t expanded = 0;
        bool confidenceTruncated = false;
    };
    struct SentenceLatticeSettings
    {
        std::size_t beamWidth = 2000, candidateLimit = 5;
        double rankPenalty = 0.03, emittedReward = 2.0, wholeSingleReward = 5.0;
        bool duplicateSingles = true;
    };
    inline std::u16string NormalizeSentenceRaw(std::u16string_view raw)
    {
        std::u16string result;
        for (char16_t unit : raw)
            if (!IsDotNetWhiteSpace(unit)) result += NormalizeCode(std::u16string(1, unit));
        return result;
    }
    inline bool HasSentenceLetter(std::u16string_view raw)
    {
        return std::any_of(raw.begin(), raw.end(), [](char16_t unit)
        {
            auto range = std::lower_bound(std::begin(LetterRanges), std::end(LetterRanges), unit,
                [](const LetterRange& item, char16_t value) { return item.last < value; });
            return range != std::end(LetterRanges) && range->first <= unit;
        });
    }
    inline std::u16string SegmentedSentenceCode(std::u16string_view raw, std::shared_ptr<const SentenceBoundary> boundary)
    {
        std::vector<std::size_t> ends;
        while (boundary) { ends.push_back(boundary->rawLength); boundary = boundary->previous; }
        std::reverse(ends.begin(), ends.end());
        std::u16string result;
        std::size_t start = 0;
        for (auto end : ends)
        {
            if (end < start || end > raw.size()) throw std::out_of_range("Invalid sentence boundary");
            if (start) result += u' ';
            result += raw.substr(start, end - start);
            start = end;
        }
        return result;
    }
    // Full recomputation foundation. Transition callback owns boundary/unigram
    // policy; isolation and supplemental scorers are explicit optional inputs.
    inline SentenceLatticeResult DecodeSentenceLattice(const SentenceLexicon& lexicon,
        std::u16string_view raw, const SentenceTransition& transition,
        const SentenceLatticeSettings& settings = {},
        const std::function<double(std::u16string_view)>& isolation = {},
        const SentenceSupplementAdvance& supplement = {},
        const SentenceLatticeResult* previous = nullptr)
    {
        if (!transition) throw std::invalid_argument("Sentence transition scorer is required");
        SentenceLatticeResult result;
        result.raw = NormalizeSentenceRaw(raw);
        // C# tests char.IsLetter per UTF-16 unit, not Rune.IsLetter. A raw
        // sequence containing only selectors/symbols never enters the lattice.
        if (!HasSentenceLetter(result.raw)) return {};
        // Reuse is valid only for the same immutable lexicon/scorers/settings.
        // The runtime owner enforces that lifetime and configuration contract.
        if (previous && previous->raw == result.raw) return *previous;
        bool reuse = previous && previous->raw.size() > 4 && result.raw.size() > 4 && !previous->states.empty();
        std::size_t fromPosition = 0;
        std::ptrdiff_t minimumEnd = -1;
        bool expand = true;
        if (reuse && result.raw.starts_with(previous->raw))
        {
            result.states = previous->states;
            std::size_t suffix = 0;
            for (auto i = result.raw.size(); i > 0; --i)
            {
                auto unit = result.raw[i - 1];
                if (!SentenceDigit(unit) && unit != u';' && unit != u'\'') break;
                ++suffix;
            }
            auto maximum = lexicon.CodeLengths().empty() ? 1 : lexicon.CodeLengths().back();
            auto span = maximum + suffix;
            fromPosition = previous->raw.size() + 1 > span ? previous->raw.size() + 1 - span : 0;
            minimumEnd = static_cast<std::ptrdiff_t>(previous->raw.size());
        }
        else if (reuse && previous->raw.starts_with(result.raw))
        {
            result.states = previous->states;
            expand = false;
        }
        result.states.resize(result.raw.size() + 1);
        if (minimumEnd < 0 && expand)
        {
            SentenceBeamState initial;
            initial.previous2 = initial.previous1 = u"\x02";
            initial.maxRank = 1;
            result.states[0].Add(std::move(initial));
        }
        for (std::size_t position = fromPosition; expand && position < result.raw.size(); ++position)
        {
            const auto& current = result.states[position].Limit(settings.beamWidth, settings.duplicateSingles);
            if (current.empty()) continue;
            auto edges = SentenceEdges(lexicon, result.raw, position, settings.duplicateSingles, minimumEnd);
            for (std::size_t start = 0; start < edges.size();)
            {
                auto end = start + 1;
                while (end < edges.size() && edges[end].codeLength == edges[start].codeLength) ++end;
                for (const auto& item : current)
                for (auto edgeIndex = start; edgeIndex < end; ++edgeIndex)
                {
                    const auto& edge = edges[edgeIndex];
                    result.states[edge.end].Add(AdvanceSentenceBeam(item, edge, transition, settings.rankPenalty,
                        settings.emittedReward, settings.wholeSingleReward, supplement));
                    ++result.expanded;
                }
                start = end;
            }
        }
        const auto& completed = result.states.back().Limit(settings.beamWidth, settings.duplicateSingles);
        result.confidenceTruncated = result.states.back().Truncated();
        bool scoreFirst = false;
        SentenceBeamBucket final;
        for (auto item : completed)
        {
            double ending = transition(item.previous2, item.previous1, u"\x03") - (isolation ? isolation(item.text) : 0);
            item.score += ending;
            item.logMass += ending;
            item.maxRank = std::max(std::size_t{1}, item.maxRank);
            if (settings.duplicateSingles && item.boundary && item.boundary->previous) scoreFirst = true;
            final.Add(std::move(item));
        }
        result.candidates = final.Limit(settings.candidateLimit, scoreFirst);
        return result;
    }
}
