#pragma once
#include "OutputActions.h"
#include <random>
namespace tiger::core
{
    std::u16string LocalizedOutputDayName(int dayOfWeek, const std::string* locale = nullptr);
    OutputClock ReadLocalOutputClock(const std::string* locale = nullptr);
    // Owned by the serialized input engine, not a process-global generator.
    class OutputRandom
    {
    public:
        OutputRandom();
        std::size_t Choose(std::size_t count);
    private:
        std::mt19937_64 _generator;
    };
}
