#include "InputReplay.h"

#include "..\Common\NativeHelpers.h"

#include <vector>
#include <string>
#include <cstring>

namespace TigerClawHookNative
{
    namespace
    {
        constexpr UINT ClipboardTextFormat = 13; // CF_UNICODETEXT
        constexpr WORD VirtualKeyV = 0x56;
        constexpr WORD VirtualKeySlash = 0xBF;
        constexpr UINT ReplaceSelectionMessage = 0x00C2; // EM_REPLACESEL
        constexpr UINT CharMessage = 0x0102; // WM_CHAR
        constexpr DWORD AsciiKeyStrokeDelayMs = 20;
    }

    InputReplay::InputReplay()
    {
        InitializeCriticalSection(&_lock);
        _lockInitialized = true;
    }

    InputReplay::~InputReplay()
    {
        if (_lockInitialized)
        {
            DeleteCriticalSection(&_lock);
            _lockInitialized = false;
        }
    }

    bool InputReplay::ShouldSuppressHookEvent(UINT flags, ULONG_PTR extraInfo)
    {
        if ((flags & LLKHF_INJECTED) == 0 || extraInfo != ReplayMarker)
        {
            return false;
        }

        EnterCriticalSection(&_lock);
        const long long now = GetNowTick();
        const bool active = _pendingReplayEvents > 0 || now <= _activeReplayUntilTick;
        if (!active)
        {
            LeaveCriticalSection(&_lock);
            return false;
        }

        if (_pendingReplayEvents > 0)
        {
            --_pendingReplayEvents;
        }

        LeaveCriticalSection(&_lock);
        return true;
    }

    bool InputReplay::IsReplayEvent(const KBDLLHOOKSTRUCT& keyboardInfo) const
    {
        return (keyboardInfo.flags & LLKHF_INJECTED) != 0 && keyboardInfo.dwExtraInfo == ReplayMarker;
    }

    bool InputReplay::TrySendUnicodeText(const std::wstring& text, std::wstring& error)
    {
        error.clear();
        if (text.empty())
        {
            return true;
        }

        std::vector<INPUT> inputs;
        inputs.reserve(text.size() * 2);
        for (wchar_t ch : text)
        {
            INPUT down = {};
            down.type = INPUT_KEYBOARD;
            down.ki.wVk = 0;
            down.ki.wScan = static_cast<WORD>(ch);
            down.ki.dwFlags = KEYEVENTF_UNICODE;
            down.ki.dwExtraInfo = ReplayMarker;
            inputs.push_back(down);

            INPUT up = down;
            up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs.push_back(up);
        }

        EnterCriticalSection(&_lock);
        _pendingReplayEvents = static_cast<int>(inputs.size());
        _activeReplayUntilTick = GetNowTick() + 200;
        LeaveCriticalSection(&_lock);

        UINT sent = SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));
        if (sent != inputs.size())
        {
            EnterCriticalSection(&_lock);
            _pendingReplayEvents = 0;
            _activeReplayUntilTick = 0;
            LeaveCriticalSection(&_lock);
            error = L"SendInput failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        return true;
    }

    bool InputReplay::TrySendAsciiKeyStrokeText(const std::wstring& text, std::wstring& error) const
    {
        error.clear();
        if (text.empty())
        {
            return true;
        }

        for (size_t index = 0; index < text.size(); ++index)
        {
            wchar_t ch = text[index];
            WORD virtualKey = 0;
            if (ch >= L'a' && ch <= L'z')
            {
                virtualKey = static_cast<WORD>(L'A' + (ch - L'a'));
            }
            else if (ch == L'/')
            {
                virtualKey = VirtualKeySlash;
            }
            else
            {
                error = L"unsupported keystroke character";
                return false;
            }

            INPUT inputs[2] = {};
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = virtualKey;
            inputs[0].ki.dwExtraInfo = ReplayMarker;

            inputs[1] = inputs[0];
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

            EnterCriticalSection(&_lock);
            _pendingReplayEvents = 2;
            _activeReplayUntilTick = GetNowTick() + 200;
            LeaveCriticalSection(&_lock);

            const UINT sent = SendInput(2, inputs, sizeof(INPUT));
            if (sent != 2)
            {
                EnterCriticalSection(&_lock);
                _pendingReplayEvents = 0;
                _activeReplayUntilTick = 0;
                LeaveCriticalSection(&_lock);
                error = L"SendInput failed: " + GetLastErrorMessage(GetLastError());
                return false;
            }

            if (index + 1 < text.size())
            {
                Sleep(AsciiKeyStrokeDelayMs);
            }
        }

        return true;
    }

    bool InputReplay::TryPasteTextViaClipboard(const std::wstring& text, std::wstring& error) const
    {
        error.clear();
        if (text.empty())
        {
            return true;
        }

        if (!OpenClipboard(nullptr))
        {
            error = L"OpenClipboard failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        HGLOBAL clipboardHandle = nullptr;
        bool success = false;

        do
        {
            if (!EmptyClipboard())
            {
                error = L"EmptyClipboard failed: " + GetLastErrorMessage(GetLastError());
                break;
            }

            const SIZE_T bufferBytes = (text.size() + 1) * sizeof(wchar_t);
            clipboardHandle = GlobalAlloc(GMEM_MOVEABLE, bufferBytes);
            if (clipboardHandle == nullptr)
            {
                error = L"GlobalAlloc failed: " + GetLastErrorMessage(GetLastError());
                break;
            }

            void* locked = GlobalLock(clipboardHandle);
            if (locked == nullptr)
            {
                error = L"GlobalLock failed: " + GetLastErrorMessage(GetLastError());
                break;
            }

            memcpy(locked, text.c_str(), bufferBytes);
            GlobalUnlock(clipboardHandle);

            if (SetClipboardData(ClipboardTextFormat, clipboardHandle) == nullptr)
            {
                error = L"SetClipboardData failed: " + GetLastErrorMessage(GetLastError());
                break;
            }

            clipboardHandle = nullptr;
            success = true;
        } while (false);

        CloseClipboard();
        if (!success)
        {
            if (clipboardHandle != nullptr)
            {
                GlobalFree(clipboardHandle);
            }

            return false;
        }

        INPUT inputs[4] = {};

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_LCONTROL;
        inputs[0].ki.dwExtraInfo = ReplayMarker;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VirtualKeyV;
        inputs[1].ki.dwExtraInfo = ReplayMarker;

        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = VirtualKeyV;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[2].ki.dwExtraInfo = ReplayMarker;

        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_LCONTROL;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].ki.dwExtraInfo = ReplayMarker;

        EnterCriticalSection(&_lock);
        _pendingReplayEvents = 4;
        _activeReplayUntilTick = GetNowTick() + 300;
        LeaveCriticalSection(&_lock);

        const UINT sent = SendInput(4, inputs, sizeof(INPUT));
        if (sent != 4)
        {
            EnterCriticalSection(&_lock);
            _pendingReplayEvents = 0;
            _activeReplayUntilTick = 0;
            LeaveCriticalSection(&_lock);
            error = L"SendInput failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        return true;
    }

    bool InputReplay::TryReplaceSelectedText(const std::wstring& text, std::wstring& error) const
    {
        error.clear();
        if (text.empty())
        {
            return true;
        }

        HWND foreground = GetForegroundWindow();
        if (foreground == nullptr)
        {
            error = L"GetForegroundWindow returned null.";
            return false;
        }

        DWORD threadId = GetWindowThreadProcessId(foreground, nullptr);
        GUITHREADINFO guiThreadInfo = {};
        guiThreadInfo.cbSize = sizeof(guiThreadInfo);

        HWND target = foreground;
        if (threadId != 0 && GetGUIThreadInfo(threadId, &guiThreadInfo) != FALSE && guiThreadInfo.hwndFocus != nullptr)
        {
            target = guiThreadInfo.hwndFocus;
        }

        SetLastError(ERROR_SUCCESS);
        const LRESULT result = SendMessageW(
            target,
            ReplaceSelectionMessage,
            TRUE,
            reinterpret_cast<LPARAM>(text.c_str()));

        const DWORD lastError = GetLastError();
        if (result == 0 && lastError != ERROR_SUCCESS)
        {
            error = L"SendMessageW(EM_REPLACESEL) failed: " + GetLastErrorMessage(lastError);
            return false;
        }

        return true;
    }

    bool InputReplay::TryPostCharText(const std::wstring& text, std::wstring& error) const
    {
        error.clear();
        if (text.empty())
        {
            return true;
        }

        HWND foreground = GetForegroundWindow();
        if (foreground == nullptr)
        {
            error = L"GetForegroundWindow returned null.";
            return false;
        }

        DWORD threadId = GetWindowThreadProcessId(foreground, nullptr);
        GUITHREADINFO guiThreadInfo = {};
        guiThreadInfo.cbSize = sizeof(guiThreadInfo);

        HWND target = foreground;
        if (threadId != 0 && GetGUIThreadInfo(threadId, &guiThreadInfo) != FALSE && guiThreadInfo.hwndFocus != nullptr)
        {
            target = guiThreadInfo.hwndFocus;
        }

        for (wchar_t ch : text)
        {
            SetLastError(ERROR_SUCCESS);
            const LRESULT result = SendMessageW(target, CharMessage, static_cast<WPARAM>(ch), 0);
            const DWORD lastError = GetLastError();
            if (result == 0 && lastError != ERROR_SUCCESS)
            {
                error = L"SendMessageW(WM_CHAR) failed: " + GetLastErrorMessage(lastError);
                return false;
            }
        }

        return true;
    }

    bool InputReplay::TrySendVirtualKey(WORD virtualKey, std::wstring& error) const
    {
        INPUT inputs[2] = {};

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = virtualKey;
        inputs[0].ki.dwExtraInfo = ReplayMarker;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = virtualKey;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[1].ki.dwExtraInfo = ReplayMarker;

        EnterCriticalSection(&_lock);
        _pendingReplayEvents = 2;
        _activeReplayUntilTick = GetNowTick() + 200;
        LeaveCriticalSection(&_lock);

        UINT sent = SendInput(2, inputs, sizeof(INPUT));
        if (sent != 2)
        {
            EnterCriticalSection(&_lock);
            _pendingReplayEvents = 0;
            _activeReplayUntilTick = 0;
            LeaveCriticalSection(&_lock);
            error = L"SendInput failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        return true;
    }

    bool InputReplay::TrySendVirtualKeyUp(WORD virtualKey, std::wstring& error) const
    {
        INPUT input = {};
        input.type = INPUT_KEYBOARD;
        input.ki.wVk = virtualKey;
        input.ki.dwFlags = KEYEVENTF_KEYUP;
        input.ki.dwExtraInfo = ReplayMarker;

        EnterCriticalSection(&_lock);
        _pendingReplayEvents = 1;
        _activeReplayUntilTick = GetNowTick() + 200;
        LeaveCriticalSection(&_lock);

        UINT sent = SendInput(1, &input, sizeof(INPUT));
        if (sent != 1)
        {
            EnterCriticalSection(&_lock);
            _pendingReplayEvents = 0;
            _activeReplayUntilTick = 0;
            LeaveCriticalSection(&_lock);
            error = L"SendInput failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        return true;
    }

    long long InputReplay::GetNowTick()
    {
        return static_cast<unsigned long>(GetTickCount());
    }
}
