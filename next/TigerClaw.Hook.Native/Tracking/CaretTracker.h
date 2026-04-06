#pragma once

#include "..\State\CaretSnapshot.h"

#include <unordered_map>
#include <vector>

struct IUIAutomation;

namespace TigerClawHookNative
{
    class CaretTracker
    {
    public:
        bool TryGetLightweightSnapshot(CaretSnapshot& caret);
        bool TryGetPreciseSnapshot(CaretSnapshot& caret);
        bool TryResolvePublishedSnapshot(const CaretSnapshot& cachedCaret, CaretSnapshot& caret);

    private:
        void UpdateDpiState(HWND foreground);
        bool TryGetAccessibleCaret(HWND foreground, CaretSnapshot& caret);
        bool TryGetFollowCaret(HWND foreground, CaretSnapshot& caret) const;
        bool TryGetGuiThreadCaret(HWND foreground, CaretSnapshot& caret);
        bool TryGetUiaSelectionCaret(HWND foreground, CaretSnapshot& caret);
        bool TryGetUiaCaretRange(HWND foreground, CaretSnapshot& caret);
        bool TryGetUiaFocusedBounds(HWND foreground, CaretSnapshot& caret);
        bool TryGetWindowBottomCenter(HWND foreground, CaretSnapshot& caret) const;
        bool EnsureUia();
        void RememberSnapshot(HWND foreground, const CaretSnapshot& caret);
        void RememberTrustedCaretHeight(HMONITOR monitor, LONG width, LONG height);
        bool TryGetHistoricalCaretHeight(HMONITOR monitor, LONG& height) const;
        LONG GetFallbackCaretHeightPx() const;
        static bool IsInCooldown(std::unordered_map<UINT_PTR, long long>& cooldowns, HWND hwnd, long long now);
        static void MarkCooldown(std::unordered_map<UINT_PTR, long long>& cooldowns, HWND hwnd, long long now, long long elapsedMs);
        static long long GetNowTick();
        double GetPhysicalBottomFallbackBias() const;
        double GetPhysicalVerticalBias(LONG top, LONG bottom) const;

        IUIAutomation* _uia = nullptr;
        std::unordered_map<UINT_PTR, bool> _uiaSelectionBlacklist;
        std::unordered_map<UINT_PTR, long long> _uiaSelectionCooldowns;
        std::unordered_map<UINT_PTR, bool> _uiaCaretRangeBlacklist;
        std::unordered_map<UINT_PTR, long long> _uiaCaretRangeCooldowns;
        std::unordered_map<UINT_PTR, bool> _uiaBoundsBlacklist;
        std::unordered_map<UINT_PTR, long long> _uiaBoundsCooldowns;
        LONG _lastCaretX = 0;
        LONG _lastCaretY = 0;
        LONG _lastWindowX = 0;
        LONG _lastWindowY = 0;
        HWND _lastForeground = nullptr;
        HMONITOR _lastMonitor = nullptr;
        double _screenDpiX = 96.0;
        double _screenDpiY = 96.0;
        double _targetDpi = 96.0;
        std::unordered_map<UINT_PTR, std::vector<LONG>> _trustedCaretHeightsByMonitor;
    };
}
