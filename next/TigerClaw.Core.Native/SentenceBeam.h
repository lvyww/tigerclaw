#pragma once
#include <algorithm>
#include "SentenceEdges.h"
#include "SentenceLmHistory.h"
#include "SentenceLearning.h"
#include <functional>
#include <memory>
#include <limits>

namespace tiger::core
{
    struct SentenceBoundary
    {
        std::shared_ptr<const SentenceBoundary> previous;
        std::size_t textLength, rawLength;
        double learningScore = 0, codeScore = 0;
        bool protectsRareCharacter = false;
        std::size_t codeLength = 0;
    };
    struct SentenceLockedPrefix
    {
        std::u16string raw, text;
        std::shared_ptr<const SentenceBoundary> boundary;
    };
    struct SentenceBeamState
    {
        double score = 0, logMass = 0, supplementScore = 0;
        std::u16string text, previous2, previous1;
        int supplementState = 0;
        std::size_t maxRank = 0;
        std::shared_ptr<const SentenceBoundary> boundary;
        SentenceLmHistory history;
        double learningScore = 0, learningPotential = 0, learningEarlyBonus = 0, codeScore = 0, lexicalScore = 0;
        double earlyLogMass = std::numeric_limits<double>::quiet_NaN();
        unsigned source = 0; // 1: direct table entry, 2: composed path
        int directRank = std::numeric_limits<int>::max();
    };
    using SentenceTransition = std::function<double(std::u16string_view, std::u16string_view, std::u16string_view)>;
    using SentenceSupplementAdvance = std::function<std::pair<int, double>(int, std::u16string_view)>;
    inline double LearningEarlyContribution(double score)
    { return std::min(.75, std::max(0.0, score) * std::clamp((score - 9.0) / 4.0, 0.0, 1.0) * .075); }
    struct SentenceLearningQuery
    {
        std::shared_ptr<const SentenceLearningSnapshot> snapshot;
        std::u16string mode;
        bool affected = false;
    };
    inline SentenceBeamState AdvanceSentenceBeam(const SentenceBeamState& item, const SentenceEdge& edge,
        const SentenceTransition& transition, double rankPenalty, double emittedReward, double wholeSingleReward,
        const SentenceSupplementAdvance& supplement = {}, const SentenceHistoryTransition* history = nullptr,
        double canonicalReward = 0, double protectedFactor = 1,
        SentenceLearningQuery* learning = nullptr, std::u16string_view raw = {})
    {
        if (!transition || !edge.candidate) throw std::invalid_argument("Sentence edge and transition are required");
        const auto& candidate = *edge.candidate;
        auto result = item;
        double supplementAdded = 0;
        for (const auto& target : candidate.elements)
        {
            result.score += history ? history->step(result.history, target) : transition(result.previous2, result.previous1, target);
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
        double learned = item.learningScore;
        result.learningPotential = 0;
        result.text += candidate.text;
        bool direct = !item.boundary && edge.wholeInput;
        if (!direct && learning && learning->snapshot && !learning->snapshot->empty())
        {
            auto start = item.boundary;
            for (;;)
            {
                auto rawStart = start ? start->rawLength : 0, textStart = start ? start->textLength : 0;
                auto fragment = std::u16string_view(result.text).substr(textStart);
                if (learningCharacters(fragment) > 16) break;
                auto code = raw.substr(rawStart, edge.end - rawStart);
                auto context = learningContext(std::u16string_view(result.text).substr(0, textStart));
                auto reward = learning->snapshot->score(learning->mode, code, fragment, context);
                auto hint = learning->snapshot->prefixScore(learning->mode, code, fragment, context);
                if (hint > 0) { result.learningPotential = std::max(result.learningPotential, hint); learning->affected = true; }
                if (reward > 0)
                {
                    learning->affected = true;
                    learned = std::max(learned, (start ? start->learningScore : 0) + reward);
                    result.learningEarlyBonus = std::max(result.learningEarlyBonus,
                        LearningEarlyContribution(learning->snapshot->confidenceScore(learning->mode, code, fragment, context)));
                }
                if (!start) break;
                start = start->previous;
            }
        }
        auto learningAdded = learned - item.learningScore;
        result.score += learningAdded;
        result.learningScore = learned;
        result.logMass = item.logMass + (result.score - item.score - supplementAdded - singleAdded - learningAdded);
        result.supplementScore += supplementAdded;
        if (!edge.selectedRank && candidate.primarySingle && candidate.elements.size() == 1)
            result.codeScore += canonicalReward * edge.codeLength;
        result.maxRank = std::max(item.maxRank, candidate.rank);
        result.source = direct ? 1 : 2;
        result.directRank = direct ? static_cast<int>(candidate.rank) : std::numeric_limits<int>::max();
        result.boundary = std::make_shared<SentenceBoundary>(SentenceBoundary{item.boundary, result.text.size(), edge.end,
            result.learningScore, result.codeScore, protectedFactor < 1 && candidate.elements.size() == 1 &&
                (candidate.primarySingle || edge.selectedRank > 0), edge.codeLength});
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
            auto source = item.source | previous.source;
            auto directRank = std::min(item.directRank, previous.directRank);
            bool learned = item.learningScore > 0 || previous.learningScore > 0 || item.learningPotential > 0 || previous.learningPotential > 0;
            bool direct = (item.source & 1) != 0, oldDirect = (previous.source & 1) != 0;
            bool replace = direct != oldDirect ? direct : learned ? item.score + item.learningPotential > previous.score + previous.learningPotential :
                item.maxRank < previous.maxRank || (item.maxRank == previous.maxRank && item.score > previous.score);
            if (replace)
                previous = std::move(item);
            previous.logMass = combined;
            previous.source = source; previous.directRank = directRank;
        }
        const std::vector<SentenceBeamState>& Limit(std::size_t limit, bool scoreFirst, bool retainLearning = true)
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
                auto first = _values.begin() + limit;
                std::stable_sort(first, _values.end(), [](const auto& a, const auto& b)
                { return a.learningPotential > 0 && (b.learningPotential <= 0 || a.score + a.learningPotential > b.score + b.learningPotential); });
                auto retained = limit;
                while (retained < _values.size() && retainLearning && retained < limit + 4 && _values[retained].learningPotential > 0) ++retained;
                _values.resize(retained);
                _truncated = true;
            }
            else std::sort(_values.begin(), _values.end(), order);
            _indices.clear(); _indices.rehash(0);
            _frozen = true;
            return _values;
        }
        bool Truncated() const { return _truncated; }
        void InheritTruncation(bool truncated) { _truncated |= truncated; }
    private:
        std::vector<SentenceBeamState> _values;
        std::unordered_map<std::u16string, std::size_t> _indices;
        bool _frozen = false, _truncated = false;
    };
}
