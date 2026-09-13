#pragma once
#include "CachedSentenceNgram.h"

namespace tiger::core
{
    // Boundary-disabled decoding still scores higher-order probabilities. Only
    // the unigram backoff contribution is removed, exactly as in the C# decoder.
    inline double ScoreSentenceNgramTransition(const CachedSentenceNgram& model,
        std::u16string_view previous2, std::u16string_view previous1,
        std::u16string_view target, bool scoreSentenceBoundaries)
    {
        bool boundary = previous2 == u"\x02" || previous1 == u"\x02" || target == u"\x03";
        return model.LogProbability(previous2, previous1, target, scoreSentenceBoundaries || !boundary);
    }
}
