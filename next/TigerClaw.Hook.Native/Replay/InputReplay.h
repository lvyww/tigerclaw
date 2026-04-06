#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    class InputReplay
    {
    public:
        static constexpr ULONG_PTR ReplayMarker = 0x5443484F4F4B0001ULL;

        bool ShouldSuppressHookEvent(UINT flags, ULONG_PTR extraInfo);
        bool IsReplayEvent(const KBDLLHOOKSTRUCT& keyboardInfo) const;
        bool TrySendUnicodeText(const std::wstring& text, std::wstring& error);
        bool TrySendAsciiKeyStrokeText(const std::wstring& text, std::wstring& error) const;
        bool TryReplaceSelectedText(const std::wstring& text, std::wstring& error) const;
        bool TryPostCharText(const std::wstring& text, std::wstring& error) const;
        bool TryPasteTextViaClipboard(const std::wstring& text, std::wstring& error) const;
        bool TrySendVirtualKey(WORD virtualKey, std::wstring& error) const;
        bool TrySendVirtualKeyUp(WORD virtualKey, std::wstring& error) const;

    private:
        static long long GetNowTick();

        mutable CRITICAL_SECTION _lock = {};
        mutable bool _lockInitialized = false;
        mutable int _pendingReplayEvents = 0;
        mutable long long _activeReplayUntilTick = 0;

    public:
        InputReplay();
        ~InputReplay();
    };
}
