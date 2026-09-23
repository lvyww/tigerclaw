#pragma once
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <mutex>
#include <thread>

namespace tiger::overlay
{
    inline double AnimationNow()
    {
        static const double frequency = [] { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return double(f.QuadPart); }();
        LARGE_INTEGER t{}; QueryPerformanceCounter(&t);
        return double(t.QuadPart) * 1000.0 / frequency;
    }

    // One-shot worker. All state and posting are serialized with UI cancellation.
    // Cancel also removes the queued wake, so repeated retargets cannot fill the queue.
    class AnimationWake
    {
        HANDLE timer_ = nullptr, stop_ = nullptr;
        std::thread worker_;
        std::mutex mutex_;
        HWND window_ = nullptr;
        UINT message_ = 0;
        UINT_PTR generation_ = 1;
        bool armed_ = false, posted_ = false, failed_ = false;
        double deadline_ = 0;
    public:
        ~AnimationWake() { Shutdown(); }
        bool Initialize(HWND window, UINT message, bool ordinary = false,
            decltype(&CreateWaitableTimerExW) create = CreateWaitableTimerExW)
        {
            if (worker_.joinable()) return true;
            window_ = window; message_ = message;
            timer_ = create(nullptr, nullptr, ordinary ? 0 : 2, TIMER_ALL_ACCESS);
            if (!timer_) timer_ = create(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
            stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!timer_ || !stop_) { Shutdown(); return false; }
            try { worker_ = std::thread([this] {
                HANDLE handles[]{stop_, timer_};
                while (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0 + 1)
                {
                    std::unique_lock<std::mutex> lock(mutex_);
                    // A signal consumed just before cancellation/rearming is not
                    // evidence that the new deadline has expired.
                    if (!armed_) continue;
                    if (AnimationNow() < deadline_)
                    {
                        // Ordinary timers may signal early at coarse clock ticks.
                        LARGE_INTEGER remaining{};
                        remaining.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(std::ceil((deadline_ - AnimationNow()) * 10000.0)));
                        if (SetWaitableTimer(timer_, &remaining, 0, nullptr, nullptr, FALSE)) continue;
                        // Let the UI finish if rearming failed.
                        failed_ = true;
                    }
                    armed_ = false;
                    const auto generation = generation_;
                    while (!posted_ && generation == generation_)
                    {
                        posted_ = PostMessageW(window_, message_, generation_, 0) != FALSE;
                        if (posted_) break;
                        failed_ = true;
                        lock.unlock();
                        auto stopped = WaitForSingleObject(stop_, 10) == WAIT_OBJECT_0;
                        lock.lock();
                        if (stopped) return;
                    }
                }
            }); }
            catch (...) { Shutdown(); return false; }
            return true;
        }
        bool Arm(double deadline, decltype(&SetWaitableTimer) set = SetWaitableTimer)
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (!timer_ || !worker_.joinable()) return false;
            if (posted_) return true; // queued UI work will sample current time
            deadline_ = deadline;
            LARGE_INTEGER due{};
            due.QuadPart = -std::max<LONGLONG>(1, static_cast<LONGLONG>(std::ceil((deadline - AnimationNow()) * 10000.0)));
            armed_ = set(timer_, &due, 0, nullptr, nullptr, FALSE) != FALSE;
            return armed_;
        }
        bool Take(UINT_PTR generation, bool* failed = nullptr)
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (generation != generation_ || !posted_) return false;
            if (failed) *failed = failed_;
            posted_ = false;
            return true;
        }
        void Cancel()
        {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                ++generation_; armed_ = posted_ = failed_ = false;
                if (timer_) CancelWaitableTimer(timer_);
            }
            // PeekMessage can dispatch sent messages: never hold our mutex here.
            MSG message{};
            if (window_) while (PeekMessageW(&message, window_, message_, message_, PM_REMOVE | PM_QS_POSTMESSAGE))
            {
                if (message.message == WM_QUIT) { PostQuitMessage(static_cast<int>(message.wParam)); break; }
                std::lock_guard<std::mutex> lock(mutex_);
                if (message.wParam == generation_ && posted_)
                {
                    // A reentrant UI update started a new animation during Peek.
                    posted_ = PostMessageW(window_, message_, generation_, 0) != FALSE;
                    break;
                }
            }
        }
        void Shutdown()
        {
            if (stop_) SetEvent(stop_);
            if (worker_.joinable()) worker_.join();
            Cancel();
            if (timer_) CloseHandle(timer_);
            if (stop_) CloseHandle(stop_);
            timer_ = stop_ = nullptr; window_ = nullptr;
        }
    };
}
