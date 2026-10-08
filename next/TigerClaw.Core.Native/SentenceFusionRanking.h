#pragma once
#include "SentenceBeam.h"
namespace tiger::core
{
    inline void ApplySentenceFusionOrdering(std::vector<SentenceBeamState>& candidates, std::u16string_view raw,
        const std::shared_ptr<const SentenceLearningSnapshot>& snapshot = {}, std::u16string_view mode = {})
    {
        auto original = candidates;
        std::vector<std::size_t> direct, composed;
        for (std::size_t i = 0; i < original.size(); ++i)
            ((original[i].source & 1) ? direct : composed).push_back(i);
        std::stable_sort(direct.begin(), direct.end(), [&](auto a, auto b) { return original[a].directRank < original[b].directRank; });
        candidates.clear();
        std::size_t di = 0, ci = 0;
        auto preference = [&](auto d, auto c) { return SentenceFusionPreference::signedScore(snapshot, mode, raw, original[d].text, original[c].text); };
        while (di < direct.size() && ci < composed.size())
        {
            double dp = 0, cp = 0;
            for (auto i = di; i < direct.size(); ++i) dp = std::max(dp, preference(direct[i], composed[ci]));
            for (auto i = ci; i < composed.size(); ++i) cp = std::max(cp, -preference(direct[di], composed[i]));
            bool takeDirect = std::abs(dp - cp) > 1e-12 ? dp > cp : direct[di] < composed[ci];
            candidates.push_back(original[takeDirect ? direct[di++] : composed[ci++]]);
        }
        while (di < direct.size()) candidates.push_back(original[direct[di++]]);
        while (ci < composed.size()) candidates.push_back(original[composed[ci++]]);
    }
}
