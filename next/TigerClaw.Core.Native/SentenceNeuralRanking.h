#pragma once
#include "SentenceBeam.h"
#include <span>

namespace tiger::core
{
    inline double SentenceNeuralWeight(std::size_t length) { return length > 0 && length <= 2 ? .30 : .84; }
    inline double CombineSentenceNeuralScore(double base, double neural, std::size_t baseLength, std::size_t length)
    {
        if (baseLength < 2 || baseLength > 6) return base + SentenceNeuralWeight(baseLength) * neural;
        double weight = SentenceNeuralWeight(length);
        double alpha = length == 2 ? .15 : (length >= 3 && length <= 6 ? .30 : weight / (1 + weight));
        return (1 - alpha) * base + alpha * neural;
    }
    struct SentenceNeuralRank { std::size_t index; double finalScore; };
    // Scores map to the unchanged request order. Base score and confidence mass
    // are never overwritten by neural scores. Only the first five participate.
    inline std::vector<SentenceNeuralRank> RankSentenceNeural(std::span<const SentenceBeamState> candidates,
        std::span<const double> scores, bool duplicateSingles)
    {
        auto count = std::min(std::size_t{5}, candidates.size());
        if (scores.size() != count) throw std::invalid_argument("Wrong sentence neural score count");
        for (double score : scores)
            if (!std::isfinite(score)) throw std::invalid_argument("Nonfinite sentence neural score");
        if (!count) return {};
        bool scoreFirst = duplicateSingles && std::any_of(candidates.begin(), candidates.begin() + count,
            [](const auto& c) { return c.boundary && c.boundary->previous; });
        auto better = [&](std::size_t a, double sa, std::size_t b, double sb)
        {
            if (!scoreFirst && candidates[a].maxRank != candidates[b].maxRank) return candidates[a].maxRank < candidates[b].maxRank;
            if (sa != sb) return sa > sb;
            if (candidates[a].maxRank != candidates[b].maxRank) return candidates[a].maxRank < candidates[b].maxRank;
            return candidates[a].text < candidates[b].text;
        };
        std::size_t baseTop = 0;
        for (std::size_t i = 1; i < count; ++i)
            if (better(i, candidates[i].score, baseTop, candidates[baseTop].score)) baseTop = i;
        auto baseLength = TextElementStarts(candidates[baseTop].text).size();
        std::vector<SentenceNeuralRank> ranks;
        for (std::size_t i = 0; i < count; ++i)
            ranks.push_back({i, CombineSentenceNeuralScore(candidates[i].score, scores[i], baseLength,
                TextElementStarts(candidates[i].text).size())});
        std::sort(ranks.begin(), ranks.end(), [&](auto a, auto b) { return better(a.index, a.finalScore, b.index, b.finalScore); });
        return ranks;
    }
}
