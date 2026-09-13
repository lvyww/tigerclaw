#pragma once
#include "CoreLaunchContext.h"

namespace TigerClawInput
{
    inline bool IsPasswordEdit(HWND window)
    {
        if (!window) return false;
        wchar_t type[128] = {};
        if (!GetClassNameW(window, type, ARRAYSIZE(type))) return false;
        // Style bits are class-specific; never interpret a browser/custom
        // window's styles as EDIT styles.
        bool edit = !_wcsicmp(type, L"Edit") || !_wcsicmp(type, L"RichEdit") ||
            !_wcsicmp(type, L"RichEdit20W") || !_wcsicmp(type, L"RichEdit20A") ||
            !_wcsicmp(type, L"RICHEDIT50W");
        return edit && (GetWindowLongPtrW(window, GWL_STYLE) & ES_PASSWORD) != 0;
    }

    inline bool IsProtectedEnvironment(bool secureMode)
    {
        return secureMode || !TigerClawStartup::CanLaunchCore();
    }

    inline bool HasPasswordFocus()
    {
        GUITHREADINFO info{};
        info.cbSize = sizeof(info);
        // Check the active input target as well as this TSF thread, so delayed
        // responses cannot use a stale ordinary context after a focus change.
        return IsPasswordEdit(GetFocus()) ||
            (GetGUIThreadInfo(0, &info) && IsPasswordEdit(info.hwndFocus));
    }
}
