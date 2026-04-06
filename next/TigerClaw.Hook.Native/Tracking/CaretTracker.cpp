#include "CaretTracker.h"

#include "..\Common\Logger.h"

#include <oleacc.h>
#include <shellscalingapi.h>

#include <algorithm>
#include <cmath>
#include <sstream>

#pragma warning(push)
#pragma warning(disable: 4192)
#import "UIAutomationCore.dll" raw_interfaces_only, raw_native_types, no_namespace, named_guids, auto_search, exclude("IAccessible") rename("TreeScope", "UIATreeScope") rename("PropertyConditionFlags", "UIAPropertyConditionFlags")
#pragma warning(pop)

namespace
{
    constexpr long long UiaSlowThresholdMs = 200;
    constexpr long long UiaCooldownMs = 5000;
    constexpr double BottomFallbackLogicalBiasDip = 40.0;
    constexpr LONG TrustedCaretHeightSampleMin = 5;
    constexpr LONG TrustedCaretHeightSampleMax = 64;
    constexpr LONG TrustedCaretWidthSampleMax = 4;
    constexpr size_t TrustedCaretHeightHistoryLimit = 8;
}

namespace TigerClawHookNative
{
    namespace
    {
        bool IsUsableCaretPosition(const CaretSnapshot& caret)
        {
            return caret.IsValid &&
                   !(caret.X == 0 && caret.Y == 0) &&
                   caret.X > -30000 &&
                   caret.X < 300000 &&
                   caret.Y > -30000 &&
                   caret.Y < 300000;
        }

        bool TryGetGuiThreadInfoForWindow(HWND foreground, GUITHREADINFO& guiThreadInfo)
        {
            guiThreadInfo = {};
            guiThreadInfo.cbSize = sizeof(guiThreadInfo);

            DWORD processId = 0;
            const DWORD threadId = GetWindowThreadProcessId(foreground, &processId);
            return threadId != 0 && GetGUIThreadInfo(threadId, &guiThreadInfo) != FALSE;
        }

        bool IsRectEffectivelyEmpty(const RECT& rect)
        {
            return rect.left == rect.right || rect.top == rect.bottom;
        }

        LONG ClampLongRound(double value)
        {
            return static_cast<LONG>(std::lround(value));
        }

    }

    bool CaretTracker::TryGetLightweightSnapshot(CaretSnapshot& caret)
    {
        const HWND foreground = GetForegroundWindow();
        if (foreground == nullptr)
        {
            caret = {};
            return false;
        }

        UpdateDpiState(foreground);

        if (TryGetAccessibleCaret(foreground, caret))
        {
            Logger::Info(L"caret-src", L"light source=accessible");
            RememberSnapshot(foreground, caret);
            return true;
        }

        if (TryGetGuiThreadCaret(foreground, caret))
        {
            Logger::Info(L"caret-src", L"light source=gui");
            RememberSnapshot(foreground, caret);
            return true;
        }

        caret = {};
        return false;
    }

    bool CaretTracker::TryGetPreciseSnapshot(CaretSnapshot& caret)
    {
        const HWND foreground = GetForegroundWindow();
        if (foreground == nullptr)
        {
            caret = {};
            return false;
        }

        UpdateDpiState(foreground);

        if (TryGetGuiThreadCaret(foreground, caret))
        {
            caret.IsPrecise = true;
            Logger::Info(L"caret-src", L"precise source=gui");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"precise miss=gui");

        if (TryGetUiaSelectionCaret(foreground, caret))
        {
            Logger::Info(L"caret-src", L"precise source=uia-selection");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"precise miss=uia-selection");

        if (TryGetUiaCaretRange(foreground, caret))
        {
            Logger::Info(L"caret-src", L"precise source=uia-caret-range");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"precise miss=uia-caret-range");

        if (TryGetUiaFocusedBounds(foreground, caret))
        {
            Logger::Info(L"caret-src", L"precise source=uia-bounds");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"precise miss=uia-bounds");

        if (TryGetFollowCaret(foreground, caret))
        {
            caret.IsPrecise = true;
            Logger::Info(L"caret-src", L"precise source=follow");
            return true;
        }
        Logger::Info(L"caret-src", L"precise miss=follow");

        if (TryGetWindowBottomCenter(foreground, caret))
        {
            caret.IsPrecise = true;
            Logger::Info(L"caret-src", L"precise source=window-fallback");
            RememberSnapshot(foreground, caret);
            return true;
        }

        Logger::Info(L"caret-src", L"precise source=none");
        caret = {};
        return false;
    }

    bool CaretTracker::TryResolvePublishedSnapshot(const CaretSnapshot& cachedCaret, CaretSnapshot& caret)
    {
        const HWND foreground = GetForegroundWindow();
        if (foreground == nullptr)
        {
            caret = {};
            return false;
        }

        UpdateDpiState(foreground);

        if (IsUsableCaretPosition(cachedCaret))
        {
            caret = cachedCaret;
            Logger::Info(L"caret-src", L"resolved source=cache");
            return true;
        }
        Logger::Info(L"caret-src", L"resolved miss=cache");

        if (TryGetGuiThreadCaret(foreground, caret))
        {
            caret.IsPrecise = true;
            Logger::Info(L"caret-src", L"resolved source=gui");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"resolved miss=gui");

        if (TryGetUiaSelectionCaret(foreground, caret))
        {
            Logger::Info(L"caret-src", L"resolved source=uia-selection");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"resolved miss=uia-selection");

        if (TryGetUiaCaretRange(foreground, caret))
        {
            Logger::Info(L"caret-src", L"resolved source=uia-caret-range");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"resolved miss=uia-caret-range");

        if (TryGetUiaFocusedBounds(foreground, caret))
        {
            Logger::Info(L"caret-src", L"resolved source=uia-bounds");
            RememberSnapshot(foreground, caret);
            return true;
        }
        Logger::Info(L"caret-src", L"resolved miss=uia-bounds");

        if (TryGetWindowBottomCenter(foreground, caret))
        {
            caret.IsPrecise = true;
            Logger::Info(L"caret-src", L"resolved fallback=window-bottom-center after cache/gui/uia-selection/uia-caret-range/uia-bounds miss");
            Logger::Info(L"caret-src", L"resolved source=window-fallback");
            RememberSnapshot(foreground, caret);
            return true;
        }

        Logger::Info(L"caret-src", L"resolved source=none");
        caret = {};
        return false;
    }

    bool CaretTracker::TryGetAccessibleCaret(HWND foreground, CaretSnapshot& caret)
    {
        GUITHREADINFO guiThreadInfo = {};
        if (!TryGetGuiThreadInfoForWindow(foreground, guiThreadInfo))
        {
            caret = {};
            return false;
        }

        const HWND focusedWindow = guiThreadInfo.hwndFocus != nullptr ? guiThreadInfo.hwndFocus : foreground;
        void* rawObject = nullptr;
        GUID accessibleGuid = IID_IAccessible;
        const HRESULT hr = AccessibleObjectFromWindow(
            focusedWindow,
            static_cast<DWORD>(OBJID_CARET),
            accessibleGuid,
            &rawObject);
        if (FAILED(hr) || rawObject == nullptr)
        {
            caret = {};
            return false;
        }

        IAccessible* accessible = static_cast<IAccessible*>(rawObject);

        long left = 0;
        long top = 0;
        long width = 0;
        long height = 0;
        VARIANT child = {};
        child.vt = VT_I4;
        child.lVal = CHILDID_SELF;
        HRESULT locationHr = accessible->accLocation(&left, &top, &width, &height, child);
        accessible->Release();
        if (FAILED(locationHr))
        {
            caret = {};
            return false;
        }

        if (left == 0 && top == 0 && width == 0 && height == 0)
        {
            caret = {};
            return false;
        }

        const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        LONG effectiveHeight = height;
        if (height <= 4)
        {
            if (!TryGetHistoricalCaretHeight(monitor, effectiveHeight))
            {
                effectiveHeight = GetFallbackCaretHeightPx();
            }
        }
        else
        {
            RememberTrustedCaretHeight(monitor, width, height);
        }

        caret.IsValid = true;
        caret.X = left + width;
        caret.Y = top + effectiveHeight;
        caret.Width = std::max<long>(2, width);
        caret.Height = std::max<long>(20, effectiveHeight);
        caret.IsPrecise = false;
        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetUiaSelectionCaret(HWND foreground, CaretSnapshot& caret)
    {
        const long long now = GetNowTick();
        if (_uiaSelectionBlacklist.find(reinterpret_cast<UINT_PTR>(foreground)) != _uiaSelectionBlacklist.end())
        {
            Logger::Info(L"caret-uia", L"selection skipped: blacklist");
            caret = {};
            return false;
        }

        if (IsInCooldown(_uiaSelectionCooldowns, foreground, now))
        {
            Logger::Info(L"caret-uia", L"selection skipped: cooldown");
            caret = {};
            return false;
        }

        if (!EnsureUia())
        {
            Logger::Info(L"caret-uia", L"selection skipped: uia unavailable");
            caret = {};
            return false;
        }

        const long long start = GetNowTick();
        IUIAutomationElement* focusedElement = nullptr;
        HRESULT hr = _uia->GetFocusedElement(&focusedElement);
        if (FAILED(hr) || focusedElement == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: GetFocusedElement hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        IUIAutomationTextPattern* textPattern = nullptr;
        GUID textPatternGuid = __uuidof(IUIAutomationTextPattern);
        hr = focusedElement->GetCurrentPatternAs(UIA_TextPatternId, &textPatternGuid, reinterpret_cast<void**>(&textPattern));
        focusedElement->Release();
        if (FAILED(hr) || textPattern == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: TextPattern hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        IUIAutomationTextRangeArray* selection = nullptr;
        hr = textPattern->GetSelection(&selection);
        textPattern->Release();
        if (FAILED(hr) || selection == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: GetSelection hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        int length = 0;
        hr = selection->get_Length(&length);
        if (FAILED(hr) || length <= 0)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: selection length hr=") + std::to_wstring(hr));
            selection->Release();
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        IUIAutomationTextRange* range = nullptr;
        hr = selection->GetElement(0, &range);
        selection->Release();
        if (FAILED(hr) || range == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: range hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        SAFEARRAY* rectangles = nullptr;
        hr = range->GetBoundingRectangles(&rectangles);
        range->Release();
        if (FAILED(hr) || rectangles == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: bounding rect hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        LONG lowerBound = 0;
        LONG upperBound = -1;
        if (SafeArrayGetLBound(rectangles, 1, &lowerBound) != S_OK ||
            SafeArrayGetUBound(rectangles, 1, &upperBound) != S_OK)
        {
            Logger::Info(L"caret-uia", L"selection failed: array bounds");
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        const LONG lengthDoubles = upperBound - lowerBound + 1;
        if (lengthDoubles < 4)
        {
            Logger::Info(L"caret-uia", std::wstring(L"selection failed: rectangles too short length=") + std::to_wstring(lengthDoubles));
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        double* data = nullptr;
        if (SafeArrayAccessData(rectangles, reinterpret_cast<void**>(&data)) != S_OK || data == nullptr)
        {
            Logger::Info(L"caret-uia", L"selection failed: access array data");
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        const double left = data[0];
        const double top = data[1];
        const double width = data[2];
        const double height = data[3];
        SafeArrayUnaccessData(rectangles);
        SafeArrayDestroy(rectangles);

        if (width <= 0.1 || height <= 0.1)
        {
            Logger::Info(L"caret-uia", L"selection failed: empty rect");
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, GetNowTick() - start);
            _uiaSelectionBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        caret.IsValid = true;
        caret.X = static_cast<LONG>(std::lround(left + width));
        caret.Y = static_cast<LONG>(std::lround(top + height));
        caret.Width = std::max<LONG>(2, static_cast<LONG>(std::lround(width)));
        caret.Height = std::max<LONG>(20, static_cast<LONG>(std::lround(height)));
        caret.IsPrecise = true;

        const long long elapsed = GetNowTick() - start;
        Logger::Info(L"caret-uia", std::wstring(L"selection elapsed=") + std::to_wstring(elapsed));
        if (elapsed > UiaSlowThresholdMs)
        {
            MarkCooldown(_uiaSelectionCooldowns, foreground, now, elapsed);
        }

        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetUiaFocusedBounds(HWND foreground, CaretSnapshot& caret)
    {
        const long long now = GetNowTick();
        if (_uiaBoundsBlacklist.find(reinterpret_cast<UINT_PTR>(foreground)) != _uiaBoundsBlacklist.end())
        {
            Logger::Info(L"caret-uia", L"bounds skipped: blacklist");
            caret = {};
            return false;
        }

        if (IsInCooldown(_uiaBoundsCooldowns, foreground, now))
        {
            Logger::Info(L"caret-uia", L"bounds skipped: cooldown");
            caret = {};
            return false;
        }

        if (!EnsureUia())
        {
            Logger::Info(L"caret-uia", L"bounds skipped: uia unavailable");
            caret = {};
            return false;
        }

        const long long start = GetNowTick();
        IUIAutomationElement* focusedElement = nullptr;
        HRESULT hr = _uia->GetFocusedElement(&focusedElement);
        if (FAILED(hr) || focusedElement == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"bounds failed: GetFocusedElement hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaBoundsCooldowns, foreground, now, GetNowTick() - start);
            _uiaBoundsBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        RECT rect = {};
        hr = focusedElement->get_CurrentBoundingRectangle(&rect);
        focusedElement->Release();
        if (FAILED(hr) || IsRectEffectivelyEmpty(rect))
        {
            Logger::Info(L"caret-uia", std::wstring(L"bounds failed: CurrentBoundingRectangle hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaBoundsCooldowns, foreground, now, GetNowTick() - start);
            _uiaBoundsBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        const LONG width = rect.right - rect.left;
        caret.IsValid = true;
        caret.X = (rect.left + rect.right) / 2;
        if (width > 20)
        {
            caret.X -= 5;
        }

        caret.Y = rect.bottom - static_cast<LONG>(std::lround(GetPhysicalVerticalBias(rect.top, rect.bottom)));
        caret.Width = 2;
        caret.Height = 20;
        caret.IsPrecise = true;

        std::wstringstream boundsStream;
        boundsStream << L"bounds rect=("
                     << rect.left << L"," << rect.top << L"," << rect.right << L"," << rect.bottom
                     << L") x=" << caret.X
                     << L" y=" << caret.Y;
        Logger::Info(L"caret-uia", boundsStream.str());

        const long long elapsed = GetNowTick() - start;
        Logger::Info(L"caret-uia", std::wstring(L"bounds elapsed=") + std::to_wstring(elapsed));
        if (elapsed > UiaSlowThresholdMs)
        {
            MarkCooldown(_uiaBoundsCooldowns, foreground, now, elapsed);
        }

        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetUiaCaretRange(HWND foreground, CaretSnapshot& caret)
    {
        const long long now = GetNowTick();
        if (_uiaCaretRangeBlacklist.find(reinterpret_cast<UINT_PTR>(foreground)) != _uiaCaretRangeBlacklist.end())
        {
            Logger::Info(L"caret-uia", L"caret-range skipped: blacklist");
            caret = {};
            return false;
        }

        if (IsInCooldown(_uiaCaretRangeCooldowns, foreground, now))
        {
            Logger::Info(L"caret-uia", L"caret-range skipped: cooldown");
            caret = {};
            return false;
        }

        if (!EnsureUia())
        {
            Logger::Info(L"caret-uia", L"caret-range skipped: uia unavailable");
            caret = {};
            return false;
        }

        const long long start = GetNowTick();
        IUIAutomationElement* focusedElement = nullptr;
        HRESULT hr = _uia->GetFocusedElement(&focusedElement);
        if (FAILED(hr) || focusedElement == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"caret-range failed: GetFocusedElement hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        IUIAutomationTextPattern2* textPattern2 = nullptr;
        GUID textPattern2Guid = __uuidof(IUIAutomationTextPattern2);
        hr = focusedElement->GetCurrentPatternAs(UIA_TextPattern2Id, &textPattern2Guid, reinterpret_cast<void**>(&textPattern2));
        focusedElement->Release();
        if (FAILED(hr) || textPattern2 == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"caret-range failed: TextPattern2 hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        long isActive = 0;
        IUIAutomationTextRange* range = nullptr;
        hr = textPattern2->GetCaretRange(&isActive, &range);
        textPattern2->Release();
        if (FAILED(hr) || range == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"caret-range failed: GetCaretRange hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        SAFEARRAY* rectangles = nullptr;
        hr = range->GetBoundingRectangles(&rectangles);
        range->Release();
        if (FAILED(hr) || rectangles == nullptr)
        {
            Logger::Info(L"caret-uia", std::wstring(L"caret-range failed: bounding rect hr=") + std::to_wstring(hr));
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        LONG lowerBound = 0;
        LONG upperBound = -1;
        if (SafeArrayGetLBound(rectangles, 1, &lowerBound) != S_OK ||
            SafeArrayGetUBound(rectangles, 1, &upperBound) != S_OK)
        {
            Logger::Info(L"caret-uia", L"caret-range failed: array bounds");
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        const LONG lengthDoubles = upperBound - lowerBound + 1;
        if (lengthDoubles < 4)
        {
            Logger::Info(L"caret-uia", std::wstring(L"caret-range failed: rectangles too short length=") + std::to_wstring(lengthDoubles));
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        double* data = nullptr;
        if (SafeArrayAccessData(rectangles, reinterpret_cast<void**>(&data)) != S_OK || data == nullptr)
        {
            Logger::Info(L"caret-uia", L"caret-range failed: access array data");
            SafeArrayDestroy(rectangles);
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        const double left = data[0];
        const double top = data[1];
        const double width = data[2];
        const double height = data[3];
        SafeArrayUnaccessData(rectangles);
        SafeArrayDestroy(rectangles);

        if (width <= 0.1 || height <= 0.1)
        {
            Logger::Info(L"caret-uia", L"caret-range failed: empty rect");
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, GetNowTick() - start);
            _uiaCaretRangeBlacklist[reinterpret_cast<UINT_PTR>(foreground)] = true;
            caret = {};
            return false;
        }

        caret.IsValid = true;
        caret.X = static_cast<LONG>(std::lround(left + width));
        caret.Y = static_cast<LONG>(std::lround(top + height));
        caret.Width = std::max<LONG>(2, static_cast<LONG>(std::lround(width)));
        caret.Height = std::max<LONG>(20, static_cast<LONG>(std::lround(height)));
        caret.IsPrecise = true;

        const long long elapsed = GetNowTick() - start;
        Logger::Info(L"caret-uia", std::wstring(L"caret-range elapsed=") + std::to_wstring(elapsed));
        if (elapsed > UiaSlowThresholdMs)
        {
            MarkCooldown(_uiaCaretRangeCooldowns, foreground, now, elapsed);
        }

        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetFollowCaret(HWND foreground, CaretSnapshot& caret) const
    {
        if (_lastCaretX == 0 || _lastCaretY == 0)
        {
            caret = {};
            return false;
        }

        RECT rect = {};
        if (!GetWindowRect(foreground, &rect))
        {
            caret = {};
            return false;
        }

        if (foreground != _lastForeground)
        {
            caret = {};
            return false;
        }

        if (rect.left == _lastWindowX && rect.top == _lastWindowY)
        {
            caret = {};
            return false;
        }

        const LONG offsetX = _lastCaretX - _lastWindowX;
        const LONG offsetY = _lastCaretY - _lastWindowY;
        caret.IsValid = true;
        caret.X = rect.left + offsetX;
        caret.Y = rect.top + offsetY;
        caret.Width = 2;
        caret.Height = 20;
        caret.IsPrecise = false;
        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetGuiThreadCaret(HWND foreground, CaretSnapshot& caret)
    {
        GUITHREADINFO guiThreadInfo = {};
        if (!TryGetGuiThreadInfoForWindow(foreground, guiThreadInfo))
        {
            caret = {};
            return false;
        }

        if (IsRectEffectivelyEmpty(guiThreadInfo.rcCaret))
        {
            caret = {};
            return false;
        }

        caret.IsValid = true;
        RECT windowRect = {};
        const HWND caretWindow = guiThreadInfo.hwndCaret != nullptr
            ? guiThreadInfo.hwndCaret
            : foreground;
        if (!GetWindowRect(caretWindow, &windowRect))
        {
            caret = {};
            return false;
        }

        const LONG rawWidth = guiThreadInfo.rcCaret.right - guiThreadInfo.rcCaret.left;
        const LONG rawHeight = guiThreadInfo.rcCaret.bottom - guiThreadInfo.rcCaret.top;
        const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        LONG effectiveHeight = rawHeight;
        if (rawHeight <= 4)
        {
            if (!TryGetHistoricalCaretHeight(monitor, effectiveHeight))
            {
                effectiveHeight = GetFallbackCaretHeightPx();
            }
        }
        else
        {
            RememberTrustedCaretHeight(monitor, rawWidth, rawHeight);
        }

        caret.X = windowRect.left + static_cast<LONG>(std::lround(guiThreadInfo.rcCaret.right * _screenDpiX / _targetDpi));
        caret.Y = windowRect.top + ClampLongRound((guiThreadInfo.rcCaret.top + effectiveHeight) * _screenDpiY / _targetDpi);
        caret.Width = std::max<LONG>(2, rawWidth);
        caret.Height = std::max<LONG>(20, effectiveHeight);
        caret.IsPrecise = false;
        return IsUsableCaretPosition(caret);
    }

    bool CaretTracker::TryGetWindowBottomCenter(HWND foreground, CaretSnapshot& caret) const
    {
        RECT rect = {};
        if (!GetWindowRect(foreground, &rect))
        {
            caret = {};
            return false;
        }

        const LONG width = rect.right - rect.left;
        caret.IsValid = true;
        caret.X = (rect.left + rect.right) / 2;
        if (width > 20)
        {
            caret.X -= 5;
        }

        caret.Y = rect.bottom - static_cast<LONG>(std::lround(GetPhysicalBottomFallbackBias()));
        caret.Width = 2;
        caret.Height = 20;
        caret.IsPrecise = false;
        return IsUsableCaretPosition(caret);
    }

    void CaretTracker::RememberSnapshot(HWND foreground, const CaretSnapshot& caret)
    {
        _lastCaretX = caret.X;
        _lastCaretY = caret.Y;

        RECT rect = {};
        if (GetWindowRect(foreground, &rect))
        {
            _lastWindowX = rect.left;
            _lastWindowY = rect.top;
        }
    }

    void CaretTracker::RememberTrustedCaretHeight(HMONITOR monitor, LONG width, LONG height)
    {
        if (monitor == nullptr ||
            height < TrustedCaretHeightSampleMin ||
            height > TrustedCaretHeightSampleMax ||
            width > TrustedCaretWidthSampleMax)
        {
            return;
        }

        auto& samples = _trustedCaretHeightsByMonitor[reinterpret_cast<UINT_PTR>(monitor)];
        if (!samples.empty())
        {
            std::vector<LONG> sorted = samples;
            std::sort(sorted.begin(), sorted.end());
            const LONG historical = sorted[sorted.size() / 2];
            const double minAllowed = std::max<double>(6.0, historical * 0.5);
            const double maxAllowed = historical * 1.8;
            if (height < minAllowed || height > maxAllowed)
            {
                return;
            }
        }

        samples.push_back(height);
        if (samples.size() > TrustedCaretHeightHistoryLimit)
        {
            samples.erase(samples.begin());
        }
    }

    bool CaretTracker::TryGetHistoricalCaretHeight(HMONITOR monitor, LONG& height) const
    {
        height = 0;
        if (monitor == nullptr)
        {
            return false;
        }

        auto it = _trustedCaretHeightsByMonitor.find(reinterpret_cast<UINT_PTR>(monitor));
        if (it == _trustedCaretHeightsByMonitor.end() || it->second.empty())
        {
            return false;
        }

        std::vector<LONG> sorted = it->second;
        std::sort(sorted.begin(), sorted.end());
        height = sorted[sorted.size() / 2];
        return height > 0;
    }

    LONG CaretTracker::GetFallbackCaretHeightPx() const
    {
        return std::max<LONG>(20, ClampLongRound(20.0 * _screenDpiY / 96.0));
    }

    bool CaretTracker::EnsureUia()
    {
        if (_uia != nullptr)
        {
            return true;
        }

        return SUCCEEDED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, __uuidof(IUIAutomation), reinterpret_cast<void**>(&_uia)));
    }

    void CaretTracker::UpdateDpiState(HWND foreground)
    {
        if (foreground == nullptr)
        {
            return;
        }

        const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (monitor == _lastMonitor && foreground == _lastForeground)
        {
            return;
        }

        _screenDpiX = 96.0;
        _screenDpiY = 96.0;
        _targetDpi = 96.0;

        UINT dpiX = 96;
        UINT dpiY = 96;
        if (monitor != nullptr && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY) == S_OK)
        {
            _screenDpiX = static_cast<double>(dpiX);
            _screenDpiY = static_cast<double>(dpiY);
        }

        const UINT windowDpi = GetDpiForWindow(foreground);
        if (windowDpi > 0)
        {
            _targetDpi = static_cast<double>(windowDpi);
        }

        _lastMonitor = monitor;
        _lastForeground = foreground;
    }

    long long CaretTracker::GetNowTick()
    {
        return static_cast<unsigned long>(GetTickCount());
    }

    bool CaretTracker::IsInCooldown(std::unordered_map<UINT_PTR, long long>& cooldowns, HWND hwnd, long long now)
    {
        const UINT_PTR key = reinterpret_cast<UINT_PTR>(hwnd);
        const auto it = cooldowns.find(key);
        if (it == cooldowns.end())
        {
            return false;
        }

        if (it->second <= now)
        {
            cooldowns.erase(it);
            return false;
        }

        return true;
    }

    void CaretTracker::MarkCooldown(std::unordered_map<UINT_PTR, long long>& cooldowns, HWND hwnd, long long now, long long elapsedMs)
    {
        if (hwnd == nullptr)
        {
            return;
        }

        const long long duration = elapsedMs > UiaSlowThresholdMs ? UiaCooldownMs : (UiaCooldownMs / 2);
        cooldowns[reinterpret_cast<UINT_PTR>(hwnd)] = now + duration;
    }

    double CaretTracker::GetPhysicalBottomFallbackBias() const
    {
        return BottomFallbackLogicalBiasDip * _screenDpiY / 96.0;
    }

    double CaretTracker::GetPhysicalVerticalBias(LONG top, LONG bottom) const
    {
        const double height = static_cast<double>(bottom - top);
        const double half = height / 2.0;
        const double fallbackCaretHeight = static_cast<double>(GetFallbackCaretHeightPx());
        if (half < 1.25 * fallbackCaretHeight)
        {
            return 0.0;
        }

        return std::min(std::min(GetPhysicalBottomFallbackBias(), 2.0 * fallbackCaretHeight), half);
    }
}
