#pragma once

#include "CaretSnapshot.h"
#include "CoreResponse.h"
#include "FocusSnapshot.h"

#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

namespace TigerClawHookNative
{
    class HookState
    {
    public:
        void UpdateModifierState(UINT virtualKey, bool isKeyDown, bool isKeyUp);
        void SetFocus(const FocusSnapshot& focus);
        void SetCaret(const CaretSnapshot& caret);
        void UpdateCoreResponse(const CoreResponse& response);

        const FocusSnapshot& GetFocus() const;
        const CaretSnapshot& GetCaret() const;
        bool WantsCaretTracking() const;
        bool WantsPreciseCaret() const;
        bool ShiftDown() const;
        bool CtrlDown() const;
        bool AltDown() const;
        bool WinDown() const;
        bool CapsLockOn() const;
        bool NumLockOn() const;
        const std::wstring& CurrentInputBuffer() const;
        bool NativeHookAltBackslashToggleEnabled() const;
        void SetNativeHookAltBackslashToggleEnabled(bool enabled);
        bool AutoSwitchSystemLayoutEnabled() const;
        void SetAutoSwitchSystemLayoutEnabled(bool enabled);
        bool UseClipboardCommit() const;
        void SetUseClipboardCommit(bool enabled);
        void SetClipboardCommitWhitelist(const std::wstring& value);
        bool ShouldUseClipboardCommitForFocus(const FocusSnapshot& focus) const;
        bool DisabledByHotkey() const;
        void SetDisabledByHotkey(bool disabled);
        UINT ActiveAltVirtualKey() const;
        void ResetCompositionState();
        bool UpdateFocus(const FocusSnapshot& focus);
        bool UpdateCaret(const CaretSnapshot& caret);

    private:
        static long long GetNowTick();

        FocusSnapshot _focus;
        CaretSnapshot _caret;
        std::wstring _lastInputBuffer;
        bool _wantsCaretTracking = false;
        bool _wantsPreciseCaret = false;
        bool _shiftDown = false;
        bool _ctrlDown = false;
        bool _altDown = false;
        bool _winDown = false;
        bool _modifierKeys[256] = {};
        bool _nativeHookAltBackslashToggleEnabled = true;
        bool _autoSwitchSystemLayoutEnabled = true;
        bool _useClipboardCommit = false;
        bool _disabledByHotkey = false;
        UINT _activeAltVirtualKey = VK_MENU;
        std::vector<std::wstring> _clipboardCommitWhitelist;
        long long _trackCaretUntilTick = 0;
        long long _preferPreciseCaretUntilTick = 0;
    };

    inline void HookState::UpdateModifierState(UINT virtualKey, bool isKeyDown, bool isKeyUp)
    {
        if (virtualKey >= 256 || (!isKeyDown && !isKeyUp)) return;
        switch (virtualKey)
        {
        case VK_SHIFT:
        case VK_LSHIFT:
        case VK_RSHIFT:
        case VK_CONTROL:
        case VK_LCONTROL:
        case VK_RCONTROL:
        case VK_MENU:
        case VK_LMENU:
        case VK_RMENU:
        case VK_LWIN:
        case VK_RWIN:
            _modifierKeys[virtualKey] = isKeyDown && !isKeyUp;
            break;
        default:
            return;
        }
        _shiftDown = _modifierKeys[VK_SHIFT] || _modifierKeys[VK_LSHIFT] || _modifierKeys[VK_RSHIFT];
        _ctrlDown = _modifierKeys[VK_CONTROL] || _modifierKeys[VK_LCONTROL] || _modifierKeys[VK_RCONTROL];
        _altDown = _modifierKeys[VK_MENU] || _modifierKeys[VK_LMENU] || _modifierKeys[VK_RMENU];
        _winDown = _modifierKeys[VK_LWIN] || _modifierKeys[VK_RWIN];
        if (_modifierKeys[VK_LMENU]) _activeAltVirtualKey = VK_LMENU;
        else if (_modifierKeys[VK_RMENU]) _activeAltVirtualKey = VK_RMENU;
        else _activeAltVirtualKey = VK_MENU;
    }

    inline void HookState::SetFocus(const FocusSnapshot& focus)
    {
        _focus = focus;
    }

    inline void HookState::SetCaret(const CaretSnapshot& caret)
    {
        _caret = caret;
    }

    inline void HookState::UpdateCoreResponse(const CoreResponse& response)
    {
        _lastInputBuffer = response.InputBuffer;
        _nativeHookAltBackslashToggleEnabled = response.NativeHookAltBackslashToggleEnabled;
        _autoSwitchSystemLayoutEnabled = response.AutoSwitchSystemLayoutEnabled;
        _useClipboardCommit = response.UseClipboardCommit;
        SetClipboardCommitWhitelist(response.ClipboardCommitWhitelist);

        const long long now = GetNowTick();
        if (!response.KeyboardOpen || response.CancelComposition)
        {
            _trackCaretUntilTick = 0;
            _preferPreciseCaretUntilTick = 0;
            _wantsCaretTracking = false;
            _wantsPreciseCaret = false;
            return;
        }

        if (!response.InputBuffer.empty())
        {
            _trackCaretUntilTick = now + 3000;
            _preferPreciseCaretUntilTick = now + 1200;
        }
        else if (response.Handled)
        {
            if (_trackCaretUntilTick < now + 600)
            {
                _trackCaretUntilTick = now + 600;
            }

            if (_preferPreciseCaretUntilTick < now + 300)
            {
                _preferPreciseCaretUntilTick = now + 300;
            }
        }

        _wantsCaretTracking = now <= _trackCaretUntilTick;
        _wantsPreciseCaret = now <= _preferPreciseCaretUntilTick;
    }

    inline const FocusSnapshot& HookState::GetFocus() const
    {
        return _focus;
    }

    inline const CaretSnapshot& HookState::GetCaret() const
    {
        return _caret;
    }

    inline bool HookState::WantsCaretTracking() const
    {
        return GetNowTick() <= _trackCaretUntilTick;
    }

    inline bool HookState::WantsPreciseCaret() const
    {
        return GetNowTick() <= _preferPreciseCaretUntilTick;
    }

    inline bool HookState::ShiftDown() const
    {
        return _shiftDown;
    }

    inline bool HookState::CtrlDown() const
    {
        return _ctrlDown;
    }

    inline bool HookState::AltDown() const
    {
        return _altDown;
    }

    inline bool HookState::WinDown() const
    {
        return _winDown;
    }

    inline bool HookState::CapsLockOn() const
    {
        return (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
    }

    inline bool HookState::NumLockOn() const
    {
        return (GetKeyState(VK_NUMLOCK) & 0x0001) != 0;
    }

    inline const std::wstring& HookState::CurrentInputBuffer() const
    {
        return _lastInputBuffer;
    }

    inline bool HookState::NativeHookAltBackslashToggleEnabled() const
    {
        return _nativeHookAltBackslashToggleEnabled;
    }

    inline void HookState::SetNativeHookAltBackslashToggleEnabled(bool enabled)
    {
        _nativeHookAltBackslashToggleEnabled = enabled;
    }

    inline bool HookState::DisabledByHotkey() const
    {
        return _disabledByHotkey;
    }

    inline bool HookState::AutoSwitchSystemLayoutEnabled() const
    {
        return _autoSwitchSystemLayoutEnabled;
    }

    inline void HookState::SetAutoSwitchSystemLayoutEnabled(bool enabled)
    {
        _autoSwitchSystemLayoutEnabled = enabled;
    }

    inline bool HookState::UseClipboardCommit() const
    {
        return _useClipboardCommit;
    }

    inline void HookState::SetUseClipboardCommit(bool enabled)
    {
        _useClipboardCommit = enabled;
    }

    inline void HookState::SetClipboardCommitWhitelist(const std::wstring& value)
    {
        _clipboardCommitWhitelist.clear();

        std::wstring token;
        for (wchar_t ch : value)
        {
            if (ch == L',' || ch == 0xFF0C)
            {
                if (!token.empty())
                {
                    std::transform(token.begin(), token.end(), token.begin(), towlower);
                    _clipboardCommitWhitelist.push_back(token);
                    token.clear();
                }

                continue;
            }

            if (ch != L'\r' && ch != L'\n')
            {
                token.push_back(ch);
            }
        }

        if (!token.empty())
        {
            std::transform(token.begin(), token.end(), token.begin(), towlower);
            _clipboardCommitWhitelist.push_back(token);
        }

        for (std::wstring& item : _clipboardCommitWhitelist)
        {
            const size_t first = item.find_first_not_of(L" \t");
            const size_t last = item.find_last_not_of(L" \t");
            item = (first == std::wstring::npos) ? std::wstring() : item.substr(first, last - first + 1);
        }

        _clipboardCommitWhitelist.erase(
            std::remove_if(
                _clipboardCommitWhitelist.begin(),
                _clipboardCommitWhitelist.end(),
                [](const std::wstring& item) { return item.empty(); }),
            _clipboardCommitWhitelist.end());
    }

    inline bool HookState::ShouldUseClipboardCommitForFocus(const FocusSnapshot& focus) const
    {
        if (_useClipboardCommit)
        {
            return true;
        }

        if (focus.ProcessName.empty())
        {
            return false;
        }

        std::wstring processName = focus.ProcessName;
        std::transform(processName.begin(), processName.end(), processName.begin(), towlower);
        return std::find(_clipboardCommitWhitelist.begin(), _clipboardCommitWhitelist.end(), processName) != _clipboardCommitWhitelist.end();
    }

    inline void HookState::SetDisabledByHotkey(bool disabled)
    {
        _disabledByHotkey = disabled;
    }

    inline UINT HookState::ActiveAltVirtualKey() const
    {
        return _activeAltVirtualKey;
    }

    inline void HookState::ResetCompositionState()
    {
        _lastInputBuffer.clear();
        _trackCaretUntilTick = 0;
        _preferPreciseCaretUntilTick = 0;
        _wantsCaretTracking = false;
        _wantsPreciseCaret = false;
        _caret = {};
    }

    inline bool HookState::UpdateFocus(const FocusSnapshot& focus)
    {
        if (_focus.Equals(focus))
        {
            return false;
        }

        _focus = focus;
        _caret = {};
        _preferPreciseCaretUntilTick = 0;
        return true;
    }

    inline bool HookState::UpdateCaret(const CaretSnapshot& caret)
    {
        if (_caret.Equals(caret))
        {
            return false;
        }

        _caret = caret;
        return true;
    }

    inline long long HookState::GetNowTick()
    {
        return static_cast<unsigned long>(GetTickCount());
    }
}
