#pragma once
#include "SentenceBeam.h"
#include <map>
#include <set>
#include <span>

namespace tiger::core
{
    struct SentencePrefixEvidence
    {
        std::u16string text;
        std::size_t rawLength;
        double share, boundaryShare;
        bool boundaryClosed;
        double baseShare = std::numeric_limits<double>::quiet_NaN();
    };
    // Input must be the already-narrowed evidence pool, not the complete Beam.
    // logMass excludes ranking-only supplement and whole-input single-character rewards.
    inline std::vector<SentencePrefixEvidence> BuildSentencePrefixEvidence(std::span<const SentenceBeamState> candidates)
    {
        if (candidates.empty()) return {};
        double maximum = candidates[0].logMass;
        for (const auto& candidate : candidates) maximum = std::max(maximum, candidate.logMass);
        auto early = [](const auto& c) { return std::isnan(c.earlyLogMass) ? c.logMass : c.earlyLogMass; };
        double earlyMaximum = early(candidates[0]);
        for (const auto& c : candidates) earlyMaximum = std::max(earlyMaximum, early(c));
        double total = 0, earlyTotal = 0;
        std::vector<SentencePrefixEvidence> output;
        std::map<std::pair<std::u16string, std::size_t>, std::size_t> indices;
        std::map<std::size_t, double> boundaryMass;
        for (const auto& candidate : candidates)
        {
            double weight = std::exp(candidate.logMass - maximum);
            total += weight;
            double earlyWeight = std::exp(early(candidate) - earlyMaximum);
            earlyTotal += earlyWeight;
            std::set<std::size_t> boundaries;
            for (auto boundary = candidate.boundary; boundary; boundary = boundary->previous)
            {
                if (boundary->textLength == 0 || boundary->textLength > candidate.text.size()) continue;
                auto text = candidate.text.substr(0, boundary->textLength);
                auto [found, inserted] = indices.emplace(std::make_pair(text, boundary->rawLength), output.size());
                if (inserted) output.push_back({std::move(text), boundary->rawLength, 0, 0, false, 0});
                output[found->second].share += earlyWeight;
                output[found->second].baseShare += weight;
                boundaries.insert(boundary->rawLength);
            }
            for (auto boundary : boundaries) boundaryMass[boundary] += weight;
        }
        if (total <= 0) return {};
        for (auto& item : output)
        {
            item.share /= earlyTotal;
            item.baseShare /= total;
            item.boundaryShare = boundaryMass[item.rawLength] / total;
            item.boundaryClosed = item.boundaryShare >= 0.99999;
        }
        return output;
    }
}
