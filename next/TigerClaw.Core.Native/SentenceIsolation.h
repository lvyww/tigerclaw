#pragma once
#include "TextElements.h"
#include "SentenceBeam.h"
#include <algorithm>
#include <cmath>
#include <functional>
#include <stdexcept>

namespace tiger::core
{
    struct SentenceIsolationSettings
    {
        int rankThreshold = 3000;
        double lambda = 2.0;
        bool useLogRank = false;
        bool Enabled() const { return rankThreshold > 0 && lambda > 0; }
    };
    using SentenceCharacterRank = std::function<int(std::u16string_view)>;
    using SentenceObservedBigram = std::function<bool(std::u16string_view, std::u16string_view)>;
    inline double SentenceIsolationPenalty(std::u16string_view text,
        const SentenceCharacterRank& rank, const SentenceObservedBigram& observed,
        const SentenceIsolationSettings& settings = {},
        const SentenceBoundary* boundary = nullptr, double protectedFactor = 1, std::size_t minimumCode = 4)
    {
        if (!settings.Enabled() || !observed || text.empty()) return 0;
        if (!rank) throw std::invalid_argument("Sentence character rank provider is required");
        auto starts = TextElementStarts(text);
        auto element = [&](std::size_t i)
        {
            auto end = i + 1 < starts.size() ? starts[i + 1] : text.size();
            return text.substr(starts[i], end - starts[i]);
        };
        double penalty = 0;
        for (std::size_t i = 0; i < starts.size(); ++i)
        {
            auto current = element(i);
            int currentRank = rank(current);
            if (currentRank <= settings.rankThreshold) continue;
            bool left = i > 0 && observed(element(i - 1), current);
            bool right = i + 1 < starts.size() && observed(current, element(i + 1));
            if (left || right) continue;
            double weight = settings.lambda;
            if (settings.useLogRank)
                weight *= std::log(std::max(double(currentRank), double(settings.rankThreshold) + 1) / settings.rankThreshold);
            for (auto b = boundary; b; b = b->previous.get())
            {
                auto begin = b->previous ? b->previous->textLength : 0;
                if (starts[i] >= begin && starts[i] < b->textLength)
                {
                    if (b->protectsRareCharacter && b->codeLength >= minimumCode) weight *= protectedFactor;
                    break;
                }
            }
            penalty += weight;
        }
        return penalty;
    }
}
