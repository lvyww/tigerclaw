#pragma once
#include <algorithm>
#include "SentenceEdges.h"
#include <functional>
#include <memory>

namespace tiger::core
{
    struct SentenceBoundary
    {
        std::shared_ptr<const SentenceBoundary> previous;
        std::size_t textLength, rawLength;
    };
    struct SentenceBeamState
    {
        double score = 0, logMass = 0, supplementScore = 0;
        std::u16string text, previous2, previous1;
        int supplementState = 0;
        std::size_t maxRank = 0;
        std::shared_ptr<const SentenceBoundary> boundary;
    };
    using SentenceTransition = std::function<double(std::u16string_view, std::u16string_view, std::u16string_view)>;
    using SentenceSupplementAdvance = std::function<std::pair<int, double>(int, std::u16string_view)>;
    inline SentenceBeamState AdvanceSentenceBeam(const SentenceBeamState& item, const SentenceEdge& edge,
        const SentenceTransition& transition, double rankPenalty, double emittedReward, double wholeSingleReward,
        const SentenceSupplementAdvance& supplement = {})
    {
        if (!transition || !edge.candidate) throw std::invalid_argument("Sentence edge and transition are required");
        const auto& candidate = *edge.candidate;
        auto result = item;
        double supplementAdded = 0;
        for (const auto& target : candidate.elements)
        {
            result.score += transition(result.previous2, result.previous1, target);
            result.score += emittedReward;
            if (supplement)
            {
                auto [state, reward] = supplement(result.supplementState, target);
                result.supplementState = state;
                result.score += reward;
                supplementAdded += reward;
            }
            result.previous2 = result.previous1;
            result.previous1 = target;
        }
        if (!edge.selectedRank) result.score -= rankPenalty * candidate.logRank;
        double singleAdded = edge.wholeInput && !edge.selectedRank && candidate.optimalSingle && candidate.elements.size() == 1
            ? wholeSingleReward : 0;
        result.score += singleAdded;
        result.logMass = item.logMass + (result.score - item.score - supplementAdded - singleAdded);
        result.supplementScore += supplementAdded;
        result.text += candidate.text;
        result.maxRank = std::max(item.maxRank, candidate.rank);
        result.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{item.boundary, result.text.size(), edge.end});
        return result;
    }
    class SentenceBeamBucket
    {
    public:
        void Add(SentenceBeamState item)
        {
            if (_frozen)
            {
                _frozen = false;
                for (std::size_t i = 0; i < _values.size(); ++i) _indices.emplace(_values[i].text, i);
            }
            auto [found, inserted] = _indices.emplace(item.text, _values.size());
            if (inserted) { _values.push_back(std::move(item)); return; }
            auto& previous = _values[found->second];
            double maximum = std::max(previous.logMass, item.logMass);
            double combined = maximum + std::log(std::exp(previous.logMass - maximum) + std::exp(item.logMass - maximum));
            // Duplicate representative always prefers rank, regardless of the
            // bucket's eventual score-first ordering; mass includes both paths.
            if (item.maxRank < previous.maxRank || (item.maxRank == previous.maxRank && item.score > previous.score))
                previous = std::move(item);
            previous.logMass = combined;
        }
        const std::vector<SentenceBeamState>& Limit(std::size_t limit, bool scoreFirst)
        {
            if (_frozen) return _values;
            limit = std::max(std::size_t{1}, limit);
            auto compareScore = [](double a, double b)
            {
                if (a == b) return 0;
                if (std::isnan(a)) return std::isnan(b) ? 0 : 1; // descending .NET Double.CompareTo
                if (std::isnan(b)) return -1;
                return a > b ? -1 : 1;
            };
            auto order = [&](const SentenceBeamState& a, const SentenceBeamState& b)
            {
                int rank = a.maxRank == b.maxRank ? 0 : a.maxRank < b.maxRank ? -1 : 1;
                int score = compareScore(a.score, b.score);
                if (scoreFirst ? score != 0 : rank != 0) return (scoreFirst ? score : rank) < 0;
                if (scoreFirst ? rank != 0 : score != 0) return (scoreFirst ? rank : score) < 0;
                return a.text < b.text;
            };
            if (_values.size() > limit)
            {
                std::partial_sort(_values.begin(), _values.begin() + limit, _values.end(), order);
                _values.resize(limit);
                _truncated = true;
            }
            else std::sort(_values.begin(), _values.end(), order);
            _indices.clear(); _indices.rehash(0);
            _frozen = true;
            return _values;
        }
        bool Truncated() const { return _truncated; }
    private:
        std::vector<SentenceBeamState> _values;
        std::unordered_map<std::u16string, std::size_t> _indices;
        bool _frozen = false, _truncated = false;
    };
}
