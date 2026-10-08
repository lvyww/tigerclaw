#pragma once
#include <array>
#include <algorithm>
#include <cstdint>
#include <functional>
#include <string_view>

namespace tiger::core
{
    // Same value-state convention as C#: newest token first, BOS once.
    struct SentenceLmHistory
    {
        std::array<std::uint32_t, 4> tokens{};
        int count = 0;
        SentenceLmHistory Append(std::uint32_t token) const
        { return {{{token, tokens[0], tokens[1], tokens[2]}}, std::min(4, count + 1)}; }
        bool operator==(const SentenceLmHistory&) const = default;
    };
    struct SentenceHistoryTransition
    {
        SentenceLmHistory begin;
        std::function<double(SentenceLmHistory&, std::u16string_view)> step;
    };
}
