#pragma once
#include "SentenceLattice.h"
#include "SentencePrefixEvidence.h"

namespace tiger::core
{
    struct SentenceEarlyEvidence
    {
        std::vector<SentencePrefixEvidence> prefixes;
        bool neutralIncompleteTail = false, mergedIncompleteTail = false, neutralLowConfidence = false;
        bool confidenceTruncated = false;
        std::u16string proposal;
        double proposalShare = 0;
        std::map<std::u16string, std::size_t> rawLengths;
    };
    inline SentenceEarlyEvidence BuildSentenceEarlyEvidence(const SentenceLexicon& lexicon,
        SentenceLatticeResult& lattice, const SentenceTransition& transition,
        const SentenceLatticeSettings& settings = {},
        const std::function<double(std::u16string_view)>& isolation = {}, std::u16string_view required = {},
        const SentenceHistoryTransition* history = nullptr)
    {
        SentenceEarlyEvidence result;
        if (lattice.raw.empty() || lattice.states.empty()) return result;
        result.confidenceTruncated = lattice.confidenceTruncated;
        if (result.confidenceTruncated && !settings.preserveTruncatedEvidence) return result;
        std::vector<SentenceBeamState> visible, pool;
        std::map<std::pair<std::u16string, std::size_t>, std::size_t> indices;
        auto add = [&](const SentenceBeamState& candidate)
        {
            if (candidate.text.empty()) return;
            auto endpoint = candidate.boundary ? candidate.boundary->rawLength : 0;
            auto [found, inserted] = indices.emplace(std::make_pair(candidate.text, endpoint), pool.size());
            if (inserted) { pool.push_back(candidate); return; }
            auto& previous = pool[found->second];
            double maximum = std::max(previous.logMass, candidate.logMass);
            double combined = maximum + std::log(std::exp(previous.logMass - maximum) + std::exp(candidate.logMass - maximum));
            auto early = [](const auto& c) { return std::isnan(c.earlyLogMass) ? c.logMass : c.earlyLogMass; };
            double earlyMaximum = std::max(early(previous), early(candidate));
            double earlyCombined = earlyMaximum + std::log(std::exp(early(previous) - earlyMaximum) + std::exp(early(candidate) - earlyMaximum));
            if (early(candidate) > early(previous)) previous = candidate;
            previous.earlyLogMass = earlyCombined;
            previous.logMass = combined;
        };
        for (const auto& candidate : lattice.confidencePool.empty() ? lattice.candidates : lattice.confidencePool)
            if (candidate.text.starts_with(required)) { visible.push_back(candidate); add(candidate); }
        auto maximumCode = lexicon.CodeLengths().empty() ? 1 : lexicon.CodeLengths().back();
        auto maximumTail = std::min(maximumCode - 1, lattice.raw.size() - 1);
        for (std::size_t length = 1; length <= maximumTail; ++length)
        {
            auto consumed = lattice.raw.size() - length;
            auto tail = std::u16string_view(lattice.raw).substr(consumed);
            bool letters = std::all_of(tail.begin(), tail.end(), [](char16_t unit) { return HasSentenceLetter(std::u16string_view(&unit, 1)); });
            if (!letters || !lexicon.IsProperPrefix(tail) || (tail.size() >= 2 && lexicon.Candidates(tail))) continue;
            const auto& partial = lattice.states[consumed].Limit(settings.beamWidth, settings.duplicateSingles);
            if (!settings.preserveTruncatedEvidence && (result.confidenceTruncated || lattice.states[consumed].Truncated()))
            {
                if (std::any_of(partial.begin(), partial.end(), [&](const auto& c) { return c.text.starts_with(required); }))
                { result.mergedIncompleteTail = true; result.confidenceTruncated |= lattice.states[consumed].Truncated(); }
                continue;
            }
            bool added = false;
            for (auto item : partial)
            {
                auto endingHistory = item.history;
                double ending = (history ? history->step(endingHistory, u"\x03") : transition(item.previous2, item.previous1, u"\x03"))
                    - (isolation ? isolation(item.text) : 0);
                item.score += ending;
                item.logMass += ending;
                item.earlyLogMass = item.logMass + std::min(.8, std::min(.75, item.supplementScore * .05) +
                    ((item.source & 1) ? 0 : item.learningEarlyBonus));
                if (!item.text.starts_with(required)) continue;
                add(item);
                added = true;
            }
            if (added)
            {
                result.mergedIncompleteTail = true;
                result.confidenceTruncated |= lattice.states[consumed].Truncated();
            }
        }
        result.neutralIncompleteTail = visible.empty() && result.mergedIncompleteTail;
        if (!visible.empty())
        {
            double maximum = visible[0].logMass, total = 0;
            for (const auto& candidate : visible) maximum = std::max(maximum, candidate.logMass);
            for (const auto& candidate : visible) total += std::exp(candidate.logMass - maximum);
            result.neutralLowConfidence = total > 0 && 1.0 / total < 0.99;
        }
        if (!result.confidenceTruncated || settings.preserveTruncatedEvidence) result.prefixes = BuildSentencePrefixEvidence(pool);
        const SentencePrefixEvidence* best = nullptr;
        std::size_t bestLength = 0;
        std::map<std::u16string, double> shares;
        for (const auto& prefix : result.prefixes)
        {
            if (!prefix.boundaryClosed) continue;
            auto found = shares.find(prefix.text);
            if (found == shares.end() || prefix.share > found->second ||
                (prefix.share == found->second && prefix.rawLength < result.rawLengths[prefix.text]))
            { shares[prefix.text] = prefix.share; result.rawLengths[prefix.text] = prefix.rawLength; }
            if (prefix.share < 0.99) continue;
            auto length = TextElementStarts(prefix.text).size();
            if (!best || length > bestLength || (length == bestLength && (prefix.share > best->share ||
                (prefix.share == best->share && prefix.rawLength < best->rawLength))))
            { best = &prefix; bestLength = length; }
        }
        if (best) { result.proposal = best->text; result.proposalShare = best->share; }
        return result;
    }
}
