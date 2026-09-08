#pragma once
#include <algorithm>

namespace tiger::overlay
{
    struct Point { int x = 0, y = 0; };
    struct WorkArea { int left, top, right, bottom; };
    inline bool UsableCaret(int x, int y)
    {
        return (x || y) && x > -30000 && x < 300000 && y > -30000 && y < 300000;
    }
    class Placement
    {
        bool anchored_ = false, above_ = false, belowLocked_ = false, placed_ = false;
        Point anchor_;
        int caretHeight_ = 20;
    public:
        void Reset() { *this = {}; }
        void RefreshAnchor()
        {
            if (placed_) belowLocked_ = !above_;
            anchored_ = false;
        }
        bool Acquire(int x, int y, int height)
        {
            // Invalid live carets hide the window without discarding a pinned anchor.
            if (!UsableCaret(x, y)) return false;
            if (!anchored_)
            {
                anchor_ = {x, y}; caretHeight_ = std::clamp(height > 0 ? height : 20, 1, 300000);
                anchored_ = true;
            }
            return true;
        }
        Point Anchor() const { return anchor_; }
        bool IsAbove() const { return above_; }
        Point Resolve(int width, int height, WorkArea work, bool startMenu = false)
        {
            int top = anchor_.y - caretHeight_;
            if (!startMenu)
            {
                if (!above_ && !belowLocked_ && anchor_.y + 5 + height > work.bottom)
                    above_ = top - 5 - height >= work.top || top - work.top > work.bottom - anchor_.y;
                placed_ = true;
            }
            Point result = startMenu ? Point{work.left + 10, work.top + 10} :
                Point{anchor_.x, above_ ? top - 5 - height : anchor_.y + 5};
            result.x = std::clamp(result.x, work.left, std::max(work.left, work.right - width - 2));
            result.y = std::clamp(result.y, work.top, std::max(work.top, work.bottom - height - 2));
            return result;
        }
    };
}
