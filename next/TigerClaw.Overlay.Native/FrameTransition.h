#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace tiger::overlay
{
    class RefreshRateCache
    {
        std::uintptr_t monitor_ = 0;
        double checked_ = 0;
        unsigned hz_ = 60;
        bool valid_ = false;
    public:
        void Invalidate() { valid_ = false; }
        template<class Query> unsigned Get(std::uintptr_t monitor, double now, Query query)
        {
            if (!valid_ || monitor != monitor_ || now - checked_ >= 2000)
            {
                auto hz = query();
                hz_ = hz >= 30 && hz <= 1000 ? hz : 60;
                monitor_ = monitor; checked_ = now; valid_ = true;
            }
            return hz_;
        }
    };
    struct FrameRect
    {
        int x, y, width, height;
        bool operator==(const FrameRect& other) const
        { return x == other.x && y == other.y && width == other.width && height == other.height; }
        bool operator!=(const FrameRect& other) const { return !(*this == other); }
    };
    // Milliseconds from a monotonic clock; sampling is independent of cadence.
    class FrameTransition
    {
        FrameRect from_{}, to_{};
        double started_ = 0;
        unsigned hz_ = 60;
        unsigned duration_ = 200;
        bool active_ = false;
    public:
        void Start(FrameRect from, FrameRect to, double now, unsigned hz, unsigned durationMs = 200)
        {
            from_ = from; to_ = to; started_ = now;
            hz_ = hz >= 30 && hz <= 1000 ? hz : 60;
            duration_ = durationMs;
            active_ = from != to && duration_ != 0;
        }
        bool Active() const { return active_; }
        FrameRect Target() const { return to_; }
        double Interval() const { return 1000.0 / hz_; }
        double End() const { return started_ + duration_; }
        // Anchor every deadline to the original epoch, never to a late callback.
        double Next(double now, unsigned hz) const
        {
            double interval = 1000.0 / (hz >= 30 && hz <= 1000 ? hz : 60);
            return std::min(End(), started_ + (std::floor(std::max(0.0, now - started_) / interval) + 1) * interval);
        }
        unsigned Duration() const { return duration_; }
        void Cancel() { active_ = false; }
        FrameRect Sample(double now)
        {
            if (!duration_) return to_;
            auto elapsed = std::clamp(now - started_, 0.0, static_cast<double>(duration_));
            if (elapsed == duration_) { active_ = false; return to_; }
            double t = elapsed / duration_;
            t = t * t * (3 - 2 * t);
            auto mix = [t](int a, int b) { return static_cast<int>(std::lround(a + (b - a) * t)); };
            return {mix(from_.x, to_.x), mix(from_.y, to_.y),
                mix(from_.width, to_.width), mix(from_.height, to_.height)};
        }
    };
}
