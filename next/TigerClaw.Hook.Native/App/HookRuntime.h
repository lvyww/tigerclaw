#pragma once

#include "..\Common\Logger.h"
#include "..\Common\CoreLaunchHelper.h"
#include "..\Hook\KeyboardHook.h"
#include "..\IPC\PipeClient.h"
#include "..\Replay\InputReplay.h"
#include "..\State\HookState.h"
#include "..\Tracking\CaretTracker.h"
#include "..\Tracking\FocusTracker.h"
#include <deque>

namespace TigerClawHookNative
{
    class HookRuntime
    {
    public:
        HookRuntime();
        int Run();

    private:
        static constexpr UINT StatePumpIntervalMs = 200;
        static constexpr long long PreciseCaretCooldownMs = 800;
        static constexpr UINT CaretPublishMessage = WM_APP + 1;
        static constexpr UINT EnsureEnglishLayoutMessage = WM_APP + 2;
        static constexpr wchar_t HookNativeExitEventName[] = L"Local\\TigerClaw.Hook.Native.Exit.v1";

        void OnStatePump();
        bool OnKeyboardEvent(const KeyboardHookEvent& keyEvent);
        bool ApplyKeyResponse(const KeyboardHookEvent& keyEvent, const CoreResponse& response, const FocusSnapshot& focus);
        void DrainPendingKeys();
        void DropPendingKeys();
        struct PendingKey
        {
            KeyboardHookEvent Event;
            FocusSnapshot Focus;
            std::string Request;
            ULONGLONG Tick;
        };
        std::deque<PendingKey> _pendingKeys;
        bool _compositionCancelPending = false;
        bool _replayedDown[256] = {};
        void TryPublishTrackedCaret(bool preferPrecise);
        void RequestEnsureEnglishSystemLayout();
        void EnsureEnglishSystemLayout();
        void RestoreStartupSystemLayout();
        bool RefreshFocusSnapshot();
        bool ShouldHandleAltBackslashToggle(const KeyboardHookEvent& keyEvent) const;
        bool ShouldSuppressToggleAltKeyUp(const KeyboardHookEvent& keyEvent) const;
        bool HandleAltBackslashToggle(const FocusSnapshot& focus);
        HKL GetPreferredRestoreKeyboardLayout() const;
        bool TryReplayCommitText(const std::wstring& text, const FocusSnapshot& focus, bool preferPostedChars = false);

        FocusTracker _focusTracker;
        CaretTracker _caretTracker;
        PipeClient _pipeClient;
        InputReplay _inputReplay;
        CoreLaunchHelper _coreLaunchHelper;
        HookState _state;
        bool _focusPublishPending = true;
        KeyboardHook _keyboardHook;
        UINT_PTR _statePumpTimerId = 0;
        DWORD _threadId = 0;
        bool _started = false;
        volatile LONG _pumpRunning = 0;
        long long _lastPreciseCaretTick = 0;
        volatile LONG _pendingCaretPublishMode = 0;
        volatile LONG _pendingEnsureEnglishLayout = 0;
        volatile LONG _pendingHelloRetry = 0;
        CaretSnapshot _cachedLightweightCaret;
        HKL _startupKeyboardLayout = nullptr;
        HKL _lastNonEnglishKeyboardLayout = nullptr;
        bool _startupKeyboardLayoutCaptured = false;
        bool _systemLayoutForcedByHook = false;
        HANDLE _exitEvent = nullptr;
        bool _suppressToggleKeyUp = false;
        bool _suppressToggleAltKeyUp = false;
        WORD _toggleAltVirtualKey = VK_MENU;
    };
}
