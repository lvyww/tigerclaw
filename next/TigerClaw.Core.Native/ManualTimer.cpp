#include "ManualTimer.h"
#include "UpperCaseComposition.h"
#include <charconv>
#include <cmath>
#include <limits>
#include <stdexcept>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    std::optional<int> ManualTimerCommandDelay(std::u16string_view code)
    {
        if (ClassifyUpperCaseCommand(code) != UpperCaseCommand::Timer) return {};
        std::string number;
        for (char16_t c : code.substr(2))
            if (c != u',' && c != u'\n') number.push_back(static_cast<char>(c));
        if (number.empty()) return {};
        double minutes = 0;
        auto parsed = std::from_chars(number.data(), number.data() + number.size(), minutes, std::chars_format::fixed);
        if (parsed.ptr != number.data() + number.size()) return {};
        if (parsed.ec == std::errc::result_out_of_range)
        {
            // The accepted command grammar has no sign or exponent. Therefore
            // an out-of-range value >= 1 is overflow; one below 1 is underflow.
            bool integerNonzero = false;
            for (char c : number)
            {
                if (c == '.') break;
                if (c >= '1' && c <= '9') integerNonzero = true;
            }
            minutes = integerNonzero ? std::numeric_limits<double>::infinity() : 0.0;
        }
        else if (parsed.ec != std::errc{}) return {};
        return ManualTimerDelayMilliseconds(minutes);
    }
    std::optional<int> ManualTimerDelayMilliseconds(double minutes)
    {
        if (minutes <= 0) return {};
        double milliseconds = minutes * 60.0 * 1000.0;
        if (!std::isfinite(milliseconds) || milliseconds > std::numeric_limits<int>::max())
            return std::numeric_limits<int>::max();
        return static_cast<int>(milliseconds);
    }
    void ShowManualTimerPopup()
    {
#ifdef _WIN32
        MessageBoxW(nullptr, L"\u65f6\u95f4\u5dee\u4e0d\u591a\u54af\uff01", L"\u8ba1\u65f6\u5668",
            MB_OK | MB_ICONINFORMATION | MB_DEFAULT_DESKTOP_ONLY); // Timer reminder
#else
        throw std::runtime_error("Desktop timer popup requires Windows");
#endif
    }
    ManualTimer::ManualTimer(std::function<void()> popup) : _popup(std::move(popup))
    {
        if (!_popup) throw std::invalid_argument("Timer callback is required");
    }
    ManualTimer::~ManualTimer()
    {
        {
            std::lock_guard guard(_mutex);
            _stopping = true;
            _due.reset();
        }
        _changed.notify_one();
        if (_worker.joinable()) _worker.join();
    }
    bool ManualTimer::ScheduleMinutes(double minutes)
    {
        auto delay = ManualTimerDelayMilliseconds(minutes);
        if (!delay) return false; // Invalid requests must not cancel the prior timer.
        ScheduleDelay(*delay);
        return true;
    }
    bool ManualTimer::ScheduleCommand(std::u16string_view code)
    {
        auto delay = ManualTimerCommandDelay(code);
        if (!delay) return false;
        ScheduleDelay(*delay);
        return true;
    }
    void ManualTimer::ScheduleDelay(int milliseconds)
    {
        {
            std::lock_guard guard(_mutex);
            if (!_worker.joinable()) _worker = std::thread([this] { Run(); });
            _due = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
            ++_generation;
        }
        _changed.notify_one();
    }
    void ManualTimer::Cancel()
    {
        {
            std::lock_guard guard(_mutex);
            _due.reset();
            ++_generation;
        }
        _changed.notify_one();
    }
    void ManualTimer::Run()
    {
        std::unique_lock lock(_mutex);
        while (!_stopping)
        {
            if (!_due)
            {
                _changed.wait(lock, [&] { return _stopping || _due.has_value(); });
                continue;
            }
            auto generation = _generation;
            auto deadline = *_due;
            if (_changed.wait_until(lock, deadline, [&] { return _stopping || generation != _generation; })) continue;
            _due.reset();
            // Capture a standalone callback while locked. Destruction waits for
            // this worker only, never a user dismissing a dialog. A replacement
            // timer can expire while an earlier popup is still open.
            try
            {
                std::thread([popup = _popup]
                {
                    try { popup(); } catch (...) { }
                }).detach();
            }
            catch (...) { } // Popup failures are best effort, as in the reference.
        }
    }
}
