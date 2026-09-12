#pragma once
#include <algorithm>
#include <cstdint>
#include <string>
#include <stdexcept>

namespace tiger::overlay
{
    struct Point { int x = 0, y = 0; };
    struct WorkArea {
        int left = 0, top = 0, right = 0, bottom = 0;
        bool operator==(const WorkArea& b) const {
            return left == b.left && top == b.top && right == b.right && bottom == b.bottom;
        }
    };
    inline Point ClampCandidate(Point current, int width, int height, WorkArea work)
    {
        using Wide = std::int64_t;
        return {static_cast<int>(std::clamp<Wide>(current.x, work.left, std::max<Wide>(work.left, Wide(work.right) - width - 2))),
            static_cast<int>(std::clamp<Wide>(current.y, work.top, std::max<Wide>(work.top, Wide(work.bottom) - height - 2)))};
    }
    inline Point StatusPosition(Point current, int width, int height, WorkArea work, bool initial)
    {
        return ClampCandidate(initial ? Point{work.right, work.bottom} : current, width, height, work);
    }
    inline bool UsableCaret(int x, int y)
    {
        return (x || y) && x > -30000 && x < 300000 && y > -30000 && y < 300000;
    }
    struct PlacementEnvironment
    {
        std::int64_t revision = 0;
        std::u16string instance;
        std::uintptr_t owner = 0, foreground = 0, focus = 0, monitor = 0;
        unsigned processId = 0, dpi = 96;
        WorkArea work{}, ownerBounds{}, foregroundBounds{};
        bool uncertain = false, startMenu = false;
        bool operator==(const PlacementEnvironment& b) const {
            return revision == b.revision && instance == b.instance && owner == b.owner && foreground == b.foreground &&
                focus == b.focus && monitor == b.monitor && processId == b.processId && dpi == b.dpi &&
                work == b.work && ownerBounds == b.ownerBounds && foregroundBounds == b.foregroundBounds &&
                uncertain == b.uncertain && startMenu == b.startMenu;
        }
    };
    // Lives for the focused input environment, not one candidate presentation.
    // Resolve on a copy; accept only with a successfully published current frame.
    class Placement
    {
        bool anchored_ = false, above_ = false, placed_ = false;
        Point anchor_{}, live_{};
        int caretHeight_ = 20, referenceHeight_ = 20, referenceY_ = 0;
        PlacementEnvironment environment_{};
    public:
        void EndComposition() { anchored_ = false; }
        void Reset() { *this = {}; }
        void RefreshAnchor() { EndComposition(); }
        bool Acquire(int x, int y, int height)
        {
            if (!UsableCaret(x, y)) return false;
            live_ = {x, y}; caretHeight_ = std::clamp(height > 0 ? height : 20, 1, 300000);
            if (!anchored_) { anchor_ = live_; anchored_ = true; }
            // Preserve the composition's X anchor, but do not freeze live Y.
            anchor_.y = y;
            return true;
        }
        Point Anchor() const { return anchor_; }
        bool IsAbove() const { return placed_ && above_; }
        bool SameEnvironment(const PlacementEnvironment& env) const { return placed_ && environment_ == env && !env.uncertain; }
        Point Resolve(int width, int height, WorkArea work, bool startMenu = false)
        {
            PlacementEnvironment env; env.work = work; env.startMenu = startMenu;
            return Resolve(width, height, env);
        }
        Point Resolve(int width, int height, const PlacementEnvironment& env)
        {
            using Wide = std::int64_t;
            const auto& work = env.work;
            if (!anchored_ || width <= 0 || height <= 0 || work.right <= work.left || work.bottom <= work.top)
                throw std::invalid_argument("Invalid candidate placement geometry");
            if (!SameEnvironment(env)) {
                above_ = false; referenceY_ = live_.y; referenceHeight_ = caretHeight_; anchor_ = live_;
            } else {
                const Wide epsilon = std::min<Wide>((Wide(env.dpi ? env.dpi : 96) * 3 + 48) / 96,
                    std::min(caretHeight_, referenceHeight_) / 4);
                const Wide delta = Wide(live_.y) - referenceY_;
                if (delta < -epsilon) {
                    above_ = false; referenceY_ = live_.y; referenceHeight_ = caretHeight_;
                    anchor_.x = live_.x;
                } else if (delta > epsilon) {
                    referenceY_ = live_.y; referenceHeight_ = caretHeight_; anchor_.x = live_.x;
                }
                // Do not chase samples within the band: slow upward motion must
                // eventually cross the stable reference. Downward motion inherits.
            }
            const Wide below = Wide(live_.y) + 5, above = Wide(live_.y) - caretHeight_ - 5 - height;
            const Wide right = std::max<Wide>(work.left, Wide(work.right) - width - 2);
            const Wide bottom = std::max<Wide>(work.top, Wide(work.bottom) - height - 2);
            const bool fitsBelow = below >= work.top && below + height <= Wide(work.bottom) - 2;
            const bool fitsAbove = above >= work.top && above + height <= Wide(work.bottom) - 2;
            if (!(above_ && fitsAbove)) {
                if (fitsBelow) above_ = false;
                else if (fitsAbove) above_ = true;
                else above_ = Wide(live_.y) - caretHeight_ - work.top > Wide(work.bottom) - live_.y;
            }
            if (env.startMenu) above_ = false;
            const Wide x = env.startMenu ? Wide(work.left) + 10 : anchor_.x;
            const Wide y = env.startMenu ? Wide(work.top) + 10 : (above_ ? above : below);
            environment_ = env; placed_ = true;
            return {static_cast<int>(std::clamp<Wide>(x, work.left, right)),
                static_cast<int>(std::clamp<Wide>(y, work.top, bottom))};
        }
    };
}
