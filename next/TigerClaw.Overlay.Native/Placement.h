#pragma once
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>

namespace tiger::overlay
{
    struct Point { int x = 0, y = 0; };
    struct WorkArea
    {
        int left, top, right, bottom;
        bool operator==(const WorkArea& b) const
        { return left == b.left && top == b.top && right == b.right && bottom == b.bottom; }
    };
    inline Point StatusPosition(Point current, int width, int height, WorkArea work, bool initial)
    {
        const int right = std::max(work.left, work.right - width - 2);
        const int bottom = std::max(work.top, work.bottom - height - 2);
        return initial ? Point{right, bottom} : Point{
            std::clamp(current.x, work.left, right), std::clamp(current.y, work.top, bottom)};
    }
    inline bool UsableCaret(int x, int y)
    {
        return (x || y) && x > -30000 && x < 300000 && y > -30000 && y < 300000;
    }
    // Only display geometry partitions this memory, never HWND or TSF context.
    struct PlacementEnvironment
    {
        std::uintptr_t monitor = 0;
        unsigned dpi = 96;
        WorkArea work{};
        bool operator==(const PlacementEnvironment& b) const
        { return monitor == b.monitor && dpi == b.dpi && work == b.work; }
    };
    enum class PlacementReason { Below, OverflowAbove, InheritedAbove, Clamped };
    struct PlacementRecord
    {
        int y = 0, caretHeight = 0;
        PlacementReason reason = PlacementReason::Below;
        bool above = false;
    };
    class Placement
    {
        using Wide = std::int64_t;
        bool anchored_ = false, above_ = false, referenceValid_ = false, decisionValid_ = false;
        Point anchor_{}, live_{};
        int caretHeight_ = 20, referenceY_ = 0, referenceHeight_ = 20;
        PlacementEnvironment environment_{};
        std::array<PlacementRecord, 100> records_{};
        std::size_t next_ = 0, count_ = 0, evidence_ = 0;
        struct DecisionKey
        {
            Point live{}, anchor{};
            int caretHeight = 0, width = 0, height = 0;
            std::uint64_t content = 0;
            bool operator==(const DecisionKey& b) const
            {
                return live.x == b.live.x && live.y == b.live.y &&
                    anchor.x == b.anchor.x && anchor.y == b.anchor.y &&
                    caretHeight == b.caretHeight && width == b.width && height == b.height && content == b.content;
            }
        };
        DecisionKey last_{};
        void ClearHistory()
        { next_ = count_ = evidence_ = 0; above_ = false; referenceValid_ = false; decisionValid_ = false; }
        static Point Clamp(Wide x, Wide y, int width, int height, WorkArea work)
        {
            return {static_cast<int>(std::clamp(x, Wide(work.left), std::max(Wide(work.left), Wide(work.right) - width - 2))),
                static_cast<int>(std::clamp(y, Wide(work.top), std::max(Wide(work.top), Wide(work.bottom) - height - 2)))};
        }
    public:
        static constexpr std::size_t Capacity = 100;
        void Reset() { *this = {}; }
        // Normal hiding ends only the short-lived anchor and deduplication key.
        // It must neither erase history nor append a synthetic hidden record.
        void EndInput() { anchored_ = false; decisionValid_ = false; }
        void RefreshAnchor() { EndInput(); }
        bool Acquire(int x, int y, int height)
        {
            if (!UsableCaret(x, y)) return false;
            live_ = {x, y};
            if (!anchored_) anchor_.x = x;
            // Keep the existing horizontal composition anchor. Live Y/height
            // are needed to detect real upward motion, not an old pinned Y.
            anchor_.y = y;
            caretHeight_ = std::clamp(height > 0 ? height : 20, 1, 300000);
            anchored_ = true;
            return true;
        }
        Point Anchor() const { return anchor_; }
        bool IsAbove() const { return above_; }
        std::size_t RecordCount() const { return count_; }
        std::size_t EvidenceCount() const { return evidence_; }
        int ReferenceY() const { return referenceY_; }
        bool SameEnvironment(const Placement& b) const
        { return referenceValid_ && b.referenceValid_ && environment_ == b.environment_; }
        bool SameDecision(const Placement& b) const
        { return decisionValid_ && b.decisionValid_ && environment_ == b.environment_ && last_ == b.last_; }
        static int Tolerance(int currentHeight, int referenceHeight, unsigned dpi)
        {
            const Wide pixels = (Wide(dpi ? dpi : 96) * 3 + 48) / 96;
            return static_cast<int>(std::max(Wide(0), std::min(pixels, Wide(std::min(currentHeight, referenceHeight)) / 4)));
        }
        // Calculate on a copy, then accept the copy only after the full target
        // frame is successfully displayed. Repeated keys do not age the ring.
        Point Resolve(int width, int height, WorkArea work, bool startMenu = false,
            std::uintptr_t monitor = 0, unsigned dpi = 96, std::uint64_t content = 0)
        {
            if (!anchored_ || width <= 0 || height <= 0 || work.right <= work.left || work.bottom <= work.top) return {};
            if (startMenu)
            {
                decisionValid_ = false;
                return Clamp(Wide(work.left) + 10, Wide(work.top) + 10, width, height, work);
            }
            const PlacementEnvironment environment{monitor, dpi ? dpi : 96, work};
            if (!referenceValid_ || !(environment == environment_))
            {
                ClearHistory();
                anchor_.x = live_.x; // A new display must not reuse the old display's X.
            }
            const DecisionKey key{live_, anchor_, caretHeight_, width, height, content};
            const Wide top = Wide(anchor_.y) - caretHeight_;
            const Wide below = Wide(anchor_.y) + 5, upper = top - 5 - height;
            if (decisionValid_ && last_ == key)
                return Clamp(anchor_.x, above_ ? upper : below, width, height, work);

            const int epsilon = Tolerance(caretHeight_, referenceHeight_, environment.dpi);
            const Wide delta = Wide(anchor_.y) - referenceY_;
            if (referenceValid_ && delta < -Wide(epsilon)) ClearHistory();
            if (!referenceValid_ || delta > epsilon)
            { referenceY_ = anchor_.y; referenceHeight_ = caretHeight_; }
            referenceValid_ = true;
            environment_ = environment;

            // Evict BEFORE selecting the next side: record 1 supports 2..100,
            // but is no longer evidence for record 101. Inheritance cannot renew it.
            if (count_ == Capacity)
            {
                if (records_[next_].reason == PlacementReason::OverflowAbove) --evidence_;
                --count_;
            }
            const Wide bottom = Wide(work.bottom) - 2;
            const bool fitsBelow = below >= work.top && below + height <= bottom;
            const bool fitsAbove = upper >= work.top && upper + height <= bottom;
            const bool inherit = above_ && evidence_ != 0 && fitsAbove;
            PlacementReason reason;
            if (fitsAbove && !fitsBelow)
            { above_ = true; reason = PlacementReason::OverflowAbove; }
            else if (inherit)
            { above_ = true; reason = PlacementReason::InheritedAbove; }
            else if (fitsBelow)
            { above_ = false; reason = PlacementReason::Below; }
            else
            {
                const Wide roomAbove = std::max(Wide(0), std::min(top - 5, bottom) - work.top);
                const Wide roomBelow = std::max(Wide(0), bottom - std::max(below, Wide(work.top)));
                above_ = roomAbove > roomBelow;
                reason = PlacementReason::Clamped; // Neither side fits: not reusable evidence.
            }
            records_[next_] = {anchor_.y, caretHeight_, reason, above_};
            next_ = (next_ + 1) % Capacity;
            ++count_;
            if (reason == PlacementReason::OverflowAbove) ++evidence_;
            last_ = key; decisionValid_ = true;
            return Clamp(anchor_.x, above_ ? upper : below, width, height, work);
        }
    };
}
