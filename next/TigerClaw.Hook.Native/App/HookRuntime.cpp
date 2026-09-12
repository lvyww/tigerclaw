#include "HookRuntime.h"

#include "..\Common\Logger.h"
#include "..\Common\NativeHelpers.h"

#include <objbase.h>
#include <algorithm>
#include <iterator>
#include <sstream>

namespace
{
    constexpr wchar_t EnglishLayoutName[] = L"00000409";
    constexpr UINT KeyboardLayoutActivateFlag = 0x00000001;
    constexpr UINT WM_INPUTLANGCHANGEREQUEST_CONST = 0x0050;

    bool ShouldReplayByAsciiKeystrokes(const std::wstring& text)
    {
        if (text.length() < 2 || text.length() > 6 || text[0] != L'/')
        {
            return false;
        }

        for (size_t i = 1; i < text.length(); ++i)
        {
            const wchar_t ch = text[i];
            if (ch < L'a' || ch > L'z')
            {
                return false;
            }
        }

        return true;
    }

    bool IsEnglishKeyboardLayout(HKL keyboardLayout)
    {
        return keyboardLayout != nullptr &&
               ((reinterpret_cast<UINT_PTR>(keyboardLayout) & 0xFFFFu) == 0x0409u);
    }

    HKL GetForegroundKeyboardLayout()
    {
        HWND foreground = GetForegroundWindow();
        DWORD threadId = foreground != nullptr ? GetWindowThreadProcessId(foreground, nullptr) : 0;
        return GetKeyboardLayout(threadId);
    }

    bool PostKeyboardLayoutToForeground(HKL keyboardLayout)
    {
        HWND foreground = GetForegroundWindow();
        if (foreground == nullptr || keyboardLayout == nullptr)
        {
            return false;
        }

        return PostMessageW(foreground, WM_INPUTLANGCHANGEREQUEST_CONST, 0, reinterpret_cast<LPARAM>(keyboardLayout)) != FALSE;
    }

    bool BroadcastKeyboardLayout(HKL keyboardLayout)
    {
        if (keyboardLayout == nullptr)
        {
            return false;
        }

        return PostMessageW(
                   HWND_BROADCAST,
                   WM_INPUTLANGCHANGEREQUEST_CONST,
                   0,
                   reinterpret_cast<LPARAM>(keyboardLayout)) != FALSE;
    }
}

namespace TigerClawHookNative
{
    HookRuntime::HookRuntime() = default;

    int HookRuntime::Run()
    {
        if (_started)
        {
            return 0;
        }

        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        _threadId = GetCurrentThreadId();
        _exitEvent = CreateEventW(nullptr, TRUE, FALSE, HookNativeExitEventName);
        bool trialExpired = false;
        std::wstring trialExpireUtc;
        if (_startupIntegrity.IsTrialExpiredNow(trialExpired, trialExpireUtc))
        {
            std::wstringstream trialStream;
            trialStream << L"trial expire_utc=" << trialExpireUtc
                        << L" expired=" << (trialExpired ? L"true" : L"false");
            Logger::Info(L"trial", trialStream.str());
            if (trialExpired)
            {
                Logger::Info(L"runtime", L"native hook startup blocked by embedded trial expiry");
                if (_exitEvent != nullptr)
                {
                    CloseHandle(_exitEvent);
                    _exitEvent = nullptr;
                }
                CoUninitialize();
                return 0;
            }
        }
        else
        {
            Logger::Info(L"trial", std::wstring(L"trial expire utc unavailable: ") + trialExpireUtc);
        }

        _startupKeyboardLayout = GetForegroundKeyboardLayout();
        _startupKeyboardLayoutCaptured = (_startupKeyboardLayout != nullptr);
        if (_startupKeyboardLayoutCaptured && !IsEnglishKeyboardLayout(_startupKeyboardLayout))
        {
            _lastNonEnglishKeyboardLayout = _startupKeyboardLayout;
        }

        CoreResponse helloResponse = {};
        std::wstring error;
        if (!_pipeClient.TryHello(helloResponse, error))
        {
            Logger::Info(L"core", std::wstring(L"hello failed: ") + error);
            if (!_pipeClient.IsCommunicationBlocked())
            {
                _coreLaunchHelper.TryLaunchCoreIfNeeded(L"hello failed");
                InterlockedExchange(&_pendingHelloRetry, 1);
            }
        }
        else if (helloResponse.EnsureSystemLayoutEn)
        {
            _state.SetNativeHookAltBackslashToggleEnabled(helloResponse.NativeHookAltBackslashToggleEnabled);
            _state.SetAutoSwitchSystemLayoutEnabled(helloResponse.AutoSwitchSystemLayoutEnabled);
            _state.SetUseClipboardCommit(helloResponse.UseClipboardCommit);
            _state.SetClipboardCommitWhitelist(helloResponse.ClipboardCommitWhitelist);
            InterlockedExchange(&_pendingEnsureEnglishLayout, 1);
        }
        else
        {
            _state.SetNativeHookAltBackslashToggleEnabled(helloResponse.NativeHookAltBackslashToggleEnabled);
            _state.SetAutoSwitchSystemLayoutEnabled(helloResponse.AutoSwitchSystemLayoutEnabled);
            _state.SetUseClipboardCommit(helloResponse.UseClipboardCommit);
            _state.SetClipboardCommitWhitelist(helloResponse.ClipboardCommitWhitelist);
        }

        if (!_keyboardHook.Install([this](const KeyboardHookEvent& keyEvent) { return OnKeyboardEvent(keyEvent); }))
        {
            Logger::Info(L"hook", std::wstring(L"install failed: ") + GetLastErrorMessage(GetLastError()));
            CoUninitialize();
            return 1;
        }

        _statePumpTimerId = SetTimer(nullptr, 0, StatePumpIntervalMs, nullptr);
        if (_statePumpTimerId == 0)
        {
            Logger::Info(L"runtime", std::wstring(L"state timer install failed: ") + GetLastErrorMessage(GetLastError()));
            _keyboardHook.Uninstall();
            CoUninitialize();
            return 1;
        }
        _started = true;
        Logger::Info(L"runtime", L"native hook runtime started");
        if (InterlockedExchange(&_pendingEnsureEnglishLayout, 0) != 0)
        {
            EnsureEnglishSystemLayout();
        }

        MSG message = {};
        while (GetMessageW(&message, nullptr, 0, 0) > 0)
        {
            if (message.message == WM_TIMER && message.wParam == _statePumpTimerId)
            {
                OnStatePump();
                continue;
            }

            if (message.message == CaretPublishMessage)
            {
                const LONG mode = InterlockedExchange(&_pendingCaretPublishMode, 0);
                if (mode != 0)
                {
                    TryPublishTrackedCaret(mode > 1);
                }
                continue;
            }

            if (message.message == EnsureEnglishLayoutMessage)
            {
                if (InterlockedExchange(&_pendingEnsureEnglishLayout, 0) != 0)
                {
                    EnsureEnglishSystemLayout();
                }
                continue;
            }

            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        if (_statePumpTimerId != 0)
        {
            KillTimer(nullptr, _statePumpTimerId);
            _statePumpTimerId = 0;
        }
        if (_exitEvent != nullptr)
        {
            CloseHandle(_exitEvent);
            _exitEvent = nullptr;
        }
        _keyboardHook.Uninstall();
        RestoreStartupSystemLayout();
        _started = false;
        Logger::Info(L"runtime", L"native hook runtime stopped");
        CoUninitialize();
        return 0;
    }

    void HookRuntime::OnStatePump()
    {
        if (InterlockedExchange(&_pumpRunning, 1) != 0)
        {
            return;
        }

        if (_exitEvent != nullptr && WaitForSingleObject(_exitEvent, 0) == WAIT_OBJECT_0)
        {
            PostQuitMessage(0);
            InterlockedExchange(&_pumpRunning, 0);
            return;
        }

        if (InterlockedExchange(&_pendingHelloRetry, 0) != 0)
        {
            CoreResponse helloResponse = {};
            std::wstring helloError;
            if (_pipeClient.TryHello(helloResponse, helloError))
            {
                Logger::Info(L"core", L"hello retry succeeded");
                _state.SetNativeHookAltBackslashToggleEnabled(helloResponse.NativeHookAltBackslashToggleEnabled);
                _state.SetAutoSwitchSystemLayoutEnabled(helloResponse.AutoSwitchSystemLayoutEnabled);
                _state.SetUseClipboardCommit(helloResponse.UseClipboardCommit);
                _state.SetClipboardCommitWhitelist(helloResponse.ClipboardCommitWhitelist);
                if (helloResponse.EnsureSystemLayoutEn)
                {
                    RequestEnsureEnglishSystemLayout();
                }
            }
            else
            {
                Logger::Info(L"core", std::wstring(L"hello retry failed: ") + helloError);
                if (!_pipeClient.IsCommunicationBlocked())
                {
                    InterlockedExchange(&_pendingHelloRetry, 1);
                }
            }
        }

        HKL currentKeyboardLayout = GetForegroundKeyboardLayout();
        if (currentKeyboardLayout != nullptr && !IsEnglishKeyboardLayout(currentKeyboardLayout))
        {
            _lastNonEnglishKeyboardLayout = currentKeyboardLayout;
        }

        RefreshFocusSnapshot();

        CaretSnapshot lightweightCaret = {};
        DrainPendingKeys();
        if (_caretTracker.TryGetLightweightSnapshot(lightweightCaret))
        {
            const bool changed = !_cachedLightweightCaret.Equals(lightweightCaret);
            _cachedLightweightCaret = lightweightCaret;
            std::wstringstream caretStream;
            caretStream << L"light caret x=" << lightweightCaret.X
                        << L" y=" << lightweightCaret.Y
                        << L" w=" << lightweightCaret.Width
                        << L" h=" << lightweightCaret.Height
                        << L" changed=" << (changed ? L"true" : L"false")
                        << L" wants=" << (_state.WantsCaretTracking() ? L"true" : L"false");
        }
        else
        {
            _cachedLightweightCaret = {};
            std::wstringstream caretMissStream;
            caretMissStream << L"light caret unavailable wants=" << (_state.WantsCaretTracking() ? L"true" : L"false");
        }

        InterlockedExchange(&_pumpRunning, 0);
    }

    bool HookRuntime::RefreshFocusSnapshot()
    {
        FocusSnapshot focus = {};
        if (!_focusTracker.TryGetSnapshot(focus))
        {
            return false;
        }

        const bool changed = _state.UpdateFocus(focus);
        if (changed && (!_pendingKeys.empty() ||
            std::any_of(std::begin(_replayedDown), std::end(_replayedDown), [](bool down) { return down; })))
        {
            DropPendingKeys();
        }
        _focusPublishPending = _focusPublishPending || changed || _pipeClient.NeedsFocusSync();
        if (!_focusPublishPending)
        {
            return false;
        }

        std::wstringstream stream;
        stream << L"focus hwnd=0x" << std::hex << reinterpret_cast<UINT_PTR>(focus.Window)
               << L" pid=" << std::dec << focus.ProcessId
               << L" class=" << focus.ClassName;
        Logger::Info(L"focus", stream.str());

        std::wstring error;
        if (!_pipeClient.TrySendFocus(focus, error))
        {
            Logger::Info(L"core", std::wstring(L"focus request failed: ") + error);
        }
        else
        {
            _focusPublishPending = false;
        }

        return true;
    }

    void HookRuntime::TryPublishTrackedCaret(bool preferPrecise)
    {
        if (!_state.WantsCaretTracking())
        {
            return;
        }

        const CaretSnapshot cachedCaret = _cachedLightweightCaret;
        CaretSnapshot resolvedCaret = {};
        const bool resolved = _caretTracker.TryResolvePublishedSnapshot(cachedCaret, resolvedCaret);

        if (!resolved)
        {
            return;
        }

        if (resolvedCaret.IsPrecise)
        {
            _lastPreciseCaretTick = static_cast<long long>(GetTickCount());
        }

        const bool changed = _state.UpdateCaret(resolvedCaret);
        std::wstringstream caretStream;
        caretStream << L"resolved caret x=" << resolvedCaret.X
                    << L" y=" << resolvedCaret.Y
                    << L" w=" << resolvedCaret.Width
                    << L" h=" << resolvedCaret.Height
                    << L" changed=" << (changed ? L"true" : L"false")
                    << L" precise=" << (resolvedCaret.IsPrecise ? L"true" : L"false")
                    << L" preferPrecise=" << (preferPrecise ? L"true" : L"false");

        std::wstring error;
        if (!_pipeClient.TrySendCaret(resolvedCaret, error))
        {
            Logger::Info(L"core", std::wstring(L"caret request failed: ") + error);
            return;
        }

        std::wstringstream sentStream;
        sentStream << L"sent caret x=" << resolvedCaret.X
                   << L" y=" << resolvedCaret.Y
                   << L" precise=" << (resolvedCaret.IsPrecise ? L"true" : L"false");
    }

    void HookRuntime::RequestEnsureEnglishSystemLayout()
    {
        if (InterlockedExchange(&_pendingEnsureEnglishLayout, 1) == 0)
        {
            PostThreadMessageW(_threadId, EnsureEnglishLayoutMessage, 0, 0);
        }
    }

    void HookRuntime::EnsureEnglishSystemLayout()
    {
        if (!_state.AutoSwitchSystemLayoutEnabled())
        {
            return;
        }

        HKL current = GetForegroundKeyboardLayout();
        if (IsEnglishKeyboardLayout(current))
        {
            return;
        }

        if (current != nullptr && !IsEnglishKeyboardLayout(current))
        {
            _lastNonEnglishKeyboardLayout = current;
        }

        if (!_startupKeyboardLayoutCaptured && current != nullptr)
        {
            _startupKeyboardLayout = current;
            _startupKeyboardLayoutCaptured = true;
        }

        HKL englishLayout = LoadKeyboardLayoutW(EnglishLayoutName, KeyboardLayoutActivateFlag);
        if (englishLayout == nullptr)
        {
            Logger::Info(L"lang", std::wstring(L"load en layout failed: ") + GetLastErrorMessage(GetLastError()));
            return;
        }

        if (!PostKeyboardLayoutToForeground(englishLayout))
        {
            Logger::Info(L"lang", std::wstring(L"post en layout failed: ") + GetLastErrorMessage(GetLastError()));
            return;
        }

        _systemLayoutForcedByHook = true;
        Logger::Info(L"lang", L"requested system keyboard layout 00000409");
    }

    void HookRuntime::RestoreStartupSystemLayout()
    {
        if (!_systemLayoutForcedByHook)
        {
            return;
        }

        HKL restoreLayout = GetPreferredRestoreKeyboardLayout();
        if (restoreLayout == nullptr || IsEnglishKeyboardLayout(restoreLayout))
        {
            return;
        }

        ActivateKeyboardLayout(restoreLayout, 0);

        if (!BroadcastKeyboardLayout(restoreLayout))
        {
            Logger::Info(L"lang", std::wstring(L"restore startup layout failed: ") + GetLastErrorMessage(GetLastError()));
            return;
        }

        _systemLayoutForcedByHook = false;
        Logger::Info(L"lang", L"restored startup keyboard layout");
    }

    bool HookRuntime::ShouldHandleAltBackslashToggle(const KeyboardHookEvent& keyEvent) const
    {
        if (!_state.NativeHookAltBackslashToggleEnabled() || keyEvent.VirtualKey != VK_OEM_5)
        {
            return false;
        }

        if (keyEvent.IsKeyDown)
        {
            return _state.AltDown();
        }

        if (keyEvent.IsKeyUp)
        {
            return _suppressToggleKeyUp;
        }

        return false;
    }

    bool HookRuntime::ShouldSuppressToggleAltKeyUp(const KeyboardHookEvent& keyEvent) const
    {
        if (!_suppressToggleAltKeyUp || !keyEvent.IsKeyUp)
        {
            return false;
        }

        return keyEvent.VirtualKey == _toggleAltVirtualKey || keyEvent.VirtualKey == VK_MENU;
    }

    bool HookRuntime::HandleAltBackslashToggle(const FocusSnapshot& focus)
    {
        if (_suppressToggleKeyUp)
        {
            _suppressToggleKeyUp = false;
            return true;
        }

        const bool disable = !_state.DisabledByHotkey();
        _state.SetDisabledByHotkey(disable);
        _suppressToggleKeyUp = true;
        _suppressToggleAltKeyUp = true;
        _toggleAltVirtualKey = static_cast<WORD>(_state.ActiveAltVirtualKey());

        std::wstring altError;
        if (!_inputReplay.TrySendVirtualKeyUp(_toggleAltVirtualKey, altError))
        {
            Logger::Info(L"replay", std::wstring(L"Alt keyup send failed: ") + altError);
        }

        if (disable)
        {
            _state.ResetCompositionState();
            _cachedLightweightCaret = {};
            InterlockedExchange(&_pendingCaretPublishMode, 0);

            std::wstring error;
            if (!_pipeClient.TrySendCompositionCanceled(error))
            {
                Logger::Info(L"core", std::wstring(L"composition_canceled failed: ") + error);
            }
            if (!_pipeClient.TrySendHookDisabled(true, error))
            {
                Logger::Info(L"core", std::wstring(L"hook_native_disabled failed: ") + error);
            }

            if (_state.AutoSwitchSystemLayoutEnabled())
            {
                HKL restoreLayout = GetPreferredRestoreKeyboardLayout();
                if (restoreLayout != nullptr)
                {
                    ActivateKeyboardLayout(restoreLayout, 0);
                    PostKeyboardLayoutToForeground(restoreLayout);
                    _systemLayoutForcedByHook = false;
                }
            }

            Logger::Info(L"runtime", L"native hook disabled by Alt+\\");
        }
        else
        {
            std::wstring error;
            if (!_pipeClient.TrySendHookDisabled(false, error))
            {
                Logger::Info(L"core", std::wstring(L"hook_native_enabled notify failed: ") + error);
            }
            RequestEnsureEnglishSystemLayout();
            Logger::Info(L"runtime", L"native hook enabled by Alt+\\");
        }

        (void)focus;
        return true;
    }

    HKL HookRuntime::GetPreferredRestoreKeyboardLayout() const
    {
        if (_lastNonEnglishKeyboardLayout != nullptr && !IsEnglishKeyboardLayout(_lastNonEnglishKeyboardLayout))
        {
            return _lastNonEnglishKeyboardLayout;
        }

        if (_startupKeyboardLayoutCaptured && _startupKeyboardLayout != nullptr && !IsEnglishKeyboardLayout(_startupKeyboardLayout))
        {
            return _startupKeyboardLayout;
        }

        return nullptr;
    }

    bool HookRuntime::TryReplayCommitText(const std::wstring& text, const FocusSnapshot& focus, bool preferPostedChars)
    {
        if (text.empty())
        {
            return true;
        }

        std::wstring replayError;
        const bool shouldUseAsciiKeystrokes = ShouldReplayByAsciiKeystrokes(text);
        const bool shouldUseClipboardCommit =
            !shouldUseAsciiKeystrokes &&
            (text.find(L'\n') != std::wstring::npos ||
             _state.ShouldUseClipboardCommitForFocus(focus));
        const bool shouldUsePostedChars =
            preferPostedChars &&
            !shouldUseAsciiKeystrokes &&
            !shouldUseClipboardCommit;
        const bool replayOk = shouldUseAsciiKeystrokes
            ? _inputReplay.TrySendAsciiKeyStrokeText(text, replayError)
            : shouldUseClipboardCommit
            ? _inputReplay.TryPasteTextViaClipboard(text, replayError)
            : shouldUsePostedChars
            ? _inputReplay.TryPostCharText(text, replayError)
            : _inputReplay.TrySendUnicodeText(text, replayError);
        if (!replayOk)
        {
            Logger::Info(L"replay", std::wstring(L"commit replay failed: ") + replayError);
            return false;
        }

        if (shouldUseAsciiKeystrokes)
        {
            Logger::Info(L"replay", L"commit via ascii keystrokes");
        }
        else if (shouldUseClipboardCommit)
        {
            Logger::Info(L"replay", std::wstring(L"clipboard commit process=") + focus.ProcessName);
        }
        else if (shouldUsePostedChars)
        {
            Logger::Info(L"replay", L"commit via WM_CHAR");
        }
        else
        {
            Logger::Info(L"replay", L"commit via SendInput");
        }

        return true;
    }

    bool HookRuntime::OnKeyboardEvent(const KeyboardHookEvent& keyEvent)
    {
        if (_inputReplay.ShouldSuppressHookEvent(keyEvent.Flags, keyEvent.ExtraInfo))
        {
            return false;
        }
        _state.UpdateModifierState(keyEvent.VirtualKey, keyEvent.IsKeyDown, keyEvent.IsKeyUp);
        RefreshFocusSnapshot();

        if (ShouldSuppressToggleAltKeyUp(keyEvent))
        {
            _suppressToggleAltKeyUp = false;
            return true;
        }

        const FocusSnapshot focus = _state.GetFocus();
        const CaretSnapshot& caret = _state.GetCaret();

        if (ShouldHandleAltBackslashToggle(keyEvent))
        {
            if (!_pendingKeys.empty()) DropPendingKeys();
            return HandleAltBackslashToggle(focus);
        }

        if (_state.DisabledByHotkey())
        {
            return false;
        }

        std::wstringstream stream;
        stream << L"vk=" << keyEvent.VirtualKey
               << L" scan=" << keyEvent.ScanCode
               << L" down=" << (keyEvent.IsKeyDown ? L"true" : L"false");
        Logger::Info(L"key", stream.str());

        if (keyEvent.IsKeyDown && focus.IsGdqLike())
        {
            std::wstring trainerError;
            if (!_inputReplay.TrySendVirtualKey(0x7D, trainerError))
            {
                Logger::Info(L"replay", std::wstring(L"F14 send failed: ") + trainerError);
            }
        }

        CoreResponse response = {};
        std::wstring error;
        PendingKey pending = { keyEvent, focus, _pipeClient.PrepareKey(keyEvent, _state, caret), GetTickCount64() };
        if (_pendingKeys.size() >= 128) DropPendingKeys();
        if (!_pendingKeys.empty() || _compositionCancelPending)
        {
            _pendingKeys.push_back(pending);
            return true;
        }
        if (!_pipeClient.TrySendPreparedKey(pending.Request, focus, response, error))
        {
            Logger::Info(L"core", std::wstring(L"key request failed: ") + error);
            if (!_pipeClient.IsCommunicationBlocked())
            {
                _coreLaunchHelper.TryLaunchCoreIfNeeded(L"key request failed");
            }
            _pendingKeys.push_back(pending);
            return true;
        }

        if (GetForegroundWindow() != focus.Window)
        {
            DropPendingKeys();
            return true;
        }
        if (keyEvent.IsKeyUp && keyEvent.VirtualKey < 256) _replayedDown[keyEvent.VirtualKey] = false;
        return ApplyKeyResponse(keyEvent, response, focus);
    }

    bool HookRuntime::ApplyKeyResponse(const KeyboardHookEvent& keyEvent, const CoreResponse& response, const FocusSnapshot& focus)
    {
        const std::wstring previousInputBuffer = _state.CurrentInputBuffer();

        const bool shouldEmitTrainerInternalBack =
            keyEvent.IsKeyDown &&
            keyEvent.VirtualKey == VK_BACK &&
            focus.IsGdqLike() &&
            response.Handled &&
            !previousInputBuffer.empty();

        _state.UpdateCoreResponse(response);

        std::wstringstream responseStream;
        responseStream << L"response handled=" << (response.Handled ? L"true" : L"false")
                       << L" commit_len=" << response.CommitText.size()
                       << L" input_len=" << response.InputBuffer.size()
                       << L" keyboard_open=" << (response.KeyboardOpen ? L"true" : L"false")
                       << L" cancel=" << (response.CancelComposition ? L"true" : L"false");
        Logger::Info(L"resp", responseStream.str());

        if (response.EnsureSystemLayoutEn)
        {
            RequestEnsureEnglishSystemLayout();
        }

        const bool enteredComposing = previousInputBuffer.empty() && !response.InputBuffer.empty();
        const bool continuedComposingAfterCommit =
            !response.CommitText.empty() &&
            !response.InputBuffer.empty();
        const bool isCtrlOrSpaceKey =
            keyEvent.VirtualKey == VK_SPACE ||
            keyEvent.VirtualKey == VK_CONTROL ||
            keyEvent.VirtualKey == VK_LCONTROL ||
            keyEvent.VirtualKey == VK_RCONTROL;
        const bool shouldSuppressCtrlSpaceResidualCommit =
            response.Handled &&
            !response.CommitText.empty() &&
            isCtrlOrSpaceKey &&
            !previousInputBuffer.empty() &&
            response.InputBuffer.empty() &&
            !response.KeyboardOpen;
        const bool shouldPassThroughShiftKeyUpToggle =
            response.Handled &&
            keyEvent.IsKeyUp &&
            (keyEvent.VirtualKey == VK_SHIFT ||
             keyEvent.VirtualKey == VK_LSHIFT ||
             keyEvent.VirtualKey == VK_RSHIFT);

        if (keyEvent.IsKeyDown && (enteredComposing || continuedComposingAfterCommit) && _state.WantsCaretTracking())
        {
            InterlockedExchange(&_pendingCaretPublishMode, 1);
            PostThreadMessageW(_threadId, CaretPublishMessage, 0, 0);
        }

        if (response.Handled)
        {
            if (!response.CommitText.empty() && !shouldSuppressCtrlSpaceResidualCommit)
            {
                TryReplayCommitText(response.CommitText, focus, false);
            }

            if (shouldEmitTrainerInternalBack)
            {
                std::wstring trainerError;
                if (!_inputReplay.TrySendVirtualKey(0x7F, trainerError))
                {
                    Logger::Info(L"replay", std::wstring(L"F16 send failed: ") + trainerError);
                }
            }

            return !shouldPassThroughShiftKeyUpToggle;
        }

        if (keyEvent.IsKeyDown && keyEvent.VirtualKey == VK_BACK && focus.IsGdqLike())
        {
            std::wstring trainerError;
            if (!_inputReplay.TrySendVirtualKey(0x7E, trainerError))
            {
                Logger::Info(L"replay", std::wstring(L"F15 send failed: ") + trainerError);
            }
        }

        return false;
    }

    void HookRuntime::DropPendingKeys()
    {
        // Release only keys previously replayed as down, never replay old text
        // into a new foreground window.
        for (UINT vk = 0; vk < 256; ++vk)
        {
            if (!_replayedDown[vk]) continue;
            INPUT release = {};
            release.type = INPUT_KEYBOARD;
            release.ki.wVk = static_cast<WORD>(vk);
            release.ki.dwFlags = KEYEVENTF_KEYUP;
            release.ki.dwExtraInfo = InputReplay::ReplayMarker;
            SendInput(1, &release, sizeof(release));
            _replayedDown[vk] = false;
        }
        _pendingKeys.clear();
        _state.ResetCompositionState();
        _compositionCancelPending = true;
    }

    void HookRuntime::DrainPendingKeys()
    {
        if (!_pendingKeys.empty() && GetTickCount64() - _pendingKeys.front().Tick >= 5000)
            DropPendingKeys();
        std::wstring error;
        if (_compositionCancelPending)
        {
            if (!_pipeClient.TryCancelForRecovery(error)) return;
            _compositionCancelPending = false;
        }
        if (_state.DisabledByHotkey()) return;
        const ULONGLONG started = GetTickCount64();
        for (int count = 0; count < 8 && !_pendingKeys.empty() && GetTickCount64() - started < 60; ++count)
        {
            PendingKey pending = _pendingKeys.front();
            if (!pending.Focus.Equals(_state.GetFocus()) || GetForegroundWindow() != pending.Focus.Window)
            {
                DropPendingKeys();
                return;
            }
            CoreResponse response;
            if (!_pipeClient.TrySendPreparedKey(pending.Request, pending.Focus, response, error)) return;
            if (GetForegroundWindow() != pending.Focus.Window)
            {
                DropPendingKeys();
                return;
            }
            _pendingKeys.pop_front();
            if (pending.Event.IsKeyUp && pending.Event.VirtualKey < 256)
            {
                _replayedDown[pending.Event.VirtualKey] = false;
            }
            if (!ApplyKeyResponse(pending.Event, response, pending.Focus))
            {
                // This physical event was held earlier; only now is passing it safe.
                INPUT input = {};
                input.type = INPUT_KEYBOARD;
                input.ki.wVk = static_cast<WORD>(pending.Event.VirtualKey);
                input.ki.wScan = static_cast<WORD>(pending.Event.ScanCode);
                input.ki.dwFlags = (pending.Event.IsKeyUp ? KEYEVENTF_KEYUP : 0) |
                    (pending.Event.IsExtended ? KEYEVENTF_EXTENDEDKEY : 0);
                input.ki.dwExtraInfo = InputReplay::ReplayMarker;
                if (SendInput(1, &input, sizeof(input)) != 1)
                {
                    DropPendingKeys();
                    return;
                }
                if (pending.Event.VirtualKey < 256)
                    _replayedDown[pending.Event.VirtualKey] = pending.Event.IsKeyDown;
            }
        }
    }
}
