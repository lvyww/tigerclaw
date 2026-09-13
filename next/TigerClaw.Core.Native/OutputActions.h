#pragma once
#include <string>
#include <string_view>
#include <functional>
#include <cstddef>
namespace tiger::core
{
    struct OutputClock
    {
        int year, month, day, hour, minute, second, dayOfWeek; // Sunday = 0
        std::u16string localizedDayName;
    };
    struct OutputContext
    {
        bool dotAfterDigit = false;
        std::u16string_view repeatBuffer;
        std::function<std::size_t(std::size_t)> randomIndex;
        std::function<OutputClock()> clock;
    };
    enum class OutputAction { Text, OpenAddWord, ToggleHideCandidates };
    struct OutputResult
    {
        OutputAction action = OutputAction::Text;
        std::u16string text;
    };
    // Accept already unpacked commit text. Actions are returned, not executed.
    // Clock/random are lazy host dependencies; no process-global state here.
    OutputResult NormalizeOutputAction(std::u16string_view text, const OutputContext& context);
}
