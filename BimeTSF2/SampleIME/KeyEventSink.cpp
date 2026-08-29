// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"
#include "SampleIME.h"
#include "Compartment.h"
#include "PipeClient.h"

static volatile LONG s_pendingCapsCompensate = 0;
static volatile LONG s_capsCompensateWorkerRunning = 0;
static const ULONGLONG kFailedKeyQueueTtlMs = 5000;
static const size_t kFailedKeyQueueMaxSize = 128;
static const DWORD kPipeKeyResponseTimeoutMs = 60;
static const UINT kKeyUpForwardBudgetMax = 20;

static LONG GetPendingCapsCompensateFlag()
{
    return InterlockedCompareExchange(&s_pendingCapsCompensate, 0, 0);
}

static LONG GetCapsCompensateWorkerFlag()
{
    return InterlockedCompareExchange(&s_capsCompensateWorkerRunning, 0, 0);
}

static BOOL GetSystemCapsLockOn()
{
    return (GetKeyState(VK_CAPITAL) & 0x0001) ? TRUE : FALSE;
}

static BOOL ShouldLogCapsDebug(UINT vkCode)
{
    if (vkCode == VK_CAPITAL)
    {
        return TRUE;
    }

    if (GetPendingCapsCompensateFlag() != 0)
    {
        return TRUE;
    }

    return (vkCode >= 'A' && vkCode <= 'Z') ? TRUE : FALSE;
}

static void LogCapsDebugKey(const char *stage,
                            WPARAM wParam,
                            LPARAM lParam,
                            BOOL isKeyDown,
                            UINT vkCode,
                            UINT scanCode,
                            UINT repeat,
                            BOOL extended,
                            BOOL shift,
                            BOOL ctrl,
                            BOOL alt,
                            BOOL win,
                            BOOL capsLock,
                            BOOL numLock)
{
    if (!ShouldLogCapsDebug(vkCode))
    {
        return;
    }

    Global::LogToFileVerbose(
        "CapsDbg %s tid=%lu msg=%s wParam=%llu vk=%u scan=%u rep=%u ext=%d "
        "mod[s=%d c=%d a=%d w=%d caps=%d num=%d] pending=%ld worker=%ld sysCaps=%d lParam=0x%llX",
        stage ? stage : "<null>",
        static_cast<unsigned long>(GetCurrentThreadId()),
        isKeyDown ? "down" : "up",
        static_cast<unsigned long long>(wParam),
        vkCode,
        scanCode,
        repeat,
        extended,
        shift,
        ctrl,
        alt,
        win,
        capsLock,
        numLock,
        GetPendingCapsCompensateFlag(),
        GetCapsCompensateWorkerFlag(),
        GetSystemCapsLockOn(),
        static_cast<unsigned long long>(lParam));
}

static void LogCapsDebugResponse(const char *stage, UINT vkCode, const BimeResponse &response, BOOL isEaten)
{
    if (!ShouldLogCapsDebug(vkCode))
    {
        return;
    }

    Global::LogToFileVerbose(
        "CapsDbg %s tid=%lu vk=%u rsp[success=%d handled=%d eaten=%d text_len=%u input_len=%u kb_open=%d/%d] "
        "pending=%ld worker=%ld sysCaps=%d",
        stage ? stage : "<null>",
        static_cast<unsigned long>(GetCurrentThreadId()),
        vkCode,
        response.success ? 1 : 0,
        response.handled ? 1 : 0,
        isEaten ? 1 : 0,
        static_cast<unsigned>(response.textToOutput.length()),
        static_cast<unsigned>(response.inputBuffer.length()),
        response.hasKeyboardOpen ? 1 : 0,
        response.keyboardOpen ? 1 : 0,
        GetPendingCapsCompensateFlag(),
        GetCapsCompensateWorkerFlag(),
        GetSystemCapsLockOn());
}


static BOOL TryBuildPipeKeyEvent(WPARAM wParam,
                                 LPARAM lParam,
                                 BOOL isKeyDown,
                                 _Out_ UINT *pVkCode,
                                 _Out_ UINT *pScanCode,
                                 _Out_ BOOL *pShift,
                                 _Out_ BOOL *pCtrl,
                                 _Out_ BOOL *pAlt,
                                 _Out_ BOOL *pWin,
                                 _Out_ BOOL *pCapsLock,
                                 _Out_ BOOL *pNumLock,
                                 _Out_ UINT *pRepeat,
                                 _Out_ BOOL *pExtended,
                                 _Out_ BOOL *pCaretValid,
                                 _Out_ LONG *pCaretX,
                                 _Out_ LONG *pCaretY)
{
    UNREFERENCED_PARAMETER(isKeyDown);
    UNREFERENCED_PARAMETER(pCaretX);
    UNREFERENCED_PARAMETER(pCaretY);

    UINT vkCode = static_cast<UINT>(wParam);
    if (vkCode == VK_PACKET)
    {
        return FALSE;
    }

    if (pVkCode)
    {
        *pVkCode = vkCode;
    }

    if (pScanCode)
    {
        *pScanCode = static_cast<UINT>((lParam >> 16) & 0xFF);
    }

    if (pShift)
    {
        *pShift = (GetKeyState(VK_SHIFT) & 0x8000) ? TRUE : FALSE;
    }

    if (pCtrl)
    {
        *pCtrl = (GetKeyState(VK_CONTROL) & 0x8000) ? TRUE : FALSE;
    }

    if (pAlt)
    {
        *pAlt = (GetKeyState(VK_MENU) & 0x8000) ? TRUE : FALSE;
    }

    if (pWin)
    {
        *pWin = ((GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000)) ? TRUE : FALSE;
    }

    if (pCapsLock)
    {
        BOOL capsLock = (GetKeyState(VK_CAPITAL) & 0x0001) ? TRUE : FALSE;
        BOOL capsLockOriginal = capsLock;
        if (InterlockedCompareExchange(&s_pendingCapsCompensate, 0, 0) != 0)
        {
            capsLock = FALSE;
            if (vkCode == VK_CAPITAL || (vkCode >= 'A' && vkCode <= 'Z'))
            {
                Global::LogToFileVerbose(
                    "CapsDbg TryBuildPipeKeyEvent override_caps vk=%u original=%d forced=%d pending=%ld sysCaps=%d",
                    vkCode,
                    capsLockOriginal ? 1 : 0,
                    capsLock ? 1 : 0,
                    GetPendingCapsCompensateFlag(),
                    GetSystemCapsLockOn() ? 1 : 0);
            }
        }
        *pCapsLock = capsLock;
    }

    if (pNumLock)
    {
        *pNumLock = (GetKeyState(VK_NUMLOCK) & 0x0001) ? TRUE : FALSE;
    }

    if (pRepeat)
    {
        UINT repeat = static_cast<UINT>(lParam & 0xFFFF);
        *pRepeat = (repeat == 0) ? 1u : repeat;
    }

    if (pExtended)
    {
        *pExtended = ((lParam & 0x01000000) != 0) ? TRUE : FALSE;
    }

    if (pCaretValid)
    {
        *pCaretValid = FALSE;
    }

    return TRUE;
}

static void LogKeyDecision(const char *stage, UINT vkCode, const char *decision)
{
    Global::LogToFileVerbose("KeySink %s vk=%u %s", stage, vkCode, decision);
}

static void LogForegroundWindowInfo(const char *stage)
{
    if (!Global::IsVerboseLoggingEnabledRuntime())
    {
        return;
    }

    HWND hwnd = GetForegroundWindow();
    DWORD pid = 0;
    DWORD tid = 0;
    char className[128] = {0};
    char windowTitle[260] = {0};

    if (hwnd != nullptr)
    {
        tid = GetWindowThreadProcessId(hwnd, &pid);
        GetClassNameA(hwnd, className, ARRAYSIZE(className));
        GetWindowTextA(hwnd, windowTitle, ARRAYSIZE(windowTitle));
    }

    Global::LogToFileVerbose("KeySink %s fg_hwnd=0x%p tid=%lu pid=%lu class=%s title=%s",
                             stage ? stage : "fg",
                             hwnd,
                             static_cast<unsigned long>(tid),
                             static_cast<unsigned long>(pid),
                             className[0] ? className : "<none>",
                             windowTitle[0] ? windowTitle : "<none>");
}

static void LogActiveProfileState(const char *stage)
{
    if (!Global::IsVerboseLoggingEnabledRuntime())
    {
        return;
    }

    ITfInputProcessorProfileMgr *pProfileMgr = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
                                  nullptr,
                                  CLSCTX_INPROC_SERVER,
                                  IID_ITfInputProcessorProfileMgr,
                                  (void **)&pProfileMgr);
    if (FAILED(hr) || pProfileMgr == nullptr)
    {
        Global::LogToFileVerbose("KeySink %s active_profile query_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        return;
    }

    TF_INPUTPROCESSORPROFILE profile = {};
    hr = pProfileMgr->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("KeySink %s active_profile get_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        pProfileMgr->Release();
        return;
    }

    wchar_t clsidW[64] = {0};
    wchar_t profileW[64] = {0};
    char clsidA[128] = {0};
    char profileA[128] = {0};

    if (StringFromGUID2(profile.clsid, clsidW, ARRAYSIZE(clsidW)) > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, clsidW, -1, clsidA, ARRAYSIZE(clsidA), nullptr, nullptr);
    }
    if (StringFromGUID2(profile.guidProfile, profileW, ARRAYSIZE(profileW)) > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, profileW, -1, profileA, ARRAYSIZE(profileA), nullptr, nullptr);
    }

    const BOOL isOurs = (IsEqualCLSID(profile.clsid, Global::SampleIMECLSID) && IsEqualGUID(profile.guidProfile, Global::SampleIMEGuidProfile)) ? TRUE : FALSE;
    Global::LogToFileVerbose("KeySink %s active_profile lang=0x%04X clsid=%s profile=%s flags=0x%08X is_ours=%d",
                             stage ? stage : "profile",
                             static_cast<unsigned>(profile.langid),
                             clsidA[0] ? clsidA : "<none>",
                             profileA[0] ? profileA : "<none>",
                             static_cast<unsigned>(profile.dwFlags),
                             isOurs);

    pProfileMgr->Release();
}

static void ToggleCapsLockOnce()
{
    INPUT inputs[2] = {};
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].ki.wVk = VK_CAPITAL;
    inputs[1].type = INPUT_KEYBOARD;
    inputs[1].ki.wVk = VK_CAPITAL;
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

    UINT sent = SendInput(2, inputs, sizeof(INPUT));
    Global::LogToFileVerbose("KeySink CapsCompensate SendInput sent=%u", sent);
}

static void ArmCapsCompensationAsync()
{
    LONG oldPending = InterlockedCompareExchange(&s_pendingCapsCompensate, 0, 0);
    InterlockedExchange(&s_pendingCapsCompensate, 1);
    LONG oldWorker = InterlockedCompareExchange(&s_capsCompensateWorkerRunning, 0, 0);
    Global::LogToFileVerbose("CapsDbg Arm request tid=%lu oldPending=%ld oldWorker=%ld pending=%ld worker=%ld sysCaps=%d",
                             static_cast<unsigned long>(GetCurrentThreadId()),
                             oldPending,
                             oldWorker,
                             GetPendingCapsCompensateFlag(),
                             GetCapsCompensateWorkerFlag(),
                             GetSystemCapsLockOn() ? 1 : 0);
    Global::LogToFileVerbose("CapsDbg Arm deferred_to_keyup pending=%ld worker=%ld sysCaps=%d",
                             GetPendingCapsCompensateFlag(),
                             GetCapsCompensateWorkerFlag(),
                             GetSystemCapsLockOn() ? 1 : 0);
}

static void ResetCapsCompensationState(_In_opt_z_ const char *reason)
{
    LONG oldPending = InterlockedExchange(&s_pendingCapsCompensate, 0);
    LONG oldWorker = InterlockedExchange(&s_capsCompensateWorkerRunning, 0);
    if (oldPending != 0 || oldWorker != 0)
    {
        Global::LogToFileVerbose("CapsDbg Reset reason=%s oldPending=%ld oldWorker=%ld sysCaps=%d",
                                 reason ? reason : "<null>",
                                 oldPending,
                                 oldWorker,
                                 GetSystemCapsLockOn() ? 1 : 0);
    }
}

static BOOL IsProcessKey(UINT vkCode)
{
    return (vkCode == VK_PROCESSKEY) ? TRUE : FALSE;
}

static UINT ExtractScanCodeFromLParam(LPARAM lParam)
{
    return static_cast<UINT>((lParam >> 16) & 0xFF);
}

static BOOL ExtractExtendedFromLParam(LPARAM lParam)
{
    return ((lParam & 0x01000000) != 0) ? TRUE : FALSE;
}

static UINT ResolvePhysicalVirtualKey(UINT vkCode, UINT scanCode, BOOL extended)
{
    if (vkCode == VK_SHIFT)
    {
        if (scanCode == 0x2A)
        {
            return VK_LSHIFT;
        }
        if (scanCode == 0x36)
        {
            return VK_RSHIFT;
        }
    }
    else if (vkCode == VK_CONTROL)
    {
        return extended ? VK_RCONTROL : VK_LCONTROL;
    }
    else if (vkCode == VK_MENU)
    {
        return extended ? VK_RMENU : VK_LMENU;
    }

    return vkCode;
}

static BOOL IsKeyUpWindowTrigger(UINT resolvedVk)
{
    return resolvedVk == VK_SHIFT || resolvedVk == VK_LSHIFT || resolvedVk == VK_RSHIFT ||
           resolvedVk == VK_CONTROL || resolvedVk == VK_LCONTROL || resolvedVk == VK_RCONTROL ||
           resolvedVk == VK_MENU || resolvedVk == VK_LMENU || resolvedVk == VK_RMENU ||
           resolvedVk == VK_LWIN || resolvedVk == VK_RWIN || resolvedVk == VK_CAPITAL;
}

static BOOL IsAlwaysForwardKeyUp(UINT resolvedVk)
{
    return IsKeyUpWindowTrigger(resolvedVk) ||
           resolvedVk == VK_SPACE ||
           resolvedVk == VK_OEM_7;
}

static BOOL IsPendingKeyEventMatch(WPARAM pendingWParam,
                                   UINT pendingScanCode,
                                   BOOL pendingExtended,
                                   WPARAM currentWParam,
                                   LPARAM currentLParam)
{
    const UINT pendingVk = static_cast<UINT>(pendingWParam);
    const UINT currentVk = static_cast<UINT>(currentWParam);
    const UINT currentScanCode = ExtractScanCodeFromLParam(currentLParam);
    const BOOL currentExtended = ExtractExtendedFromLParam(currentLParam);

    if (pendingWParam == currentWParam &&
        pendingScanCode == currentScanCode &&
        pendingExtended == currentExtended)
    {
        return TRUE;
    }

    if (IsProcessKey(pendingVk) || IsProcessKey(currentVk))
    {
        if (pendingScanCode != 0 && currentScanCode != 0)
        {
            return (pendingScanCode == currentScanCode && pendingExtended == currentExtended) ? TRUE : FALSE;
        }

        return (pendingWParam == currentWParam) ? TRUE : FALSE;
    }

    return FALSE;
}

static void LogPendingCacheDecision(const char *stage,
                                    BOOL match,
                                    WPARAM pendingWParam,
                                    UINT pendingScanCode,
                                    BOOL pendingExtended,
                                    WPARAM currentWParam,
                                    LPARAM currentLParam)
{
    const UINT currentScanCode = ExtractScanCodeFromLParam(currentLParam);
    const BOOL currentExtended = ExtractExtendedFromLParam(currentLParam);
    Global::LogToFileVerbose(
        "KeySink %s match=%d pending[wParam=%llu scan=%u ext=%d] current[wParam=%llu scan=%u ext=%d lParam=0x%llX]",
        stage ? stage : "pending",
        match ? 1 : 0,
        static_cast<unsigned long long>(pendingWParam),
        pendingScanCode,
        pendingExtended ? 1 : 0,
        static_cast<unsigned long long>(currentWParam),
        currentScanCode,
        currentExtended ? 1 : 0,
        static_cast<unsigned long long>(currentLParam));
}

static BOOL ShouldForceEatKey(UINT vkCode, const BimeResponse &response)
{
    const BOOL isAlpha = (vkCode >= 'A' && vkCode <= 'Z') ? TRUE : FALSE;
    const BOOL isProcess = IsProcessKey(vkCode);
    if (!isAlpha && !isProcess)
    {
        return FALSE;
    }

    if (response.handled)
    {
        return FALSE;
    }

    // Defensive fallback: if core returned composition/output payload for alpha/process key,
    // do not allow raw text to pass through to the app.
    return (!response.textToOutput.empty() || !response.inputBuffer.empty()) ? TRUE : FALSE;
}

static BOOL IsImmersiveForegroundWindow(_In_opt_ HWND hwnd)
{
    if (hwnd == nullptr)
    {
        return FALSE;
    }

    WCHAR className[128] = {};
    if (GetClassNameW(hwnd, className, ARRAYSIZE(className)) <= 0)
    {
        return FALSE;
    }

    if (_wcsicmp(className, L"Windows.UI.Core.CoreWindow") == 0)
    {
        return TRUE;
    }

    // UWP desktop bridge / app frame host.
    if (_wcsicmp(className, L"ApplicationFrameWindow") == 0)
    {
        return TRUE;
    }

    return FALSE;
}


static BOOL IsShellTrayWindow(_In_opt_ HWND hwnd)
{
    if (hwnd == nullptr)
    {
        return FALSE;
    }

    WCHAR className[128] = {};
    if (GetClassNameW(hwnd, className, ARRAYSIZE(className)) <= 0)
    {
        return FALSE;
    }

    return (_wcsicmp(className, L"Shell_TrayWnd") == 0) ? TRUE : FALSE;
}

//+---------------------------------------------------------------------------
//
// _IsKeyEaten
//
//----------------------------------------------------------------------------

//+---------------------------------------------------------------------------
//
// _IsKeyboardDisabled
//
//----------------------------------------------------------------------------

BOOL CSampleIME::_IsKeyboardDisabled(_In_opt_ ITfContext *pContextHint)
{
    ITfDocumentMgr* pDocMgrFocus = nullptr;
    ITfContext* pResolvedContext = nullptr;
    BOOL releaseResolvedContext = FALSE;
    BOOL isDisabled = FALSE;
    BOOL contextKeyboardDisabled = FALSE;
    BOOL contextEmptyContext = FALSE;
    BOOL threadKeyboardDisabled = FALSE;
    BOOL threadEmptyContext = FALSE;
    BOOL usedContextCompartment = FALSE;
    BOOL disabledByKeyboard = FALSE;
    BOOL disabledByEmpty = FALSE;
    HRESULT hrGetFocus = E_FAIL;
    HRESULT hrGetTop = E_FAIL;
    HRESULT hrCtxKeyboard = E_FAIL;
    HRESULT hrCtxEmpty = E_FAIL;
    HRESULT hrThreadKeyboard = E_FAIL;
    HRESULT hrThreadEmpty = E_FAIL;
    HWND hwndForeground = GetForegroundWindow();
    const BOOL isImmersiveForeground = IsImmersiveForegroundWindow(hwndForeground);
    const BOOL treatAsImmersive = (_isImmersiveSession || isImmersiveForeground) ? TRUE : FALSE;

    if (_pThreadMgr == nullptr)
    {
        isDisabled = TRUE;
        goto Exit;
    }

    if (pContextHint != nullptr)
    {
        pResolvedContext = pContextHint;
        pResolvedContext->AddRef();
        releaseResolvedContext = TRUE;
        hrGetTop = S_OK;
    }
    else
    {
        hrGetFocus = _pThreadMgr->GetFocus(&pDocMgrFocus);
        if (SUCCEEDED(hrGetFocus) && (pDocMgrFocus != nullptr))
        {
            hrGetTop = pDocMgrFocus->GetTop(&pResolvedContext);
            if (SUCCEEDED(hrGetTop) && (pResolvedContext != nullptr))
            {
                releaseResolvedContext = TRUE;
            }
        }
    }

    if (pResolvedContext != nullptr)
    {
        CCompartment contextKeyboardDisabledCompartment(pResolvedContext, _tfClientId, GUID_COMPARTMENT_KEYBOARD_DISABLED);
        hrCtxKeyboard = contextKeyboardDisabledCompartment._GetCompartmentBOOL(contextKeyboardDisabled);

        CCompartment contextEmptyCompartment(pResolvedContext, _tfClientId, GUID_COMPARTMENT_EMPTYCONTEXT);
        hrCtxEmpty = contextEmptyCompartment._GetCompartmentBOOL(contextEmptyContext);

        if (SUCCEEDED(hrCtxKeyboard) || SUCCEEDED(hrCtxEmpty))
        {
            usedContextCompartment = TRUE;
            disabledByKeyboard = (SUCCEEDED(hrCtxKeyboard) && contextKeyboardDisabled) ? TRUE : FALSE;
            disabledByEmpty = (SUCCEEDED(hrCtxEmpty) && contextEmptyContext) ? TRUE : FALSE;
            isDisabled = (disabledByKeyboard || disabledByEmpty) ? TRUE : FALSE;
        }
    }

    if (!usedContextCompartment)
    {
        CCompartment threadKeyboardDisabledCompartment(_pThreadMgr, _tfClientId, GUID_COMPARTMENT_KEYBOARD_DISABLED);
        hrThreadKeyboard = threadKeyboardDisabledCompartment._GetCompartmentBOOL(threadKeyboardDisabled);

        CCompartment threadEmptyCompartment(_pThreadMgr, _tfClientId, GUID_COMPARTMENT_EMPTYCONTEXT);
        hrThreadEmpty = threadEmptyCompartment._GetCompartmentBOOL(threadEmptyContext);

        if (SUCCEEDED(hrThreadKeyboard) || SUCCEEDED(hrThreadEmpty))
        {
            disabledByKeyboard = (SUCCEEDED(hrThreadKeyboard) && threadKeyboardDisabled) ? TRUE : FALSE;
            disabledByEmpty = (SUCCEEDED(hrThreadEmpty) && threadEmptyContext) ? TRUE : FALSE;
            isDisabled = (disabledByKeyboard || disabledByEmpty) ? TRUE : FALSE;
        }
        else if (pResolvedContext == nullptr)
        {
            isDisabled = treatAsImmersive ? FALSE : TRUE;
        }
    }

    if (treatAsImmersive && !disabledByKeyboard && disabledByEmpty)
    {
        // In immersive/UWP contexts, EMPTYCONTEXT may transiently be true for editable controls.
        // Do not block key forwarding solely because of EMPTYCONTEXT.
        isDisabled = FALSE;
    }

Exit:
    Global::LogToFileVerbose("KeySink _IsKeyboardDisabled disabled=%d hr_focus=0x%08X hr_top=0x%08X hint_ctx=%p ctx=%p use_ctx=%d by_kb=%d by_empty=%d ctx_kb=%d ctx_empty=%d hr_ctx_kb=0x%08X hr_ctx_empty=0x%08X tm_kb=%d tm_empty=%d hr_tm_kb=0x%08X hr_tm_empty=0x%08X docmgr=%p immersive=%d immersive_fg=%d",
                             isDisabled,
                             static_cast<unsigned>(hrGetFocus),
                             static_cast<unsigned>(hrGetTop),
                             pContextHint,
                             pResolvedContext,
                             usedContextCompartment,
                             disabledByKeyboard,
                             disabledByEmpty,
                             contextKeyboardDisabled,
                             contextEmptyContext,
                             static_cast<unsigned>(hrCtxKeyboard),
                             static_cast<unsigned>(hrCtxEmpty),
                             threadKeyboardDisabled,
                             threadEmptyContext,
                             static_cast<unsigned>(hrThreadKeyboard),
                             static_cast<unsigned>(hrThreadEmpty),
                             pDocMgrFocus,
                             _isImmersiveSession,
                             isImmersiveForeground);

    if (releaseResolvedContext && pResolvedContext)
    {
        pResolvedContext->Release();
    }

    if (pDocMgrFocus)
    {
        pDocMgrFocus->Release();
    }

    return isDisabled;
}

void CSampleIME::_ClearPendingResponseCache()
{
    _pendingResponseValid = FALSE;
    _pendingResponseIsKeyDown = FALSE;
    _pendingResponseWParam = 0;
    _pendingResponseScanCode = 0;
    _pendingResponseExtended = FALSE;
    _pendingResponseHandled = FALSE;
    _pendingResponseExpectKeyUp = TRUE;
    _pendingResponseHasKeyboardOpen = FALSE;
    _pendingResponseKeyboardOpen = FALSE;
    _pendingResponseCancelComposition = FALSE;
    _pendingResponseCompositionTracking = FALSE;
    _pendingResponseCompositionPending = FALSE;
    _pendingResponseTextToOutput.clear();
    _pendingResponseInputBuffer.clear();
}

void CSampleIME::_StorePendingResponseCache(BOOL isKeyDown, WPARAM wParam, UINT scanCode, BOOL extended, _In_ const BimeResponse &response)
{
    _pendingResponseValid = TRUE;
    _pendingResponseIsKeyDown = isKeyDown;
    _pendingResponseWParam = wParam;
    _pendingResponseScanCode = scanCode;
    _pendingResponseExtended = extended;
    _pendingResponseHandled = response.handled;
    _pendingResponseExpectKeyUp = response.expectKeyUp;
    _pendingResponseHasKeyboardOpen = response.hasKeyboardOpen;
    _pendingResponseKeyboardOpen = response.keyboardOpen;
    _pendingResponseCancelComposition = response.cancelComposition;
    _pendingResponseCompositionTracking = response.compositionTracking;
    _pendingResponseCompositionPending = response.compositionPending;
    _pendingResponseTextToOutput = response.textToOutput;
    _pendingResponseInputBuffer = response.inputBuffer;
    Global::LogToFileVerbose("KeySink pending_store msg=%s wParam=%llu scan=%u ext=%d handled=%d text_len=%u input_len=%u",
                             isKeyDown ? "down" : "up",
                             static_cast<unsigned long long>(wParam),
                             scanCode,
                             extended ? 1 : 0,
                             response.handled ? 1 : 0,
                             static_cast<unsigned>(_pendingResponseTextToOutput.length()),
                             static_cast<unsigned>(_pendingResponseInputBuffer.length()));
}

void CSampleIME::_ClearPendingKeyEvent()
{
    _pendingKeyEventValid = FALSE;
    _pendingKeyEventIsKeyDown = FALSE;
    _pendingKeyEventWParam = 0;
    _pendingKeyEventScanCode = 0;
    _pendingKeyEventExtended = FALSE;
    _pendingKeyEventId = 0;
}

void CSampleIME::_StorePendingKeyEvent(BOOL isKeyDown, WPARAM wParam, UINT scanCode, BOOL extended, ULONGLONG eventId)
{
    _pendingKeyEventValid = eventId != 0;
    _pendingKeyEventIsKeyDown = isKeyDown;
    _pendingKeyEventWParam = wParam;
    _pendingKeyEventScanCode = scanCode;
    _pendingKeyEventExtended = extended;
    _pendingKeyEventId = eventId;
}

BOOL CSampleIME::_TryConsumePendingKeyEvent(BOOL isKeyDown, WPARAM wParam, LPARAM lParam, _Out_ ULONGLONG *pEventId)
{
    if (pEventId == nullptr || !_pendingKeyEventValid || _pendingKeyEventIsKeyDown != isKeyDown)
    {
        return FALSE;
    }

    if (!IsPendingKeyEventMatch(_pendingKeyEventWParam,
                                _pendingKeyEventScanCode,
                                _pendingKeyEventExtended,
                                wParam,
                                lParam))
    {
        return FALSE;
    }

    *pEventId = _pendingKeyEventId;
    _ClearPendingKeyEvent();
    return TRUE;
}

BOOL CSampleIME::_TryConsumePendingResponseCache(BOOL isKeyDown, WPARAM wParam, LPARAM lParam, _Out_ BimeResponse *pResponse)
{
    if (pResponse == nullptr)
    {
        return FALSE;
    }

    if (!_pendingResponseValid)
    {
        Global::LogToFileVerbose("KeySink pending_consume msg=%s miss=cache_invalid current[wParam=%llu scan=%u ext=%d lParam=0x%llX]",
                                 isKeyDown ? "down" : "up",
                                 static_cast<unsigned long long>(wParam),
                                 ExtractScanCodeFromLParam(lParam),
                                 ExtractExtendedFromLParam(lParam) ? 1 : 0,
                                 static_cast<unsigned long long>(lParam));
        return FALSE;
    }

    if (_pendingResponseIsKeyDown != isKeyDown)
    {
        Global::LogToFileVerbose("KeySink pending_consume msg=%s miss=direction_mismatch pending_msg=%s current[wParam=%llu scan=%u ext=%d lParam=0x%llX]",
                                 isKeyDown ? "down" : "up",
                                 _pendingResponseIsKeyDown ? "down" : "up",
                                 static_cast<unsigned long long>(wParam),
                                 ExtractScanCodeFromLParam(lParam),
                                 ExtractExtendedFromLParam(lParam) ? 1 : 0,
                                 static_cast<unsigned long long>(lParam));
        return FALSE;
    }

    const BOOL match = IsPendingKeyEventMatch(_pendingResponseWParam, _pendingResponseScanCode, _pendingResponseExtended, wParam, lParam);
    LogPendingCacheDecision("pending_consume", match, _pendingResponseWParam, _pendingResponseScanCode, _pendingResponseExtended, wParam, lParam);
    if (!match)
    {
        return FALSE;
    }

    pResponse->success = TRUE;
    pResponse->handled = _pendingResponseHandled;
    pResponse->expectKeyUp = _pendingResponseExpectKeyUp;
    pResponse->hasKeyboardOpen = _pendingResponseHasKeyboardOpen;
    pResponse->keyboardOpen = _pendingResponseKeyboardOpen;
    pResponse->cancelComposition = _pendingResponseCancelComposition;
    pResponse->compositionTracking = _pendingResponseCompositionTracking;
    pResponse->compositionPending = _pendingResponseCompositionPending;
    pResponse->textToOutput = _pendingResponseTextToOutput;
    pResponse->inputBuffer = _pendingResponseInputBuffer;

    Global::LogToFileVerbose("KeySink pending_consume hit msg=%s handled=%d text_len=%u input_len=%u",
                             isKeyDown ? "down" : "up",
                             pResponse->handled ? 1 : 0,
                             static_cast<unsigned>(pResponse->textToOutput.length()),
                             static_cast<unsigned>(pResponse->inputBuffer.length()));

    _ClearPendingResponseCache();
    _ClearPendingKeyEvent();
    return TRUE;
}

void CSampleIME::_RefreshKeyUpWindowForKeyDown(UINT vkCode, UINT scanCode, BOOL extended, _In_ const BimeResponse &response)
{
    UINT resolvedVk = ResolvePhysicalVirtualKey(vkCode, scanCode, extended);
    if (IsKeyUpWindowTrigger(resolvedVk) ||
        (response.expectKeyUp && resolvedVk != VK_SPACE && resolvedVk != VK_OEM_7))
    {
        _keyUpForwardBudget = kKeyUpForwardBudgetMax;
    }
}

void CSampleIME::_RefreshKeyUpWindowForFailedKeyDown(UINT vkCode, UINT scanCode, BOOL extended)
{
    UINT resolvedVk = ResolvePhysicalVirtualKey(vkCode, scanCode, extended);
    if (IsKeyUpWindowTrigger(resolvedVk))
    {
        _keyUpForwardBudget = kKeyUpForwardBudgetMax;
    }
}

BOOL CSampleIME::_ShouldForwardKeyUp(WPARAM wParam, LPARAM lParam) const
{
    UINT resolvedVk = ResolvePhysicalVirtualKey(static_cast<UINT>(wParam),
                                                ExtractScanCodeFromLParam(lParam),
                                                ExtractExtendedFromLParam(lParam));
    return IsAlwaysForwardKeyUp(resolvedVk) || _keyUpForwardBudget > 0;
}

void CSampleIME::_AdvanceKeyUpWindow(WPARAM wParam, LPARAM lParam)
{
    UINT resolvedVk = ResolvePhysicalVirtualKey(static_cast<UINT>(wParam),
                                                ExtractScanCodeFromLParam(lParam),
                                                ExtractExtendedFromLParam(lParam));
    if (IsKeyUpWindowTrigger(resolvedVk))
    {
        _keyUpForwardBudget = kKeyUpForwardBudgetMax;
    }
    else if (_keyUpForwardBudget > 0)
    {
        --_keyUpForwardBudget;
    }
}

void CSampleIME::_PruneFailedKeyQueue(ULONGLONG nowTick)
{
    while (!_failedKeyQueue.empty())
    {
        const FailedKeyMessage &front = _failedKeyQueue.front();
        ULONGLONG age = (nowTick >= front.tick) ? (nowTick - front.tick) : 0;
        if (age <= kFailedKeyQueueTtlMs)
        {
            break;
        }

        Global::LogToFileVerbose("KeyFailQueue: drop expired vk=%u age=%llu", front.vkCode, age);
        _failedKeyQueue.pop_front();
    }

    while (_failedKeyQueue.size() > kFailedKeyQueueMaxSize)
    {
        const FailedKeyMessage &front = _failedKeyQueue.front();
        Global::LogToFileVerbose("KeyFailQueue: drop overflow vk=%u size=%u", front.vkCode, static_cast<unsigned>(_failedKeyQueue.size()));
        _failedKeyQueue.pop_front();
    }
}

void CSampleIME::_EnqueueFailedKeyMessage(UINT vkCode,
                                          UINT scanCode,
                                          BOOL isKeyDown,
                                          BOOL shift,
                                          BOOL ctrl,
                                          BOOL alt,
                                          BOOL win,
                                          BOOL capsLock,
                                          BOOL numLock,
                                          UINT repeat,
                                          BOOL extended,
                                          BOOL caretValid,
                                          LONG caretX,
                                          LONG caretY,
                                          _In_z_ const char *reason,
                                          ULONGLONG eventId)
{
    ULONGLONG nowTick = GetTickCount64();
    _PruneFailedKeyQueue(nowTick);

    FailedKeyMessage message = {};
    message.tick = nowTick;
    message.eventId = eventId != 0 ? eventId : _pPipeClient->NextKeyEventId();
    message.vkCode = vkCode;
    message.scanCode = scanCode;
    message.isKeyDown = isKeyDown;
    message.shift = shift;
    message.ctrl = ctrl;
    message.alt = alt;
    message.win = win;
    message.capsLock = capsLock;
    message.numLock = numLock;
    message.repeat = repeat;
    message.extended = extended;
    message.caretValid = caretValid;
    message.caretX = caretX;
    message.caretY = caretY;
    _failedKeyQueue.push_back(message);

    _PruneFailedKeyQueue(nowTick);

    Global::LogToFileVerbose("KeyFailQueue: enqueue size=%u vk=%u action=%s reason=%s",
                             static_cast<unsigned>(_failedKeyQueue.size()),
                             vkCode,
                             isKeyDown ? "down" : "up",
                             (reason != nullptr) ? reason : "<none>");
}

BOOL CSampleIME::_FlushFailedKeyQueue(_In_opt_ ITfContext *pContext, _In_z_ const char *stageTag)
{
    ULONGLONG nowTick = GetTickCount64();
    _PruneFailedKeyQueue(nowTick);
    if (_failedKeyQueue.empty())
    {
        return TRUE;
    }

    if (!_EnsurePipeConnected())
    {
        Global::LogToFileVerbose("KeyFailQueue: flush skipped disconnected size=%u", static_cast<unsigned>(_failedKeyQueue.size()));
        return FALSE;
    }

    while (!_failedKeyQueue.empty())
    {
        const FailedKeyMessage &message = _failedKeyQueue.front();
        ULONGLONG age = (nowTick >= message.tick) ? (nowTick - message.tick) : 0;
        if (age > kFailedKeyQueueTtlMs)
        {
            Global::LogToFileVerbose("KeyFailQueue: flush drop expired vk=%u age=%llu", message.vkCode, age);
            _failedKeyQueue.pop_front();
            continue;
        }

        BimeResponse response;
        HRESULT hr = _pPipeClient->SendKeyAndWait(message.vkCode,
                                                  message.scanCode,
                                                  message.isKeyDown,
                                                  "flush_replay",
                                                  message.shift,
                                                  message.ctrl,
                                                  message.alt,
                                                  message.win,
                                                  message.capsLock,
                                                  message.numLock,
                                                  message.repeat,
                                                  message.extended,
                                                  message.caretValid,
                                                  message.caretX,
                                                  message.caretY,
                                                  &response,
                                                  kPipeKeyResponseTimeoutMs,
                                                  message.eventId);
        if (FAILED(hr))
        {
            Global::LogToFile("KeyFailQueue: flush failed hr=0x%08X vk=%u action=%s size=%u",
                              static_cast<unsigned>(hr),
                              message.vkCode,
                              message.isKeyDown ? "down" : "up",
                              static_cast<unsigned>(_failedKeyQueue.size()));
            return FALSE;
        }

        BOOL committedViaAnchor = FALSE;
        _ApplyResponseAndSyncState(pContext, &response, stageTag, &committedViaAnchor);
        if (message.isKeyDown)
        {
            _RefreshKeyUpWindowForKeyDown(message.vkCode,
                                          message.scanCode,
                                          message.extended,
                                          response);
        }
        Global::LogToFileVerbose("KeyFailQueue: flushed vk=%u action=%s handled=%d commit_anchor=%d",
                                 message.vkCode,
                                 message.isKeyDown ? "down" : "up",
                                 response.handled,
                                 committedViaAnchor);
        _failedKeyQueue.pop_front();
        nowTick = GetTickCount64();
    }

    return TRUE;
}

BOOL CSampleIME::_ApplyResponseAndSyncState(_In_opt_ ITfContext *pContext, _Inout_ BimeResponse *pResponse, _In_z_ const char *stageTag, _Out_opt_ BOOL *pCommittedViaAnchor)
{
    if (pResponse == nullptr)
    {
        if (pCommittedViaAnchor != nullptr)
        {
            *pCommittedViaAnchor = FALSE;
        }
        return FALSE;
    }

    BOOL committedViaAnchor = _SyncCaretAnchorForResponse(pContext, pResponse);

    if (pResponse->compositionTracking)
    {
        _ScheduleCompositionRefresh(pResponse->compositionPending);
    }
    else
    {
        _CancelCompositionRefresh();
    }

    if (pResponse->hasKeyboardOpen)
    {
        _SyncKeyboardOpenCompartment(pResponse->keyboardOpen);
        Global::LogToFileVerbose("CompartmentSync %s keyboard_open=%d",
                                 (stageTag != nullptr) ? stageTag : "<unknown>",
                                 pResponse->keyboardOpen);
    }

    if (pCommittedViaAnchor != nullptr)
    {
        *pCommittedViaAnchor = committedViaAnchor;
    }
    return committedViaAnchor;
}

STDAPI CSampleIME::OnSetFocus(BOOL fForeground)
{
    Global::LogToFileVerbose("KeySink OnSetFocus foreground=%d", fForeground);
    ResetCapsCompensationState(fForeground ? "OnSetFocus.foreground" : "OnSetFocus.background");

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfKeyEventSink::OnTestKeyDown
//
// Called by the system to query this service wants a potential keystroke.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnTestKeyDown(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten)
{
    pContext;

    if (pIsEaten == nullptr)
    {
        return E_INVALIDARG;
    }

    Global::UpdateModifiers(wParam, lParam);

    UINT vkCode = static_cast<UINT>(wParam);

    if (_pendingResponseValid &&
        _pendingResponseIsKeyDown &&
        IsPendingKeyEventMatch(_pendingResponseWParam, _pendingResponseScanCode, _pendingResponseExtended, wParam, lParam))
    {
        BimeResponse cachedResponse;
        cachedResponse.success = TRUE;
        cachedResponse.handled = _pendingResponseHandled;
        cachedResponse.expectKeyUp = _pendingResponseExpectKeyUp;
        cachedResponse.hasKeyboardOpen = _pendingResponseHasKeyboardOpen;
        cachedResponse.keyboardOpen = _pendingResponseKeyboardOpen;
        cachedResponse.cancelComposition = _pendingResponseCancelComposition;
        cachedResponse.compositionTracking = _pendingResponseCompositionTracking;
        cachedResponse.compositionPending = _pendingResponseCompositionPending;
        cachedResponse.textToOutput = _pendingResponseTextToOutput;
        cachedResponse.inputBuffer = _pendingResponseInputBuffer;

        BOOL forceEatCached = ShouldForceEatKey(vkCode, cachedResponse);
        *pIsEaten = cachedResponse.handled || forceEatCached;
        if (cachedResponse.hasKeyboardOpen)
        {
            _SyncKeyboardOpenCompartment(cachedResponse.keyboardOpen);
            Global::LogToFileVerbose("CompartmentSync OnTestKeyDown keyboard_open=%d cache=1", cachedResponse.keyboardOpen);
        }
        Global::LogToFileVerbose("KeySink OnTestKeyDown vk=%u handled=%d cache_reuse=1",
                                 vkCode,
                                 *pIsEaten);
        return S_OK;
    }

    _ClearPendingResponseCache();
    _ClearPendingKeyEvent();

    if (_trialExpired)
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyDown", vkCode, "skip=trial_expired");
        return S_OK;
    }


    if (_IsStartupGuardActive())
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyDown", vkCode, "skip=startup_guard");
        return S_OK;
    }

    if (_IsKeyboardDisabled(pContext))
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyDown", vkCode, "skip=keyboard_disabled");
        LogForegroundWindowInfo("OnTestKeyDown.keyboard_disabled");
        LogActiveProfileState("OnTestKeyDown.keyboard_disabled");
        return S_OK;
    }

    if (!_EnsurePipeConnected())
    {
        _RefreshKeyUpWindowForFailedKeyDown(vkCode,
                                            ExtractScanCodeFromLParam(lParam),
                                            ExtractExtendedFromLParam(lParam));
        *pIsEaten = TRUE;
        LogKeyDecision("OnTestKeyDown", vkCode, "hold=pipe_disconnected");
        return S_OK;
    }

    UINT scanCode = 0;
    BOOL shift = FALSE;
    BOOL ctrl = FALSE;
    BOOL alt = FALSE;
    BOOL win = FALSE;
    BOOL capsLock = FALSE;
    BOOL numLock = FALSE;
    UINT repeat = 1;
    BOOL extended = FALSE;
    BOOL caretValid = FALSE;
    LONG caretX = 0;
    LONG caretY = 0;

    if (!TryBuildPipeKeyEvent(wParam, lParam, TRUE, &vkCode, &scanCode, &shift, &ctrl, &alt, &win, &capsLock, &numLock, &repeat, &extended, &caretValid, &caretX, &caretY))
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyDown", static_cast<UINT>(wParam), "skip=build_event_failed");
        return S_OK;
    }
    LogCapsDebugKey("OnTestKeyDown.built", wParam, lParam, TRUE, vkCode, scanCode, repeat, extended, shift, ctrl, alt, win, capsLock, numLock);
    ULONGLONG eventId = _pPipeClient->NextKeyEventId();
    _StorePendingKeyEvent(TRUE, wParam, scanCode, extended, eventId);
    if (!_FlushFailedKeyQueue(pContext, "OnTestKeyDown.prepend"))
    {
        _RefreshKeyUpWindowForFailedKeyDown(vkCode, scanCode, extended);
        *pIsEaten = TRUE;
        Global::LogToFile("KeySink OnTestKeyDown vk=%u defer_current=1 reason=flush_failed", vkCode);
        return S_OK;
    }

    BimeResponse response;
    HRESULT hr = _pPipeClient->SendKeyAndWait(vkCode,
                                              scanCode,
                                              TRUE,
                                              "test_keydown",
                                              shift,
                                              ctrl,
                                              alt,
                                              win,
                                              capsLock,
                                              numLock,
                                              repeat,
                                              extended,
                                              caretValid,
                                              caretX,
                                              caretY,
                                              &response,
                                              kPipeKeyResponseTimeoutMs,
                                              eventId);
    if (FAILED(hr))
    {
        _RefreshKeyUpWindowForFailedKeyDown(vkCode, scanCode, extended);
        *pIsEaten = TRUE;
        Global::LogToFile("KeySink OnTestKeyDown vk=%u pipe_error hr=0x%08X retry_in_keydown=1", vkCode, static_cast<unsigned>(hr));
        return S_OK;
    }

    _StorePendingResponseCache(TRUE, wParam, scanCode, extended, response);
    _RefreshKeyUpWindowForKeyDown(vkCode, scanCode, extended, response);

    BOOL forceEat = ShouldForceEatKey(vkCode, response);
    *pIsEaten = response.handled || forceEat;
    Global::LogToFileVerbose("KeySink OnTestKeyDown vk=%u handled=%d text_len=%u input_len=%u",
                             vkCode,
                             *pIsEaten,
                             static_cast<unsigned>(response.textToOutput.length()),
                             static_cast<unsigned>(response.inputBuffer.length()));
    if (forceEat)
    {
        Global::LogToFileVerbose("KeySink OnTestKeyDown vk=%u force_eat=1", vkCode);
    }
    BOOL committedViaAnchor = FALSE;
    _ApplyResponseAndSyncState(pContext, &response, "OnTestKeyDown", &committedViaAnchor);
    Global::LogToFileVerbose("KeySink OnTestKeyDown apply vk=%u commit_anchor=%d", vkCode, committedViaAnchor);

    if (vkCode == VK_CAPITAL)
    {
        if (response.handled)
        {
            ArmCapsCompensationAsync();
            Global::LogToFileVerbose("CapsDbg OnTestKeyDown armed=1 pending=%ld worker=%ld sysCaps=%d",
                                     GetPendingCapsCompensateFlag(),
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
        }
        else
        {
            InterlockedExchange(&s_pendingCapsCompensate, 0);
            Global::LogToFileVerbose("CapsDbg OnTestKeyDown armed=0 pending=%ld worker=%ld sysCaps=%d",
                                     GetPendingCapsCompensateFlag(),
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
        }
    }
    LogCapsDebugResponse("OnTestKeyDown.response", vkCode, response, *pIsEaten);
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfKeyEventSink::OnKeyDown
//
// Called by the system to offer this service a keystroke.  If *pIsEaten == TRUE
// on exit, the application will not handle the keystroke.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnKeyDown(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten)
{
    if (pIsEaten == nullptr)
    {
        return E_INVALIDARG;
    }

    Global::UpdateModifiers(wParam, lParam);

    UINT vkCode = static_cast<UINT>(wParam);

    if (_trialExpired)
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyDown", vkCode, "skip=trial_expired");
        return S_OK;
    }


    if (_IsStartupGuardActive())
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyDown", vkCode, "skip=startup_guard");
        return S_OK;
    }

    if (_IsKeyboardDisabled(pContext))
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyDown", vkCode, "skip=keyboard_disabled");
        LogForegroundWindowInfo("OnKeyDown.keyboard_disabled");
        LogActiveProfileState("OnKeyDown.keyboard_disabled");
        return S_OK;
    }

    if (!_EnsurePipeConnected())
    {
        _RefreshKeyUpWindowForFailedKeyDown(vkCode,
                                            ExtractScanCodeFromLParam(lParam),
                                            ExtractExtendedFromLParam(lParam));
        UINT queueVkCode = 0;
        UINT queueScanCode = 0;
        BOOL queueShift = FALSE;
        BOOL queueCtrl = FALSE;
        BOOL queueAlt = FALSE;
        BOOL queueWin = FALSE;
        BOOL queueCapsLock = FALSE;
        BOOL queueNumLock = FALSE;
        UINT queueRepeat = 1;
        BOOL queueExtended = FALSE;
        BOOL queueCaretValid = FALSE;
        LONG queueCaretX = 0;
        LONG queueCaretY = 0;

        if (TryBuildPipeKeyEvent(wParam, lParam, TRUE, &queueVkCode, &queueScanCode, &queueShift, &queueCtrl, &queueAlt, &queueWin, &queueCapsLock, &queueNumLock, &queueRepeat, &queueExtended, &queueCaretValid, &queueCaretX, &queueCaretY))
        {
            ULONGLONG eventId = 0;
            if (!_TryConsumePendingKeyEvent(TRUE, wParam, lParam, &eventId))
            {
                eventId = _pPipeClient->NextKeyEventId();
            }
            _EnqueueFailedKeyMessage(queueVkCode,
                                     queueScanCode,
                                     TRUE,
                                     queueShift,
                                     queueCtrl,
                                     queueAlt,
                                     queueWin,
                                     queueCapsLock,
                                     queueNumLock,
                                     queueRepeat,
                                     queueExtended,
                                     queueCaretValid,
                                     queueCaretX,
                                     queueCaretY,
                                     "pipe_disconnected_keydown",
                                     eventId);
        }

        *pIsEaten = TRUE;
        LogKeyDecision("OnKeyDown", vkCode, "queued=pipe_disconnected");
        return S_OK;
    }

    BimeResponse response;
    BOOL hasCachedResponse = FALSE;

    if (_TryConsumePendingResponseCache(TRUE, wParam, lParam, &response))
    {
        hasCachedResponse = TRUE;

        BOOL shift = (GetKeyState(VK_SHIFT) & 0x8000) ? TRUE : FALSE;
        BOOL ctrl = (GetKeyState(VK_CONTROL) & 0x8000) ? TRUE : FALSE;
        BOOL alt = (GetKeyState(VK_MENU) & 0x8000) ? TRUE : FALSE;
        BOOL win = ((GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000)) ? TRUE : FALSE;
        BOOL capsLock = GetSystemCapsLockOn();
        BOOL numLock = (GetKeyState(VK_NUMLOCK) & 0x0001) ? TRUE : FALSE;
        UINT scanCode = ExtractScanCodeFromLParam(lParam);
        UINT repeat = static_cast<UINT>(lParam & 0xFFFF);
        BOOL extended = ExtractExtendedFromLParam(lParam);
        LogCapsDebugKey("OnKeyDown.cached", wParam, lParam, TRUE, vkCode, scanCode, (repeat == 0 ? 1u : repeat), extended, shift, ctrl, alt, win, capsLock, numLock);
    }

    if (hasCachedResponse)
    {
        BOOL forceEatCached = ShouldForceEatKey(vkCode, response);
        *pIsEaten = response.handled || forceEatCached;
        if (forceEatCached)
        {
            Global::LogToFileVerbose("KeySink OnKeyDown vk=%u force_eat=1 cache_only=1", vkCode);
        }
        Global::LogToFileVerbose("KeySink OnKeyDown vk=%u handled=%d cache_only=1", vkCode, *pIsEaten);
        LogCapsDebugResponse("OnKeyDown.cache_only", vkCode, response, *pIsEaten);
        return S_OK;
    }

    if (!hasCachedResponse)
    {
        UINT scanCode = 0;
        BOOL shift = FALSE;
        BOOL ctrl = FALSE;
        BOOL alt = FALSE;
        BOOL win = FALSE;
        BOOL capsLock = FALSE;
        BOOL numLock = FALSE;
        UINT repeat = 1;
        BOOL extended = FALSE;
        BOOL caretValid = FALSE;
        LONG caretX = 0;
        LONG caretY = 0;

        if (!TryBuildPipeKeyEvent(wParam, lParam, TRUE, &vkCode, &scanCode, &shift, &ctrl, &alt, &win, &capsLock, &numLock, &repeat, &extended, &caretValid, &caretX, &caretY))
        {
            *pIsEaten = FALSE;
            LogKeyDecision("OnKeyDown", static_cast<UINT>(wParam), "skip=build_event_failed");
            return S_OK;
        }
        LogCapsDebugKey("OnKeyDown.built", wParam, lParam, TRUE, vkCode, scanCode, repeat, extended, shift, ctrl, alt, win, capsLock, numLock);
        ULONGLONG eventId = 0;
        if (!_TryConsumePendingKeyEvent(TRUE, wParam, lParam, &eventId))
        {
            eventId = _pPipeClient->NextKeyEventId();
        }
        if (!_FlushFailedKeyQueue(pContext, "OnKeyDown.prepend"))
        {
            _RefreshKeyUpWindowForFailedKeyDown(vkCode, scanCode, extended);
            _EnqueueFailedKeyMessage(vkCode,
                                     scanCode,
                                     TRUE,
                                     shift,
                                     ctrl,
                                     alt,
                                     win,
                                     capsLock,
                                     numLock,
                                     repeat,
                                     extended,
                                     caretValid,
                                     caretX,
                                     caretY,
                                     "flush_failed_before_keydown",
                                     eventId);
            *pIsEaten = TRUE;
            Global::LogToFile("KeySink OnKeyDown vk=%u queue_current=1 reason=flush_failed", vkCode);
            return S_OK;
        }

        HRESULT hr = _pPipeClient->SendKeyAndWait(vkCode,
                                                  scanCode,
                                                  TRUE,
                                                  "keydown",
                                                  shift,
                                                  ctrl,
                                                  alt,
                                                  win,
                                                  capsLock,
                                                  numLock,
                                                  repeat,
                                                  extended,
                                                  caretValid,
                                                  caretX,
                                                  caretY,
                                                  &response,
                                                  kPipeKeyResponseTimeoutMs,
                                                  eventId);
        if (FAILED(hr))
        {
            _RefreshKeyUpWindowForFailedKeyDown(vkCode, scanCode, extended);
            _EnqueueFailedKeyMessage(vkCode,
                                     scanCode,
                                     TRUE,
                                     shift,
                                     ctrl,
                                     alt,
                                     win,
                                     capsLock,
                                     numLock,
                                     repeat,
                                     extended,
                                     caretValid,
                                     caretX,
                                     caretY,
                                     "send_failed_keydown",
                                     eventId);
            *pIsEaten = TRUE;
            Global::LogToFile("KeySink OnKeyDown vk=%u pipe_error hr=0x%08X queued=1", vkCode, static_cast<unsigned>(hr));
            return S_OK;
        }
    }

    _RefreshKeyUpWindowForKeyDown(vkCode,
                                  ExtractScanCodeFromLParam(lParam),
                                  ExtractExtendedFromLParam(lParam),
                                  response);

    BOOL forceEat = ShouldForceEatKey(vkCode, response);
    *pIsEaten = response.handled || forceEat;

    BOOL committedViaAnchor = FALSE;
    _ApplyResponseAndSyncState(pContext, &response, "OnKeyDown", &committedViaAnchor);
    Global::LogToFileVerbose("KeySink OnKeyDown vk=%u handled=%d text_len=%u input_len=%u cache=%d commit_anchor=%d",
                             vkCode,
                             *pIsEaten,
                             static_cast<unsigned>(response.textToOutput.length()),
                             static_cast<unsigned>(response.inputBuffer.length()),
                             hasCachedResponse,
                             committedViaAnchor);
    if (forceEat)
    {
        Global::LogToFileVerbose("KeySink OnKeyDown vk=%u force_eat=1", vkCode);
    }
    LogCapsDebugResponse("OnKeyDown.response", vkCode, response, *pIsEaten);

    if (vkCode == VK_CAPITAL)
    {
        if (response.handled)
        {
            ArmCapsCompensationAsync();
            Global::LogToFileVerbose("CapsDbg OnKeyDown armed=1 pending=%ld worker=%ld sysCaps=%d",
                                     GetPendingCapsCompensateFlag(),
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
        }
        else
        {
            InterlockedExchange(&s_pendingCapsCompensate, 0);
            Global::LogToFileVerbose("CapsDbg OnKeyDown armed=0 pending=%ld worker=%ld sysCaps=%d",
                                     GetPendingCapsCompensateFlag(),
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
        }
    }

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfKeyEventSink::OnTestKeyUp
//
// Called by the system to query this service wants a potential keystroke.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnTestKeyUp(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten)
{
    if (pIsEaten == nullptr)
    {
        return E_INVALIDARG;
    }

    Global::UpdateModifiers(wParam, lParam);

    UINT vkCode = static_cast<UINT>(wParam);

    if (_pendingResponseValid &&
        !_pendingResponseIsKeyDown &&
        IsPendingKeyEventMatch(_pendingResponseWParam, _pendingResponseScanCode, _pendingResponseExtended, wParam, lParam))
    {
        BimeResponse cachedResponse;
        cachedResponse.success = TRUE;
        cachedResponse.handled = _pendingResponseHandled;
        cachedResponse.expectKeyUp = _pendingResponseExpectKeyUp;
        cachedResponse.hasKeyboardOpen = _pendingResponseHasKeyboardOpen;
        cachedResponse.keyboardOpen = _pendingResponseKeyboardOpen;
        cachedResponse.cancelComposition = _pendingResponseCancelComposition;
        cachedResponse.compositionTracking = _pendingResponseCompositionTracking;
        cachedResponse.compositionPending = _pendingResponseCompositionPending;
        cachedResponse.textToOutput = _pendingResponseTextToOutput;
        cachedResponse.inputBuffer = _pendingResponseInputBuffer;

        *pIsEaten = cachedResponse.handled;
        if (cachedResponse.hasKeyboardOpen)
        {
            _SyncKeyboardOpenCompartment(cachedResponse.keyboardOpen);
            Global::LogToFileVerbose("CompartmentSync OnTestKeyUp keyboard_open=%d cache=1", cachedResponse.keyboardOpen);
        }
        Global::LogToFileVerbose("KeySink OnTestKeyUp vk=%u handled=%d cache_reuse=1", vkCode, *pIsEaten);

        if (vkCode == VK_CAPITAL && GetPendingCapsCompensateFlag() != 0)
        {
            InterlockedExchange(&s_pendingCapsCompensate, 0);
            Global::LogToFileVerbose("CapsDbg OnTestKeyUp(cache) apply pending=0 worker=%ld sysCaps(before)=%d",
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
            ToggleCapsLockOnce();
            Global::LogToFileVerbose("CapsDbg OnTestKeyUp(cache) applied worker=%ld sysCaps(after)=%d",
                                     GetCapsCompensateWorkerFlag(),
                                     GetSystemCapsLockOn() ? 1 : 0);
            _pendingResponseHandled = TRUE;
            *pIsEaten = TRUE;
        }

        return S_OK;
    }

    _ClearPendingResponseCache();
    _ClearPendingKeyEvent();

    if (!_ShouldForwardKeyUp(wParam, lParam))
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyUp", vkCode, "skip=outside_window");
        return S_OK;
    }

    if (_trialExpired)
    {
        if (vkCode == VK_CAPITAL)
        {
            ResetCapsCompensationState("OnTestKeyUp.trial_expired");
        }
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyUp", vkCode, "skip=trial_expired");
        return S_OK;
    }


    if (_IsStartupGuardActive())
    {
        if (vkCode == VK_CAPITAL)
        {
            ResetCapsCompensationState("OnTestKeyUp.startup_guard");
        }
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyUp", vkCode, "skip=startup_guard");
        return S_OK;
    }

    if (_IsKeyboardDisabled(pContext))
    {
        if (vkCode == VK_CAPITAL)
        {
            ResetCapsCompensationState("OnTestKeyUp.keyboard_disabled");
        }
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyUp", vkCode, "skip=keyboard_disabled");
        LogForegroundWindowInfo("OnTestKeyUp.keyboard_disabled");
        LogActiveProfileState("OnTestKeyUp.keyboard_disabled");
        return S_OK;
    }

    if (!_EnsurePipeConnected())
    {
        if (vkCode == VK_CAPITAL)
        {
            ResetCapsCompensationState("OnTestKeyUp.pipe_disconnected");
        }
        *pIsEaten = TRUE;
        LogKeyDecision("OnTestKeyUp", vkCode, "hold=pipe_disconnected");
        return S_OK;
    }

    UINT scanCode = 0;
    BOOL shift = FALSE;
    BOOL ctrl = FALSE;
    BOOL alt = FALSE;
    BOOL win = FALSE;
    BOOL capsLock = FALSE;
    BOOL numLock = FALSE;
    UINT repeat = 1;
    BOOL extended = FALSE;
    BOOL caretValid = FALSE;
    LONG caretX = 0;
    LONG caretY = 0;

    if (!TryBuildPipeKeyEvent(wParam, lParam, FALSE, &vkCode, &scanCode, &shift, &ctrl, &alt, &win, &capsLock, &numLock, &repeat, &extended, &caretValid, &caretX, &caretY))
    {
        *pIsEaten = FALSE;
        LogKeyDecision("OnTestKeyUp", static_cast<UINT>(wParam), "skip=build_event_failed");
        return S_OK;
    }
    LogCapsDebugKey("OnTestKeyUp.built", wParam, lParam, FALSE, vkCode, scanCode, repeat, extended, shift, ctrl, alt, win, capsLock, numLock);
    ULONGLONG eventId = _pPipeClient->NextKeyEventId();
    _StorePendingKeyEvent(FALSE, wParam, scanCode, extended, eventId);
    if (!_FlushFailedKeyQueue(pContext, "OnTestKeyUp.prepend"))
    {
        *pIsEaten = TRUE;
        Global::LogToFile("KeySink OnTestKeyUp vk=%u defer_current=1 reason=flush_failed", vkCode);
        return S_OK;
    }

    BimeResponse response;
    HRESULT hr = _pPipeClient->SendKeyAndWait(vkCode,
                                              scanCode,
                                              FALSE,
                                              "test_keyup",
                                              shift,
                                              ctrl,
                                              alt,
                                              win,
                                              capsLock,
                                              numLock,
                                              repeat,
                                              extended,
                                              FALSE,
                                              0,
                                              0,
                                              &response,
                                              kPipeKeyResponseTimeoutMs,
                                              eventId);
    if (FAILED(hr))
    {
        *pIsEaten = TRUE;
        Global::LogToFile("KeySink OnTestKeyUp vk=%u pipe_error hr=0x%08X retry_in_keyup=1", vkCode, static_cast<unsigned>(hr));
        return S_OK;
    }

    _StorePendingResponseCache(FALSE, wParam, scanCode, extended, response);

    *pIsEaten = response.handled;
    BOOL committedViaAnchor = FALSE;
    _ApplyResponseAndSyncState(pContext, &response, "OnTestKeyUp", &committedViaAnchor);
    Global::LogToFileVerbose("KeySink OnTestKeyUp vk=%u handled=%d commit_anchor=%d", vkCode, *pIsEaten, committedViaAnchor);
    LogCapsDebugResponse("OnTestKeyUp.response", vkCode, response, *pIsEaten);
    if (vkCode == VK_CAPITAL && GetPendingCapsCompensateFlag() != 0)
    {
        InterlockedExchange(&s_pendingCapsCompensate, 0);
        Global::LogToFileVerbose("CapsDbg OnTestKeyUp apply pending=0 worker=%ld sysCaps(before)=%d",
                                 GetCapsCompensateWorkerFlag(),
                                 GetSystemCapsLockOn() ? 1 : 0);
        ToggleCapsLockOnce();
        Global::LogToFileVerbose("CapsDbg OnTestKeyUp applied worker=%ld sysCaps(after)=%d",
                                 GetCapsCompensateWorkerFlag(),
                                 GetSystemCapsLockOn() ? 1 : 0);
        _pendingResponseHandled = TRUE;
        *pIsEaten = TRUE;
    }
    _AdvanceKeyUpWindow(wParam, lParam);
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfKeyEventSink::OnKeyUp
//
// Called by the system to offer this service a keystroke.  If *pIsEaten == TRUE
// on exit, the application will not handle the keystroke.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnKeyUp(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten)
{
    pContext;
    if (pIsEaten == nullptr)
    {
        return E_INVALIDARG;
    }

    Global::UpdateModifiers(wParam, lParam);

    UINT vkCode = static_cast<UINT>(wParam);

    BOOL hasMatchingTestResponse = _pendingResponseValid &&
                                   !_pendingResponseIsKeyDown &&
                                   IsPendingKeyEventMatch(_pendingResponseWParam,
                                                          _pendingResponseScanCode,
                                                          _pendingResponseExtended,
                                                          wParam,
                                                          lParam);
    if (!_ShouldForwardKeyUp(wParam, lParam) && !hasMatchingTestResponse)
    {
        _ClearPendingResponseCache();
        _ClearPendingKeyEvent();
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyUp", vkCode, "skip=outside_window");
        return S_OK;
    }

    if (_trialExpired)
    {
        _AdvanceKeyUpWindow(wParam, lParam);
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyUp", vkCode, "skip=trial_expired");
        return S_OK;
    }


    if (_IsStartupGuardActive())
    {
        _AdvanceKeyUpWindow(wParam, lParam);
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyUp", vkCode, "skip=startup_guard");
        return S_OK;
    }

    if (_IsKeyboardDisabled(pContext))
    {
        _AdvanceKeyUpWindow(wParam, lParam);
        *pIsEaten = FALSE;
        LogKeyDecision("OnKeyUp", vkCode, "skip=keyboard_disabled");
        LogForegroundWindowInfo("OnKeyUp.keyboard_disabled");
        LogActiveProfileState("OnKeyUp.keyboard_disabled");
        return S_OK;
    }

    if (!_EnsurePipeConnected())
    {
        UINT queueVkCode = 0;
        UINT queueScanCode = 0;
        BOOL queueShift = FALSE;
        BOOL queueCtrl = FALSE;
        BOOL queueAlt = FALSE;
        BOOL queueWin = FALSE;
        BOOL queueCapsLock = FALSE;
        BOOL queueNumLock = FALSE;
        UINT queueRepeat = 1;
        BOOL queueExtended = FALSE;
        BOOL queueCaretValid = FALSE;
        LONG queueCaretX = 0;
        LONG queueCaretY = 0;

        if (TryBuildPipeKeyEvent(wParam, lParam, FALSE, &queueVkCode, &queueScanCode, &queueShift, &queueCtrl, &queueAlt, &queueWin, &queueCapsLock, &queueNumLock, &queueRepeat, &queueExtended, &queueCaretValid, &queueCaretX, &queueCaretY))
        {
            ULONGLONG eventId = 0;
            if (!_TryConsumePendingKeyEvent(FALSE, wParam, lParam, &eventId))
            {
                eventId = _pPipeClient->NextKeyEventId();
            }
            _EnqueueFailedKeyMessage(queueVkCode,
                                     queueScanCode,
                                     FALSE,
                                     queueShift,
                                     queueCtrl,
                                     queueAlt,
                                     queueWin,
                                     queueCapsLock,
                                     queueNumLock,
                                     queueRepeat,
                                     queueExtended,
                                     queueCaretValid,
                                     queueCaretX,
                                     queueCaretY,
                                     "pipe_disconnected_keyup",
                                     eventId);
        }

        *pIsEaten = TRUE;
        _AdvanceKeyUpWindow(wParam, lParam);
        LogKeyDecision("OnKeyUp", vkCode, "queued=pipe_disconnected");
        return S_OK;
    }

    BimeResponse response;
    BOOL hasCachedResponse = FALSE;

    if (_TryConsumePendingResponseCache(FALSE, wParam, lParam, &response))
    {
        hasCachedResponse = TRUE;

        BOOL shift = (GetKeyState(VK_SHIFT) & 0x8000) ? TRUE : FALSE;
        BOOL ctrl = (GetKeyState(VK_CONTROL) & 0x8000) ? TRUE : FALSE;
        BOOL alt = (GetKeyState(VK_MENU) & 0x8000) ? TRUE : FALSE;
        BOOL win = ((GetKeyState(VK_LWIN) & 0x8000) || (GetKeyState(VK_RWIN) & 0x8000)) ? TRUE : FALSE;
        BOOL capsLock = GetSystemCapsLockOn();
        BOOL numLock = (GetKeyState(VK_NUMLOCK) & 0x0001) ? TRUE : FALSE;
        UINT scanCode = ExtractScanCodeFromLParam(lParam);
        UINT repeat = static_cast<UINT>(lParam & 0xFFFF);
        BOOL extended = ExtractExtendedFromLParam(lParam);
        LogCapsDebugKey("OnKeyUp.cached", wParam, lParam, FALSE, vkCode, scanCode, (repeat == 0 ? 1u : repeat), extended, shift, ctrl, alt, win, capsLock, numLock);
    }

    if (hasCachedResponse)
    {
        BOOL forceEatCached = ShouldForceEatKey(vkCode, response);
        *pIsEaten = response.handled || forceEatCached;
        if (forceEatCached)
        {
            Global::LogToFileVerbose("KeySink OnKeyUp vk=%u force_eat=1 cache_only=1", vkCode);
        }
        Global::LogToFileVerbose("KeySink OnKeyUp vk=%u handled=%d cache_only=1", vkCode, *pIsEaten);
        LogCapsDebugResponse("OnKeyUp.cache_only", vkCode, response, *pIsEaten);
        return S_OK;
    }

    if (!hasCachedResponse)
    {
        UINT scanCode = 0;
        BOOL shift = FALSE;
        BOOL ctrl = FALSE;
        BOOL alt = FALSE;
        BOOL win = FALSE;
        BOOL capsLock = FALSE;
        BOOL numLock = FALSE;
        UINT repeat = 1;
        BOOL extended = FALSE;
        BOOL caretValid = FALSE;
        LONG caretX = 0;
        LONG caretY = 0;

        if (!TryBuildPipeKeyEvent(wParam, lParam, FALSE, &vkCode, &scanCode, &shift, &ctrl, &alt, &win, &capsLock, &numLock, &repeat, &extended, &caretValid, &caretX, &caretY))
        {
            _AdvanceKeyUpWindow(wParam, lParam);
            *pIsEaten = FALSE;
            LogKeyDecision("OnKeyUp", static_cast<UINT>(wParam), "skip=build_event_failed");
            return S_OK;
        }
        LogCapsDebugKey("OnKeyUp.built", wParam, lParam, FALSE, vkCode, scanCode, repeat, extended, shift, ctrl, alt, win, capsLock, numLock);
        ULONGLONG eventId = 0;
        if (!_TryConsumePendingKeyEvent(FALSE, wParam, lParam, &eventId))
        {
            eventId = _pPipeClient->NextKeyEventId();
        }
        if (!_FlushFailedKeyQueue(pContext, "OnKeyUp.prepend"))
        {
            _EnqueueFailedKeyMessage(vkCode,
                                     scanCode,
                                     FALSE,
                                     shift,
                                     ctrl,
                                     alt,
                                     win,
                                     capsLock,
                                     numLock,
                                     repeat,
                                     extended,
                                     caretValid,
                                     caretX,
                                     caretY,
                                     "flush_failed_before_keyup",
                                     eventId);
            *pIsEaten = TRUE;
            _AdvanceKeyUpWindow(wParam, lParam);
            Global::LogToFile("KeySink OnKeyUp vk=%u queue_current=1 reason=flush_failed", vkCode);
            return S_OK;
        }

        HRESULT hr = _pPipeClient->SendKeyAndWait(vkCode,
                                                  scanCode,
                                                  FALSE,
                                                  "keyup",
                                                  shift,
                                                  ctrl,
                                                  alt,
                                                  win,
                                                  capsLock,
                                                  numLock,
                                                  repeat,
                                                  extended,
                                                  FALSE,
                                                  0,
                                                  0,
                                                  &response,
                                                  kPipeKeyResponseTimeoutMs,
                                                  eventId);
        if (FAILED(hr))
        {
            _EnqueueFailedKeyMessage(vkCode,
                                     scanCode,
                                     FALSE,
                                     shift,
                                     ctrl,
                                     alt,
                                     win,
                                     capsLock,
                                     numLock,
                                     repeat,
                                     extended,
                                     caretValid,
                                     caretX,
                                     caretY,
                                     "send_failed_keyup",
                                     eventId);
            *pIsEaten = TRUE;
            _AdvanceKeyUpWindow(wParam, lParam);
            Global::LogToFile("KeySink OnKeyUp vk=%u pipe_error hr=0x%08X queued=1", vkCode, static_cast<unsigned>(hr));
            return S_OK;
        }
    }

    BOOL forceEat = ShouldForceEatKey(vkCode, response);
    *pIsEaten = response.handled || forceEat;

    BOOL committedViaAnchor = FALSE;
    _ApplyResponseAndSyncState(pContext, &response, "OnKeyUp", &committedViaAnchor);
    Global::LogToFileVerbose("KeySink OnKeyUp vk=%u handled=%d text_len=%u input_len=%u cache=%d commit_anchor=%d",
                             vkCode,
                             *pIsEaten,
                             static_cast<unsigned>(response.textToOutput.length()),
                             static_cast<unsigned>(response.inputBuffer.length()),
                             hasCachedResponse,
                             committedViaAnchor);
    if (forceEat)
    {
        Global::LogToFileVerbose("KeySink OnKeyUp vk=%u force_eat=1", vkCode);
    }
    LogCapsDebugResponse("OnKeyUp.response", vkCode, response, *pIsEaten);
    if (vkCode == VK_CAPITAL && GetPendingCapsCompensateFlag() != 0)
    {
        InterlockedExchange(&s_pendingCapsCompensate, 0);
        Global::LogToFileVerbose("CapsDbg OnKeyUp apply pending=0 worker=%ld sysCaps(before)=%d",
                                 GetCapsCompensateWorkerFlag(),
                                 GetSystemCapsLockOn() ? 1 : 0);
        ToggleCapsLockOnce();
        Global::LogToFileVerbose("CapsDbg OnKeyUp applied worker=%ld sysCaps(after)=%d",
                                 GetCapsCompensateWorkerFlag(),
                                 GetSystemCapsLockOn() ? 1 : 0);
        *pIsEaten = TRUE;
    }

    _AdvanceKeyUpWindow(wParam, lParam);

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfKeyEventSink::OnPreservedKey
//
// Called when a hotkey (registered by us, or by the system) is typed.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnPreservedKey(ITfContext *pContext, REFGUID rguid, BOOL *pIsEaten)
{
    pContext;
    rguid;

    if (pIsEaten == nullptr)
    {
        return E_INVALIDARG;
    }

    // Ctrl+Space is handled by the regular key forwarding pipeline to BimeCore.
    *pIsEaten = FALSE;
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// _InitKeyEventSink
//
// Advise a keystroke sink.
//----------------------------------------------------------------------------

BOOL CSampleIME::_InitKeyEventSink()
{
    ITfKeystrokeMgr* pKeystrokeMgr = nullptr;
    HRESULT hr = S_OK;

    if (FAILED(_pThreadMgr->QueryInterface(IID_ITfKeystrokeMgr, (void **)&pKeystrokeMgr)))
    {
        return FALSE;
    }

    hr = pKeystrokeMgr->AdviseKeyEventSink(_tfClientId, (ITfKeyEventSink *)this, _keySinkUseForeground);

    pKeystrokeMgr->Release();

    Global::LogToFileVerbose("KeySink Init advise hr=0x%08X foreground=%d",
                             static_cast<unsigned>(hr),
                             _keySinkUseForeground ? 1 : 0);
    return (hr == S_OK);
}

//+---------------------------------------------------------------------------
//
// _UninitKeyEventSink
//
// Unadvise a keystroke sink.  Assumes we have advised one already.
//----------------------------------------------------------------------------

void CSampleIME::_UninitKeyEventSink()
{
    if (_pThreadMgr == nullptr)
    {
        return;
    }

    ITfKeystrokeMgr* pKeystrokeMgr = nullptr;

    if (FAILED(_pThreadMgr->QueryInterface(IID_ITfKeystrokeMgr, (void **)&pKeystrokeMgr)))
    {
        return;
    }

    pKeystrokeMgr->UnadviseKeyEventSink(_tfClientId);

    pKeystrokeMgr->Release();
}

void CSampleIME::_AdjustKeySinkModeForForegroundWindow(_In_opt_ HWND hwndForeground)
{
    _RefreshImmersiveState("KeySink.AdjustMode", hwndForeground);

    const BOOL isImmersiveWindow = IsImmersiveForegroundWindow(hwndForeground);
    const BOOL isShellTray = IsShellTrayWindow(hwndForeground);
    // Keep standard TSF key sink mode for all windows, including immersive/UWP.
    const BOOL useForeground = TRUE;

    Global::LogToFileVerbose("KeySink mode evaluate immersive=%d immersive_window=%d shell=%d useForeground=%d",
                             _isImmersiveSession,
                             isImmersiveWindow,
                             isShellTray,
                             useForeground);

    if (useForeground == _keySinkUseForeground)
    {
        return;
    }

    _UninitKeyEventSink();
    _keySinkUseForeground = useForeground;

    if (!_InitKeyEventSink())
    {
        Global::LogToFileVerbose("KeySink mode switch failed foreground=%d", _keySinkUseForeground ? 1 : 0);
        return;
    }

    Global::LogToFileVerbose("KeySink mode switched foreground=%d", _keySinkUseForeground ? 1 : 0);
}
