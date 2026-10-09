#pragma once
#include "SentenceBeam.h"
#include "SentenceLexicalPrior.h"
#include "SentenceFusionRanking.h"
#include "LexiconText.h"
#include "DotNetLetterTable.h"
#include <stop_token>

namespace tiger::core
{
    struct SentenceLatticeResult
    {
        std::u16string raw;
        std::vector<SentenceBeamBucket> states;
        std::vector<SentenceBeamState> candidates; // scores include EOS/isolation; boundaries preserved
        std::vector<SentenceBeamState> confidencePool;
        std::size_t expanded = 0;
        bool confidenceTruncated = false, learningAffected = false;
    };
    struct SentenceLatticeSettings
    {
        std::size_t beamWidth = 2000, candidateLimit = 5;
        double rankPenalty = 0.03, emittedReward = 2.0, wholeSingleReward = 5.0;
        bool duplicateSingles = true, preserveTruncatedEvidence = false;
        double canonicalReward = 0, protectedIsolationFactor = 1, lexicalWeight = 0;
        int autoSelectMinCodeLength = 3; // 0 disables implicit non-first entries
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
        const SentenceLatticeResult* previous = nullptr, const SentenceHistoryTransition* history = nullptr,
        SentenceLearningQuery* learning = nullptr,
        const std::function<double(const SentenceBeamState&)>& pathIsolation = {}, const SentenceLockedPrefix* locked = nullptr, std::stop_token stop = {})
    {
        auto canceled = [&] { if (stop.stop_requested()) throw std::runtime_error("Sentence decode canceled"); };
        canceled();
        if (!transition) throw std::invalid_argument("Sentence transition scorer is required");
        SentenceLatticeResult result;
        result.raw = NormalizeSentenceRaw(raw);
        // C# tests char.IsLetter per UTF-16 unit, not Rune.IsLetter. A raw
        // sequence containing only selectors/symbols never enters the lattice.
        if (!HasSentenceLetter(result.raw)) return {};
        auto lockedRaw = locked ? NormalizeSentenceRaw(locked->raw) : std::u16string{};
        if (locked && (lockedRaw.empty() || !result.raw.starts_with(lockedRaw))) return {};
        // Reuse is valid only for the same immutable lexicon/scorers/settings.
        // The runtime owner enforces that lifetime and configuration contract.
        if (previous && previous->raw == result.raw) return *previous;
        auto maximumCode = lexicon.CodeLengths().empty() ? 1 : lexicon.CodeLengths().back();
        auto ascii = [](std::u16string_view text) { return std::all_of(text.begin(), text.end(), [](char16_t c) { return c >= u'a' && c <= u'z'; }); };
        // Selectors can change an already expanded edge. Learned hints can also
        // change the global beam comparator; recompute these generations.
        bool reuse = !locked && previous && !previous->learningAffected && !(learning && learning->snapshot && !learning->snapshot->empty()) &&
            previous->raw.size() > maximumCode && result.raw.size() > maximumCode && !previous->states.empty() &&
            ascii(previous->raw) && ascii(result.raw);
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
        if (minimumEnd < 0 && expand && learning) learning->affected = false;
        if (minimumEnd < 0 && expand)
        {
            SentenceBeamState initial;
            initial.previous2 = initial.previous1 = u"\x02";
            initial.maxRank = 1;
            if (history) initial.history = history->begin;
            if (locked)
            {
                initial.text = locked->text;
                initial.codeScore = locked->boundary ? locked->boundary->codeScore : 0;
                std::vector<const SentenceBoundary*> chain;
                for (auto b = locked->boundary.get(); b; b = b->previous.get()) chain.push_back(b);
                for (auto it = chain.rbegin(); it != chain.rend(); ++it)
                {
                    auto copy = **it; copy.learningScore = 0; copy.previous = initial.boundary;
                    initial.boundary = std::make_shared<SentenceBoundary>(std::move(copy));
                }
                auto starts = TextElementStarts(locked->text);
                for (std::size_t i = 0; i < starts.size(); ++i)
                {
                    auto end = i + 1 < starts.size() ? starts[i + 1] : locked->text.size();
                    auto target = std::u16string_view(locked->text).substr(starts[i], end - starts[i]);
                    initial.score += (history ? history->step(initial.history, target) : transition(initial.previous2, initial.previous1, target)) + settings.emittedReward;
                    if (supplement)
                    {
                        auto [state, reward] = supplement(initial.supplementState, target);
                        initial.supplementState = state; initial.supplementScore += reward; initial.score += reward;
                    }
                    initial.previous2 = initial.previous1; initial.previous1 = target;
                }
                initial.logMass = initial.score - initial.supplementScore;
                fromPosition = lockedRaw.size();
            }
            result.states[fromPosition].Add(std::move(initial));
        }
        for (std::size_t position = fromPosition; expand && position < result.raw.size(); ++position)
        {
            const auto& current = result.states[position].Limit(settings.beamWidth, settings.duplicateSingles || (learning && learning->affected));
            if (current.empty()) continue;
            auto edges = SentenceEdges(lexicon, result.raw, position, settings.duplicateSingles, minimumEnd, settings.autoSelectMinCodeLength);
            for (std::size_t start = 0; start < edges.size();)
            {
                auto end = start + 1;
                while (end < edges.size() && edges[end].codeLength == edges[start].codeLength) ++end;
                for (const auto& item : current)
                for (auto edgeIndex = start; edgeIndex < end; ++edgeIndex)
                {
                    canceled();
                    const auto& edge = edges[edgeIndex];
                    result.states[edge.end].InheritTruncation(result.states[position].Truncated());
                    result.states[edge.end].Add(AdvanceSentenceBeam(item, edge, transition, settings.rankPenalty,
                        settings.emittedReward, settings.wholeSingleReward, supplement, history,
                        settings.canonicalReward, settings.protectedIsolationFactor, learning, result.raw));
                    ++result.expanded;
                }
                start = end;
            }
        }
        const auto& completed = result.states.back().Limit(settings.beamWidth, settings.duplicateSingles || (learning && learning->affected));
        result.confidenceTruncated = result.states.back().Truncated();
        result.learningAffected = learning && learning->affected;
        bool scoreFirst = false;
        SentenceBeamBucket final;
        for (auto item : completed)
        {
            canceled();
            auto endingHistory = item.history;
            double ending = (history ? history->step(endingHistory, u"\x03") : transition(item.previous2, item.previous1, u"\x03"))
                - (isolation ? isolation(item.text) : 0);
            item.score += ending + item.codeScore;
            if (pathIsolation) item.score += (isolation ? isolation(item.text) : 0) - pathIsolation(item);
            if (item.source & 1) { item.score -= item.learningScore; item.learningScore = 0; }
            item.logMass += ending;
            item.earlyLogMass = item.logMass + std::min(.8, std::min(.75, item.supplementScore * .05) +
                ((item.source & 1) ? 0 : item.learningEarlyBonus));
            if (item.learningScore > 0) scoreFirst = true;
            item.maxRank = std::max(std::size_t{1}, item.maxRank);
            if (settings.duplicateSingles && item.boundary && item.boundary->previous) scoreFirst = true;
            result.confidencePool.push_back(item);
            final.Add(std::move(item));
        }
        result.candidates = final.Limit(settings.candidateLimit, scoreFirst, false);
        // Preserve dictionary order within direct entries, then merge the two
        // ordered subsequences using receipt-backed pair preferences.
        if (settings.lexicalWeight > 0 && result.candidates.size() > 1)
        {
            for (std::size_t i = 0; i < std::min(std::size_t{5}, result.candidates.size()); ++i)
            {
                auto& c = result.candidates[i];
                c.lexicalScore = SentenceLexicalPrior::Embedded().Score(c.text) * settings.lexicalWeight;
                c.score += c.lexicalScore;
            }
            SentenceBeamBucket ranked;
            for (const auto& c : result.candidates) ranked.Add(c);
            result.candidates = ranked.Limit(result.candidates.size(), scoreFirst, false);
        }
        ApplySentenceFusionOrdering(result.candidates, result.raw, learning ? learning->snapshot : nullptr,
            learning ? std::u16string_view(learning->mode) : std::u16string_view{});
        return result;
    }
}
