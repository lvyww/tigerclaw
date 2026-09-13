#pragma once
#include "TextElements.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <map>
#include <queue>
#include <span>
#include <stdexcept>
#include <string>

namespace tiger::core
{
    struct SentenceSupplementEntry
    {
        std::u16string text;
        std::int64_t weight;
        double reward;
        static SentenceSupplementEntry Create(std::u16string text, std::int64_t weight)
        {
            weight = std::clamp(weight, std::int64_t{1}, std::int64_t{1000000000});
            return {std::move(text), weight, std::clamp(9.0 + 2.0 * std::log(weight / 1000.0), 0.0, 16.0)};
        }
    };
    class SentenceSupplementMatcher
    {
        struct Node
        {
            std::map<std::u16string, int, std::less<>> transitions;
            int failure = 0;
            double reward = 0;
        };
        std::vector<Node> _nodes{1};
    public:
        bool IsEmpty() const { return _nodes.size() <= 1; }
        static SentenceSupplementMatcher Build(std::span<const SentenceSupplementEntry> entries)
        {
            SentenceSupplementMatcher result;
            auto& nodes = result._nodes;
            for (const auto& entry : entries)
            {
                if (entry.text.empty() || entry.reward <= 0) continue;
                int state = 0;
                auto starts = TextElementStarts(entry.text);
                for (std::size_t i = 0; i < starts.size(); ++i)
                {
                    auto end = i + 1 < starts.size() ? starts[i + 1] : entry.text.size();
                    auto element = entry.text.substr(starts[i], end - starts[i]);
                    auto found = nodes[state].transitions.find(element);
                    if (found == nodes[state].transitions.end())
                    {
                        if (nodes.size() >= static_cast<std::size_t>(std::numeric_limits<int>::max()))
                            throw std::length_error("Too many sentence supplement states");
                        int next = static_cast<int>(nodes.size());
                        nodes[state].transitions.emplace(std::move(element), next);
                        nodes.emplace_back();
                        state = next;
                    }
                    else state = found->second;
                }
                nodes[state].reward = std::max(nodes[state].reward, entry.reward);
            }
            std::queue<int> pending;
            for (const auto& [element, child] : nodes[0].transitions) pending.push(child);
            while (!pending.empty())
            {
                int current = pending.front(); pending.pop();
                for (const auto& [element, child] : nodes[current].transitions)
                {
                    int fallback = nodes[current].failure;
                    while (fallback != 0 && !nodes[fallback].transitions.contains(element)) fallback = nodes[fallback].failure;
                    auto found = nodes[fallback].transitions.find(element);
                    nodes[child].failure = found != nodes[fallback].transitions.end() && found->second != child ? found->second : 0;
                    nodes[child].reward = std::max(nodes[child].reward, nodes[nodes[child].failure].reward);
                    pending.push(child);
                }
            }
            return result;
        }
        std::pair<int, double> Advance(int state, std::u16string_view element) const
        {
            if (IsEmpty() || element.empty()) return {0, 0};
            int current = state >= 0 && static_cast<std::size_t>(state) < _nodes.size() ? state : 0;
            while (current != 0 && !_nodes[current].transitions.contains(element)) current = _nodes[current].failure;
            auto found = _nodes[current].transitions.find(element);
            if (found != _nodes[current].transitions.end()) current = found->second;
            return {current, _nodes[current].reward};
        }
    };
}
