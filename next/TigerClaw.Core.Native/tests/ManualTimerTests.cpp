#include "ManualTimer.h"
#include <future>
#include <atomic>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>
using namespace tiger::core;
using namespace std::chrono_literals;
static void Check(bool condition) { if (!condition) throw std::runtime_error("Manual timer regression"); }
int main()
{
    try
    {
        Check(!ManualTimerDelayMilliseconds(0) && !ManualTimerDelayMilliseconds(-1));
        Check(ManualTimerDelayMilliseconds(1) == 60000);
        Check(ManualTimerDelayMilliseconds(0.000001) == 0);
        Check(ManualTimerDelayMilliseconds(std::numeric_limits<double>::infinity()) == 2147483647);
        Check(ManualTimerDelayMilliseconds(std::numeric_limits<double>::quiet_NaN()) == 2147483647);
        Check(ManualTimerCommandDelay(u"Ds1,000") == 60000000);
        Check(ManualTimerCommandDelay(u"DS.000001\n") == 0);
        Check(!ManualTimerCommandDelay(u"Ds,,") && !ManualTimerCommandDelay(u"Ds0"));
        Check(!ManualTimerCommandDelay(u"ds1") && !ManualTimerCommandDelay(u"Ds1\r\n"));
        auto fired = std::make_shared<std::promise<void>>();
        auto future = fired->get_future();
        {
            ManualTimer timer([fired] { fired->set_value(); });
            Check(timer.ScheduleCommand(u"Ds60"));
            Check(timer.ScheduleCommand(u"Ds.000001"));
            Check(future.wait_for(2s) == std::future_status::ready);
        }
        auto canceled = std::make_shared<std::promise<void>>();
        auto canceledFuture = canceled->get_future();
        {
            ManualTimer timer([canceled] { canceled->set_value(); });
            Check(timer.ScheduleMinutes(60));
            Check(!timer.ScheduleMinutes(0));
            timer.Cancel();
        }
        Check(canceledFuture.wait_for(20ms) == std::future_status::timeout);
        auto retained = std::make_shared<std::promise<void>>();
        auto retainedFuture = retained->get_future();
        {
            ManualTimer timer([retained] { retained->set_value(); });
            timer.ScheduleCommand(u"Ds.0005");
            Check(!timer.ScheduleCommand(u"Ds,,"));
            Check(retainedFuture.wait_for(2s) == std::future_status::ready);
        }
        // A running callback must neither serialize subsequent expirations nor
        // hold the timer destructor waiting for the popup to close.
        auto release = std::make_shared<std::promise<void>>();
        auto gate = release->get_future().share();
        auto count = std::make_shared<std::atomic<int>>(0);
        auto twice = std::make_shared<std::promise<void>>();
        auto twiceFuture = twice->get_future();
        auto first = std::make_shared<std::promise<void>>();
        auto firstFuture = first->get_future();
        {
            ManualTimer timer([count, twice, first, gate]
            {
                int calls = ++*count;
                if (calls == 1) first->set_value();
                if (calls == 2) twice->set_value();
                gate.wait();
            });
            timer.ScheduleMinutes(0.000001);
            Check(firstFuture.wait_for(2s) == std::future_status::ready);
            timer.ScheduleMinutes(0.000001);
            Check(twiceFuture.wait_for(2s) == std::future_status::ready);
        }
        release->set_value();
        std::cout << "Manual timer tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
