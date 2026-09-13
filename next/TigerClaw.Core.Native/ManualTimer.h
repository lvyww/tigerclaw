#pragma once
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>
#include <string_view>

namespace tiger::core
{
    std::optional<int> ManualTimerDelayMilliseconds(double minutes);
    std::optional<int> ManualTimerCommandDelay(std::u16string_view code);
    void ShowManualTimerPopup();
    // Owns one pending timer. Expired callbacks run independently, like .NET
    // Timer: cancellation/disposal does not close a popup already being shown.
    // The callback must own everything it needs; never capture this timer or
    // a raw pointer to a host that can be destroyed before the callback ends.
    class ManualTimer
    {
    public:
        explicit ManualTimer(std::function<void()> popup = ShowManualTimerPopup);
        ~ManualTimer();
        ManualTimer(const ManualTimer&) = delete;
        ManualTimer& operator=(const ManualTimer&) = delete;
        bool ScheduleMinutes(double minutes);
        bool ScheduleCommand(std::u16string_view code);
        void Cancel();
    private:
        void Run();
        void ScheduleDelay(int milliseconds);
        std::function<void()> _popup;
        std::mutex _mutex;
        std::condition_variable _changed;
        std::optional<std::chrono::steady_clock::time_point> _due;
        std::uint64_t _generation = 0;
        bool _stopping = false;
        std::thread _worker;
    };
}
