#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace tiger::overlay
{
    struct FrameRect
    {
        int x, y, width, height;
        bool operator==(const FrameRect& other) const
        { return x == other.x && y == other.y && width == other.width && height == other.height; }
        bool operator!=(const FrameRect& other) const { return !(*this == other); }
    };
    // Keep the same cadence for motion, growth and shrinkage.
    // Longer timelines use proportionally more steps. Late ticks skip steps
    // rather than extending the animation to play every nominal frame.
    class FrameTransition
    {
        FrameRect from_{}, to_{};
        std::uint64_t started_ = 0;
        unsigned frames_ = 6, interval_ = 4;
        unsigned duration_ = 200;
        bool active_ = false;
    public:
        void Start(FrameRect from, FrameRect to, std::uint64_t now, unsigned hz, unsigned durationMs = 200)
        {
            from_ = from; to_ = to; started_ = now;
            frames_ = std::clamp((hz + 5) / 10, 6u, 10u);
            interval_ = (20 + frames_ - 1) / frames_;
            duration_ = durationMs;
            frames_ = (frames_ * duration_ + 19) / 20;
            active_ = from != to && duration_ != 0;
        }
        bool Active() const { return active_; }
        FrameRect Target() const { return to_; }
        unsigned Interval() const { return interval_; }
        unsigned Duration() const { return duration_; }
        void Cancel() { active_ = false; }
        FrameRect Sample(std::uint64_t now)
        {
            if (!duration_) return to_;
            auto elapsed = std::min<std::uint64_t>(now >= started_ ? now - started_ : 0, duration_);
            auto frame = elapsed * frames_ / duration_;
            if (frame == frames_) { active_ = false; return to_; }
            double t = static_cast<double>(frame) / frames_;
            t = t * t * (3 - 2 * t);
            auto mix = [t](int a, int b) { return static_cast<int>(std::lround(a + (b - a) * t)); };
            return {mix(from_.x, to_.x), mix(from_.y, to_.y),
                mix(from_.width, to_.width), mix(from_.height, to_.height)};
        }
    };
}
