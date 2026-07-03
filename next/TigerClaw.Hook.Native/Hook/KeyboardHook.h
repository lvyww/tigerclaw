#pragma once

#include <windows.h>

#include <functional>

namespace TigerClawHookNative
{
    struct KeyboardHookEvent
    {
        DWORD VirtualKey = 0;
        DWORD ScanCode = 0;
        bool IsKeyDown = false;
        bool IsKeyUp = false;
        bool IsInjected = false;
        bool IsExtended = false;
        DWORD Flags = 0;
        ULONG_PTR ExtraInfo = 0;
    };

    class KeyboardHook
    {
    public:
        using Callback = std::function<bool(const KeyboardHookEvent&)>;

        KeyboardHook();
        ~KeyboardHook();

        bool Install(const Callback& callback);
        void Uninstall();

    private:
        static LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam);
        LRESULT HandleHook(int code, WPARAM wParam, LPARAM lParam) const;

        static KeyboardHook* CurrentInstance;

        HHOOK _hook = nullptr;
        Callback _callback;
    };
}
