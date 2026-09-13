#pragma once
#include "SentenceNgram.h"
#include "FixedScoreCache.h"

namespace tiger::core
{
    // Owner must serialize calls. Raw SentenceNgram remains cache-free for
    // independent verification and concurrent immutable queries.
    class CachedSentenceNgram
    {
    public:
        explicit CachedSentenceNgram(const SentenceNgram& model) : _model(model) {}
        double LogProbability(std::u16string_view a, std::u16string_view b, std::u16string_view c, bool unigram = true) const
        {
            auto key = SentenceNgram::Triple(SentenceNgram::Scalar(a), SentenceNgram::Scalar(b), SentenceNgram::Scalar(c));
            if (!unigram) key |= std::uint64_t{1} << 63;
            double value;
            if (_scores.Get(key, value)) return value;
            value = _model.LogProbability(a, b, c, unigram);
            _scores.Set(key, value);
            return value;
        }
        bool HasObservedBigram(std::u16string_view a, std::u16string_view b) const
        {
            auto key = SentenceNgram::Pair(SentenceNgram::Scalar(a), SentenceNgram::Scalar(b));
            bool value;
            if (_observed.Get(key, value)) return value;
            value = _model.HasObservedBigram(a, b);
            _observed.Set(key, value);
            return value;
        }
        void Clear() { _scores.Clear(); _observed.Clear(); }
    private:
        const SentenceNgram& _model;
        mutable FixedScoreCache<double> _scores{1 << 18};
        mutable FixedScoreCache<bool> _observed{1 << 16};
    };
}
