#include "KeyboardHook.h"

namespace TigerClawHookNative
{
    KeyboardHook* KeyboardHook::CurrentInstance = nullptr;

    KeyboardHook::KeyboardHook() = default;

    KeyboardHook::~KeyboardHook()
    {
        Uninstall();
    }

    bool KeyboardHook::Install(const Callback& callback)
    {
        _callback = callback;
        CurrentInstance = this;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, HookProc, nullptr, 0);
        if (_hook == nullptr)
        {
            CurrentInstance = nullptr;
            _callback = nullptr;
            return false;
        }

        return true;
    }

    void KeyboardHook::Uninstall()
    {
        if (_hook != nullptr)
        {
            UnhookWindowsHookEx(_hook);
            _hook = nullptr;
        }

        CurrentInstance = nullptr;
        _callback = nullptr;
    }

    LRESULT CALLBACK KeyboardHook::HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (CurrentInstance == nullptr)
        {
            return CallNextHookEx(nullptr, code, wParam, lParam);
        }

        return CurrentInstance->HandleHook(code, wParam, lParam);
    }

    LRESULT KeyboardHook::HandleHook(int code, WPARAM wParam, LPARAM lParam) const
    {
        if (code < 0 || lParam == 0 || !_callback)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        const auto* keyboardInfo = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);
        KeyboardHookEvent keyEvent = {};
        keyEvent.VirtualKey = keyboardInfo->vkCode;
        keyEvent.ScanCode = keyboardInfo->scanCode;
        keyEvent.IsInjected = (keyboardInfo->flags & LLKHF_INJECTED) != 0;
        keyEvent.IsKeyDown = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
        keyEvent.IsKeyUp = (wParam == WM_KEYUP || wParam == WM_SYSKEYUP);
        keyEvent.Flags = keyboardInfo->flags;
        keyEvent.ExtraInfo = keyboardInfo->dwExtraInfo;

        if (_callback(keyEvent))
        {
            return 1;
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
