#pragma once
#include "SentenceBeam.h"
namespace tiger::core
{
    inline void ApplySentenceFusionOrdering(std::vector<SentenceBeamState>& candidates, std::u16string_view raw,
        const std::shared_ptr<const SentenceLearningSnapshot>& snapshot = {}, std::u16string_view mode = {})
    {
        (void)raw; (void)snapshot; (void)mode;
        ApplySentenceDirectRankOrdering(candidates);
    }
}
