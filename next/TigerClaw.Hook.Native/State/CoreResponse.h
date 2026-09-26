#pragma once

#include <string>

namespace TigerClawHookNative
{
    struct CoreResponse
    {
        bool Success = false;
        bool Handled = false;
        bool KeyboardOpen = false;
        bool CancelComposition = false;
        bool WantsCaretTracking = false;
        bool WantsPreciseCaret = false;
        bool EnsureSystemLayoutEn = false;
        bool NativeHookAltBackslashToggleEnabled = true;
        bool AutoSwitchSystemLayoutEnabled = true;
        bool UseClipboardCommit = false;
        std::wstring LearningReceipt;
        std::wstring CommitText;
        std::wstring InputBuffer;
        std::wstring ClipboardCommitWhitelist;
    };
}
