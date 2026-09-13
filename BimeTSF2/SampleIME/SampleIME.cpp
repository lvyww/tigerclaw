// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "globals.h"
#include "SampleIME.h"
#include "Compartment.h"
#include "PipeClient.h"
#include "EditSession.h"
#include "LanguageBar.h"
#include "TipCandidateList.h"
#include "TipCandidateString.h"
#include "EmbeddedBuildInfo.h"
#include "CoreLaunchContext.h"
#include "ProtectedInput.h"

#include <stdio.h>
#include <string.h>


static const ULONGLONG kCoreLaunchThrottleMs = 2000;
static const WCHAR kCoreInstallRegKey[] = L"Software\\TigerClaw\\Install";
static const WCHAR kCoreInstallRegValueName[] = L"CorePath";
static const WCHAR kCoreRunRegKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
static const WCHAR kCoreRunValueName[] = L"TigerClawCore";
static const WCHAR kCoreExeFileName[] = L"TigerClaw.Core.exe";


static BOOL IsRegularFilePath(_In_ const WCHAR *path)
{
    if (path == nullptr || path[0] == L'\0')
    {
        return FALSE;
    }

    DWORD attr = GetFileAttributesW(path);
    if (attr == INVALID_FILE_ATTRIBUTES)
    {
        return FALSE;
    }

    return ((attr & FILE_ATTRIBUTE_DIRECTORY) == 0) ? TRUE : FALSE;
}

static void TrimWrappedQuotesInPlace(_Inout_updates_(pathCount) WCHAR *path, size_t pathCount)
{
    if (path == nullptr || pathCount < 2)
    {
        return;
    }

    size_t len = wcsnlen_s(path, pathCount);
    if (len >= 2 && path[0] == L'"' && path[len - 1] == L'"')
    {
        for (size_t i = 0; i + 1 < len; ++i)
        {
            path[i] = path[i + 1];
        }
        path[len - 2] = L'\0';
    }
}

static BOOL TryReadCorePathFromRegistry(_In_ HKEY rootKey, _In_ REGSAM samDesired, _Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    if (path == nullptr || pathCount < 2)
    {
        return FALSE;
    }

    path[0] = L'\0';

    HKEY hKey = nullptr;
    LONG openResult = RegOpenKeyExW(rootKey, kCoreInstallRegKey, 0, samDesired, &hKey);
    if (openResult != ERROR_SUCCESS)
    {
        return FALSE;
    }

    DWORD valueType = 0;
    DWORD valueBytes = static_cast<DWORD>(pathCount * sizeof(WCHAR));
    LONG queryResult = RegQueryValueExW(hKey, kCoreInstallRegValueName, nullptr, &valueType, reinterpret_cast<LPBYTE>(path), &valueBytes);
    RegCloseKey(hKey);

    if (queryResult != ERROR_SUCCESS || (valueType != REG_SZ && valueType != REG_EXPAND_SZ))
    {
        path[0] = L'\0';
        return FALSE;
    }

    path[pathCount - 1] = L'\0';

    WCHAR expanded[MAX_PATH] = {};
    if (valueType == REG_EXPAND_SZ)
    {
        DWORD expandedLen = ExpandEnvironmentStringsW(path, expanded, ARRAYSIZE(expanded));
        if (expandedLen != 0 && expandedLen < ARRAYSIZE(expanded))
        {
            StringCchCopyW(path, pathCount, expanded);
        }
    }

    TrimWrappedQuotesInPlace(path, pathCount);
    return IsRegularFilePath(path);
}

static BOOL TryResolveBimeCorePathFromRunValue(_Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    if (path == nullptr || pathCount < 2)
    {
        return FALSE;
    }

    path[0] = L'\0';

    HKEY hKey = nullptr;
    LONG openResult = RegOpenKeyExW(HKEY_CURRENT_USER, kCoreRunRegKey, 0, KEY_QUERY_VALUE, &hKey);
    if (openResult != ERROR_SUCCESS)
    {
        return FALSE;
    }

    WCHAR runValue[MAX_PATH * 2] = {};
    DWORD valueType = 0;
    DWORD valueBytes = sizeof(runValue);
    LONG queryResult = RegQueryValueExW(hKey, kCoreRunValueName, nullptr, &valueType, reinterpret_cast<LPBYTE>(runValue), &valueBytes);
    RegCloseKey(hKey);

    if (queryResult != ERROR_SUCCESS || (valueType != REG_SZ && valueType != REG_EXPAND_SZ))
    {
        return FALSE;
    }

    runValue[(ARRAYSIZE(runValue) - 1)] = L'\0';

    WCHAR expanded[MAX_PATH * 2] = {};
    const WCHAR *valueToParse = runValue;
    if (valueType == REG_EXPAND_SZ)
    {
        DWORD expandedLen = ExpandEnvironmentStringsW(runValue, expanded, ARRAYSIZE(expanded));
        if (expandedLen != 0 && expandedLen < ARRAYSIZE(expanded))
        {
            valueToParse = expanded;
        }
    }

    const WCHAR *firstQuote = wcschr(valueToParse, L'"');
    if (firstQuote != nullptr)
    {
        ++firstQuote;
        const WCHAR *secondQuote = wcschr(firstQuote, L'"');
        if (secondQuote != nullptr && secondQuote > firstQuote)
        {
            size_t copyLen = static_cast<size_t>(secondQuote - firstQuote);
            if (copyLen < pathCount)
            {
                for (size_t i = 0; i < copyLen; ++i)
                {
                    path[i] = firstQuote[i];
                }
                path[copyLen] = L'\0';
                return IsRegularFilePath(path);
            }
        }
    }

    StringCchCopyW(path, pathCount, valueToParse);
    TrimWrappedQuotesInPlace(path, pathCount);
    return IsRegularFilePath(path);
}

static BOOL TryResolveBimeCorePathFromRegistry(_Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    if (path == nullptr || pathCount < 2)
    {
        return FALSE;
    }

    path[0] = L'\0';

    if (TryReadCorePathFromRegistry(HKEY_CURRENT_USER, KEY_QUERY_VALUE, path, pathCount))
    {
        return TRUE;
    }

#if defined(_WIN64)
    if (TryReadCorePathFromRegistry(HKEY_LOCAL_MACHINE, KEY_QUERY_VALUE, path, pathCount))
    {
        return TRUE;
    }
#else
    if (TryReadCorePathFromRegistry(HKEY_LOCAL_MACHINE, KEY_QUERY_VALUE | KEY_WOW64_64KEY, path, pathCount))
    {
        return TRUE;
    }
    if (TryReadCorePathFromRegistry(HKEY_LOCAL_MACHINE, KEY_QUERY_VALUE, path, pathCount))
    {
        return TRUE;
    }
#endif

    if (TryResolveBimeCorePathFromRunValue(path, pathCount))
    {
        return TRUE;
    }

    return FALSE;
}

static TfGuidAtom g_caretAnchorInputDisplayAttributeAtom = TF_INVALID_GUIDATOM;

static HRESULT EnsureInputDisplayAttributeAtom(_Out_ TfGuidAtom *pAtom)
{
    if (pAtom == nullptr)
    {
        return E_INVALIDARG;
    }

    if (g_caretAnchorInputDisplayAttributeAtom != TF_INVALID_GUIDATOM)
    {
        *pAtom = g_caretAnchorInputDisplayAttributeAtom;
        return S_OK;
    }

    ITfCategoryMgr *pCategoryMgr = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void **)&pCategoryMgr);
    if (FAILED(hr) || pCategoryMgr == nullptr)
    {
        return FAILED(hr) ? hr : E_FAIL;
    }

    TfGuidAtom atom = TF_INVALID_GUIDATOM;
    hr = pCategoryMgr->RegisterGUID(Global::SampleIMEGuidDisplayAttributeInput, &atom);
    pCategoryMgr->Release();

    if (SUCCEEDED(hr))
    {
        g_caretAnchorInputDisplayAttributeAtom = atom;
    }

    *pAtom = atom;
    return hr;
}

static void ApplyInputDisplayAttribute(_In_opt_ ITfContext *pContext, TfEditCookie ec, _In_opt_ ITfRange *pRange)
{
    if (pContext == nullptr || pRange == nullptr)
    {
        return;
    }

    TfGuidAtom atom = TF_INVALID_GUIDATOM;
    HRESULT hr = EnsureInputDisplayAttributeAtom(&atom);
    if (FAILED(hr) || atom == TF_INVALID_GUIDATOM)
    {
        Global::LogToFileVerbose("CaretAnchor: ensure_display_attr_atom failed hr=0x%08X", static_cast<unsigned>(hr));
        return;
    }

    ITfProperty *pDisplayAttributeProperty = nullptr;
    hr = pContext->GetProperty(GUID_PROP_ATTRIBUTE, &pDisplayAttributeProperty);
    if (FAILED(hr) || pDisplayAttributeProperty == nullptr)
    {
        Global::LogToFileVerbose("CaretAnchor: get_display_attr_prop failed hr=0x%08X", static_cast<unsigned>(hr));
        return;
    }

    VARIANT var = {};
    var.vt = VT_I4;
    var.lVal = atom;
    hr = pDisplayAttributeProperty->SetValue(ec, pRange, &var);
    pDisplayAttributeProperty->Release();

    if (FAILED(hr))
    {
        Global::LogToFileVerbose("CaretAnchor: set_display_attr failed hr=0x%08X", static_cast<unsigned>(hr));
    }
}

static void ClearRangeDisplayAttribute(_In_opt_ ITfContext *pContext, TfEditCookie ec, _In_opt_ ITfRange *pRange)
{
    if (pContext == nullptr || pRange == nullptr)
    {
        return;
    }

    ITfProperty *pDisplayAttributeProperty = nullptr;
    HRESULT hr = pContext->GetProperty(GUID_PROP_ATTRIBUTE, &pDisplayAttributeProperty);
    if (FAILED(hr) || pDisplayAttributeProperty == nullptr)
    {
        Global::LogToFileVerbose("CaretAnchor: clear_display_attr_prop failed hr=0x%08X", static_cast<unsigned>(hr));
        return;
    }

    hr = pDisplayAttributeProperty->Clear(ec, pRange);
    pDisplayAttributeProperty->Release();

    if (FAILED(hr))
    {
        Global::LogToFileVerbose("CaretAnchor: clear_display_attr failed hr=0x%08X", static_cast<unsigned>(hr));
    }
}

static const WCHAR kCaretCoalesceWindowClass[] = L"TigerClaw.CaretCoalesceWindow";
static const UINT_PTR kCaretCoalesceTimerId = 1;
static const UINT_PTR kFocusQueryStateTimerId = 2;
// ime_active publish retry: on first launch Core is still starting, so the pipe is down when we
// first want to report active=TRUE. Retry on a timer until Core connects (bounded).
static const UINT_PTR kImeActivePublishRetryTimerId = 3;
static const UINT_PTR kCompositionRefreshTimerId = 4;
static const UINT kImeActivePublishRetryDelayMs = 400;
static const int kImeActivePublishRetryMax = 15;
static const UINT kCaretCoalesceWindowMs = 12;
static const UINT kCaretAnchorOverrideWindowMs = 3;
static const UINT kFocusQueryStateDelayMs = 30;
static const UINT kFocusSyncDebounceMs = 80;
static const DWORD kPipeHelloTimeoutMs = 80;
static const DWORD kPipeFocusQueryTimeoutMs = 60;
static const UINT kCompositionPendingRefreshMs = 20;
static const UINT kCompositionTrackingRefreshMs = 120;
static const DWORD kCompositionRefreshTimeoutMs = 60;
static const LONG kCaretCoalesceImmediateJumpThreshold = 10;
static const UINT kCaretEndEditSuppressWindowMs = 24;
static const UINT kCaretSendCooldownMs = 20;

static int GetCaretSourcePriority(int source)
{
    switch (source)
    {
    case CARET_SOURCE_LAYOUT:
        return 3;
    case CARET_SOURCE_END_EDIT:
        return 2;
    case CARET_SOURCE_ANCHOR:
    case CARET_SOURCE_COMMIT:
        return 1;
    default:
        return 0;
    }
}

LRESULT CALLBACK CSampleIME_WindowProc(HWND wndHandle, UINT uMsg, WPARAM wParam, LPARAM lParam)
{
    CSampleIME *pTextService = reinterpret_cast<CSampleIME *>(GetWindowLongPtrW(wndHandle, GWLP_USERDATA));

    switch (uMsg)
    {
    case WM_NCCREATE:
    {
        CREATESTRUCTW *pCreateStruct = reinterpret_cast<CREATESTRUCTW *>(lParam);
        if (pCreateStruct != nullptr)
        {
            pTextService = reinterpret_cast<CSampleIME *>(pCreateStruct->lpCreateParams);
            SetWindowLongPtrW(wndHandle, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(pTextService));
        }
        return TRUE;
    }

    case WM_TIMER:
        if (pTextService != nullptr)
        {
            // ReadResponse pumps timers. Keep our timers armed, but defer their
            // pipe work until the active request has returned.
            if (pTextService->_pPipeClient != nullptr && pTextService->_pPipeClient->IsBusy())
            {
                return 0;
            }
            if (wParam == kFailedKeyFlushTimerId)
            {
                pTextService->_HandleFailedKeyFlush();
                return 0;
            }
            if (wParam == kCaretCoalesceTimerId)
            {
                pTextService->_FlushPendingCaretMessage(FALSE);
                return 0;
            }
            if (wParam == kFocusQueryStateTimerId)
            {
                pTextService->_HandleDeferredFocusStateQuery();
                return 0;
            }
            if (wParam == kImeActivePublishRetryTimerId)
            {
                if (pTextService->_msgWndHandle != nullptr)
                {
                    KillTimer(pTextService->_msgWndHandle, kImeActivePublishRetryTimerId);
                }
                pTextService->_PublishImeActive();
                return 0;
            }
            if (wParam == kCompositionRefreshTimerId)
            {
                pTextService->_HandleCompositionRefresh();
                return 0;
            }
        }
        break;

    case WM_DeferredReopenCaretAnchorComposition:
        if (pTextService != nullptr)
        {
            pTextService->_HandleDeferredCaretAnchorReopen();
            return 0;
        }
        break;

    case WM_PrimeCaretTrackingFromAnchor:
        if (pTextService != nullptr)
        {
            pTextService->_caretTrackingPrimePending = FALSE;
            pTextService->_PrimeCaretTrackingFromAnchor();
            return 0;
        }
        break;

    case WM_DESTROY:
        if (pTextService != nullptr && pTextService->_msgWndHandle == wndHandle)
        {
            KillTimer(wndHandle, kCaretCoalesceTimerId);
            KillTimer(wndHandle, kFocusQueryStateTimerId);
            KillTimer(wndHandle, kImeActivePublishRetryTimerId);
            KillTimer(wndHandle, kCompositionRefreshTimerId);
            KillTimer(wndHandle, kFailedKeyFlushTimerId);
            pTextService->_ClearDeferredCaretAnchorReopen();
            pTextService->_caretTrackingPrimePending = FALSE;
            pTextService->_msgWndHandle = nullptr;
        }
        break;

    default:
        break;
    }

    return DefWindowProcW(wndHandle, uMsg, wParam, lParam);
}

//+---------------------------------------------------------------------------
//
// CreateInstance
//
//----------------------------------------------------------------------------

/* static */
HRESULT CSampleIME::CreateInstance(_In_ IUnknown *pUnkOuter, REFIID riid, _Outptr_ void **ppvObj)
{
    CSampleIME* pSampleIME = nullptr;
    HRESULT hr = S_OK;

    if (ppvObj == nullptr)
    {
        return E_INVALIDARG;
    }

    *ppvObj = nullptr;

    if (nullptr != pUnkOuter)
    {
        return CLASS_E_NOAGGREGATION;
    }

    pSampleIME = new (std::nothrow) CSampleIME();
    if (pSampleIME == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    hr = pSampleIME->QueryInterface(riid, ppvObj);

    pSampleIME->Release();

    return hr;
}

//+---------------------------------------------------------------------------
//
// ctor
//
//----------------------------------------------------------------------------

CSampleIME::CSampleIME()
{
    DllAddRef();

    _pThreadMgr = nullptr;

    _threadMgrEventSinkCookie = TF_INVALID_COOKIE;

    _pLangBarItem = nullptr;

    _pDocMgrLastFocused = nullptr;

    _pSIPIMEOnOffCompartment = nullptr;
    _dwSIPIMEOnOffCompartmentSinkCookie = 0;
    _msgWndHandle = nullptr;
    _imeActivePublishRetryCount = 0;

    _pPipeClient = new (std::nothrow) CPipeClient();

    _activateTick = 0;
    _isDevenvHost = FALSE;
    _ctrlSpacePreservedKeyRegistered = FALSE;
    _keySinkUseForeground = TRUE;
    _isImmersiveSession = FALSE;
    _lastCoreLaunchAttemptTick = 0;
    _coreLaunchWorkerRunning = 0;
    _lastFocusHwnd = 0;
    _lastFocusProcessId = 0;
    _lastFocusSentTick = 0;
    _pendingFocusHwnd = 0;
    _pendingFocusProcessId = 0;
    _focusQueryPending = FALSE;

    _hasSentCaret = FALSE;
    _lastCaretSentTick = 0;
    _lastSentCaretX = 0;
    _lastSentCaretY = 0;
    _lastSentCaretWidth = 0;
    _lastSentCaretHeight = 0;
    _lastLayoutCaretTick = 0;
    _lastHighPriorityCaretTick = 0;
    _lastLayoutRequestTick = 0;
    _lastLayoutCaretX = 0;
    _lastLayoutCaretY = 0;
    _hasPendingCaret = FALSE;
    _pendingCaretX = 0;
    _pendingCaretY = 0;
    _pendingCaretWidth = 0;
    _pendingCaretHeight = 0;
    _pendingCaretSource = CARET_SOURCE_UNKNOWN;
    _forceNextCaret = TRUE;
    _caretTrackingPrimePending = FALSE;
    _suppressExternalCompositionCanceledNotify = FALSE;


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
    _deferredReopenInputBuffer.clear();
    _keyUpForwardBudget = 0;

    _refCount = 1;
}

BOOL CSampleIME::_IsRangeCovered(TfEditCookie ec, _In_ ITfRange *pRangeTest, _In_ ITfRange *pRangeCover)
{
    if (pRangeTest == nullptr || pRangeCover == nullptr)
    {
        return FALSE;
    }

    LONG compareResult = 0;
    if (FAILED(pRangeCover->CompareStart(ec, pRangeTest, TF_ANCHOR_START, &compareResult)) || compareResult > 0)
    {
        return FALSE;
    }

    if (FAILED(pRangeCover->CompareEnd(ec, pRangeTest, TF_ANCHOR_END, &compareResult)) || compareResult < 0)
    {
        return FALSE;
    }

    return TRUE;
}

//+---------------------------------------------------------------------------
//
// dtor
//
//----------------------------------------------------------------------------

CSampleIME::~CSampleIME()
{
    if (_pPipeClient)
    {
        delete _pPipeClient;
        _pPipeClient = nullptr;
    }

    DllRelease();
}

//+---------------------------------------------------------------------------
//
// QueryInterface
//
//----------------------------------------------------------------------------

STDAPI CSampleIME::QueryInterface(REFIID riid, _Outptr_ void **ppvObj)
{
    if (ppvObj == nullptr)
    {
        return E_INVALIDARG;
    }

    *ppvObj = nullptr;

    if (IsEqualIID(riid, IID_IUnknown) ||
        IsEqualIID(riid, IID_ITfTextInputProcessor))
    {
        *ppvObj = (ITfTextInputProcessor *)this;
    }
    else if (IsEqualIID(riid, IID_ITfTextInputProcessorEx))
    {
         *ppvObj = (ITfTextInputProcessorEx *)this;
    }
    else if (IsEqualIID(riid, IID_ITfThreadMgrEventSink))
    {
        *ppvObj = (ITfThreadMgrEventSink *)this;
    }
    else if (IsEqualIID(riid, IID_ITfKeyEventSink))
    {
        *ppvObj = (ITfKeyEventSink *)this;
    }
    else if (IsEqualIID(riid, IID_ITfDisplayAttributeProvider))
    {
        *ppvObj = (ITfDisplayAttributeProvider *)this;
    }
    else if (IsEqualIID(riid, IID_ITfFunctionProvider))
    {
        *ppvObj = (ITfFunctionProvider *)this;
    }
    else if (IsEqualIID(riid, IID_ITfFunction))
    {
        *ppvObj = static_cast<ITfFunction *>(static_cast<ITfFnGetPreferredTouchKeyboardLayout *>(this));
    }
    else if (IsEqualIID(riid, IID_ITfFnShowHelp))
    {
        *ppvObj = (ITfFnShowHelp *)this;
    }
    else if (IsEqualIID(riid, IID_ITfFnSearchCandidateProvider))
    {
        *ppvObj = (ITfFnSearchCandidateProvider *)this;
    }
    else if (IsEqualIID(riid, IID_ITfFnGetPreferredTouchKeyboardLayout))
    {
        *ppvObj = (ITfFnGetPreferredTouchKeyboardLayout *)this;
    }
    else if (IsEqualIID(riid, IID_ITfActiveLanguageProfileNotifySink))
    {
        *ppvObj = (ITfActiveLanguageProfileNotifySink *)this;
    }

    if (*ppvObj)
    {
        AddRef();
        return S_OK;
    }

    return E_NOINTERFACE;
}

//+---------------------------------------------------------------------------
//
// AddRef
//
//----------------------------------------------------------------------------

STDAPI_(ULONG) CSampleIME::AddRef()
{
    return ++_refCount;
}

//+---------------------------------------------------------------------------
//
// Release
//
//----------------------------------------------------------------------------

STDAPI_(ULONG) CSampleIME::Release()
{
    LONG cr = --_refCount;

    assert(_refCount >= 0);

    if (_refCount == 0)
    {
        delete this;
    }

    return cr;
}

//+---------------------------------------------------------------------------
//扩展文本输入处理器
// ITfTextInputProcessorEx::ActivateEx
//dwFlags标示了激活模式
//----------------------------------------------------------------------------

STDAPI CSampleIME::ActivateEx(ITfThreadMgr *pThreadMgr, TfClientId tfClientId, DWORD dwFlags)
{
    _pThreadMgr = pThreadMgr;
    _pThreadMgr->AddRef();

    _tfClientId = tfClientId;
    _dwActivateFlags = dwFlags;
    _lastCoreLaunchAttemptTick = 0;
    _coreLaunchWorkerRunning = 0;
    _lastFocusHwnd = 0;
    _lastFocusProcessId = 0;
    _lastFocusSentTick = 0;
    _pendingFocusHwnd = 0;
    _pendingFocusProcessId = 0;
    _focusQueryPending = FALSE;
    _profileActive = FALSE;
    _lastImeActiveSent = FALSE;
    _hasSentImeActive = FALSE;
    _imeActivePublishRetryCount = 0;

    if (!_InitCaretCoalesceWindow())
    {
        Global::LogToFile("ActivateEx: _InitCaretCoalesceWindow failed");
        goto ExitError;
    }

    _activateTick = GetTickCount64();
    _isDevenvHost = FALSE;
    WCHAR exePath[MAX_PATH] = {};
    DWORD exePathLen = GetModuleFileNameW(nullptr, exePath, ARRAYSIZE(exePath));
    if (exePathLen > 0)
    {
        const WCHAR* fileName = wcsrchr(exePath, L'\\');
        fileName = (fileName == nullptr) ? exePath : (fileName + 1);
        _isDevenvHost = (_wcsicmp(fileName, L"devenv.exe") == 0) ? TRUE : FALSE;
    }
    WCHAR dllPath[MAX_PATH] = {};
    DWORD dllPathLen = GetModuleFileNameW(Global::dllInstanceHandle, dllPath, ARRAYSIZE(dllPath));
    Global::LogToFileVerbose("ActivateEx: protocol=%d exe=%ls dll=%ls",
                             BIME_PROTOCOL_VERSION,
                             (exePathLen > 0) ? exePath : L"<unknown>",
                             (dllPathLen > 0) ? dllPath : L"<unknown>");
    Global::LogToFileVerbose("ActivateEx: hostIsDevenv=%d activateTick=%llu", _isDevenvHost, _activateTick);
    Global::LogToFileVerbose("ActivateEx: dwFlags=0x%08X secure=%d comless=%d store=%d",
                             static_cast<unsigned>(_dwActivateFlags),
                             _IsSecureMode(),
                             _IsComLess(),
                             _IsStoreAppMode());
    _RefreshImmersiveState("ActivateEx", GetForegroundWindow());

    if (!_InitThreadMgrEventSink())
    {
        Global::LogToFile("ActivateEx: _InitThreadMgrEventSink failed");
        goto ExitError;
    }

    // Bridge mode: no ITfTextEditSink registration.

    if (!_InitKeyEventSink())
    {
        Global::LogToFile("ActivateEx: _InitKeyEventSink failed");
        goto ExitError;
    }

    _InitCtrlSpacePreservedKey();
    // Status window activation: enable the active-language-profile sink (report activation only; no legacy composition).
    _InitActiveLanguageProfileNotifySink();

    if (!_InitFunctionProviderSink())
    {
        Global::LogToFile("ActivateEx: _InitFunctionProviderSink failed");
        goto ExitError;
    }

    if (!_InitBridgeLanguageBar())
    {
        Global::LogToFile("ActivateEx: _InitBridgeLanguageBar failed");
        goto ExitError;
    }

    // Publish activation now: switching back to this IME always runs ActivateEx, but the self
    // OnActivated that triggered it is usually missed (sink advised too late), so push state here.
    _PublishImeActive();

    return S_OK;

ExitError:
    Global::LogToFile("ActivateEx: failed");
    Deactivate();
    return E_FAIL;
}

//+---------------------------------------------------------------------------
//
// ITfTextInputProcessorEx::Deactivate
//
//----------------------------------------------------------------------------

STDAPI CSampleIME::Deactivate()
{
    _ClearDeferredCaretAnchorReopen();

    _UninitFunctionProviderSink();
    // Status window activation: unadvise the active-language-profile sink (must run before _pThreadMgr is released).
    _UninitActiveLanguageProfileNotifySink();

    _UninitKeyEventSink();

    _UninitCtrlSpacePreservedKey();

    _UninitBridgeLanguageBar();

    _UninitCaretCoalesceWindow();

    _EndCaretAnchorComposition(nullptr);
    if (_pCaretAnchorCompositionSink != nullptr)
    {
        _pCaretAnchorCompositionSink->Release();
        _pCaretAnchorCompositionSink = nullptr;
    }

    _UninitThreadMgrEventSink();

    CCompartment CompartmentKeyboardOpen(_pThreadMgr, _tfClientId, GUID_COMPARTMENT_KEYBOARD_OPENCLOSE);
    CompartmentKeyboardOpen._ClearCompartment();

    CCompartment CompartmentDoubleSingleByte(_pThreadMgr, _tfClientId, Global::SampleIMEGuidCompartmentDoubleSingleByte);
    CompartmentDoubleSingleByte._ClearCompartment();

    CCompartment CompartmentPunctuation(_pThreadMgr, _tfClientId, Global::SampleIMEGuidCompartmentPunctuation);
    CompartmentPunctuation._ClearCompartment();

    if (_pThreadMgr != nullptr)
    {
        _pThreadMgr->Release();
    }

    _tfClientId = TF_CLIENTID_NULL;

    if (_pDocMgrLastFocused)
    {
        _pDocMgrLastFocused->Release();
        _pDocMgrLastFocused = nullptr;
    }

    // On deactivate/teardown, tell Core to hide the status window (must send before Disconnect).
    _CancelImeActivePublishRetry();
    _profileActive = FALSE;
    if (_pPipeClient && _pPipeClient->IsConnected())
    {
        _pPipeClient->SendImeActiveMessage(FALSE);
        _lastImeActiveSent = FALSE;
        _hasSentImeActive = TRUE;
    }

    if (_pPipeClient)
    {
        _pPipeClient->Disconnect();
    }

    _activateTick = 0;
    _isDevenvHost = FALSE;
    _ctrlSpacePreservedKeyRegistered = FALSE;
    _keySinkUseForeground = TRUE;
    _isImmersiveSession = FALSE;
    _lastCoreLaunchAttemptTick = 0;
    _coreLaunchWorkerRunning = 0;
    _lastFocusHwnd = 0;
    _lastFocusProcessId = 0;
    _lastFocusSentTick = 0;
    _pendingFocusHwnd = 0;
    _pendingFocusProcessId = 0;
    _focusQueryPending = FALSE;

    _ResetCaretCoalesceState();

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
    _keyUpForwardBudget = 0;
    _CancelCompositionRefresh();

    return S_OK;
}

//+---------------------------------------------------------------------------
//扩展功能提供者
// ITfFunctionProvider::GetType
//
//----------------------------------------------------------------------------
class CCaretAnchorCompositionSink : public ITfCompositionSink
{
public:
    explicit CCaretAnchorCompositionSink(_In_ CSampleIME *pTextService)
    {
        _refCount = 1;
        _pTextService = pTextService;
        if (_pTextService != nullptr)
        {
            _pTextService->AddRef();
        }
    }

    ~CCaretAnchorCompositionSink()
    {
        if (_pTextService != nullptr)
        {
            _pTextService->Release();
            _pTextService = nullptr;
        }
    }

    STDMETHODIMP QueryInterface(REFIID riid, _Outptr_ void **ppvObj) override
    {
        if (ppvObj == nullptr)
        {
            return E_INVALIDARG;
        }

        *ppvObj = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfCompositionSink))
        {
            *ppvObj = static_cast<ITfCompositionSink *>(this);
        }

        if (*ppvObj != nullptr)
        {
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef(void) override
    {
        return static_cast<ULONG>(InterlockedIncrement(&_refCount));
    }

    STDMETHODIMP_(ULONG) Release(void) override
    {
        LONG cr = InterlockedDecrement(&_refCount);
        assert(cr >= 0);
        if (cr == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(cr);
    }

    STDMETHODIMP OnCompositionTerminated(TfEditCookie ecWrite, _In_ ITfComposition *pComposition) override
    {
        ecWrite;
        if (_pTextService != nullptr)
        {
            _pTextService->_OnCaretAnchorCompositionExternallyTerminated(pComposition);
        }
        return S_OK;
    }

private:
    LONG _refCount;
    CSampleIME *_pTextService;
};

class CStartCaretAnchorEditSession : public CEditSessionBase
{
public:
    CStartCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext)
        : CEditSessionBase(pTextService, pContext)
    {
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pTextService->_pCaretAnchorComposition != nullptr && _pTextService->_pCaretAnchorContext == _pContext)
        {
            return S_OK;
        }

        ITfInsertAtSelection *pInsertAtSelection = nullptr;
        ITfContextComposition *pContextComposition = nullptr;
        ITfRange *pInsertionRange = nullptr;
        HRESULT hr = _pContext->QueryInterface(IID_ITfContextComposition, (void **)&pContextComposition);
        if (FAILED(hr) || pContextComposition == nullptr)
        {
            return S_OK;
        }

        hr = _pContext->QueryInterface(IID_ITfInsertAtSelection, (void **)&pInsertAtSelection);
        if (SUCCEEDED(hr) && pInsertAtSelection != nullptr)
        {
            hr = pInsertAtSelection->InsertTextAtSelection(ec, TF_IAS_QUERYONLY, nullptr, 0, &pInsertionRange);
        }

        if (pInsertionRange == nullptr)
        {
            TF_SELECTION selection = {};
            ULONG fetched = 0;
            hr = _pContext->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
            if (FAILED(hr) || fetched != 1 || selection.range == nullptr)
            {
                if (pInsertAtSelection != nullptr)
                {
                    pInsertAtSelection->Release();
                }
                pContextComposition->Release();
                return S_OK;
            }

            pInsertionRange = selection.range;
        }

        if (pInsertAtSelection != nullptr)
        {
            pInsertAtSelection->Release();
        }

        if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
        {
            _pTextService->_pCaretAnchorCompositionSink = new (std::nothrow) CCaretAnchorCompositionSink(_pTextService);
            if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
            {
                pInsertionRange->Release();
                pContextComposition->Release();
                return E_OUTOFMEMORY;
            }
        }

        ITfComposition *pComposition = nullptr;
        hr = pContextComposition->StartComposition(ec, pInsertionRange, _pTextService->_pCaretAnchorCompositionSink, &pComposition);
        if (SUCCEEDED(hr))
        {
            TF_SELECTION selection = {};
            selection.range = pInsertionRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = FALSE;
            _pContext->SetSelection(ec, 1, &selection);
        }
        pInsertionRange->Release();
        pContextComposition->Release();

        if (FAILED(hr) || pComposition == nullptr)
        {
            Global::LogToFileVerbose("CaretAnchor: StartComposition failed hr=0x%08X", static_cast<unsigned>(hr));
            return S_OK;
        }

        _pTextService->_pCaretAnchorComposition = pComposition;
        _pTextService->_pCaretAnchorContext = _pContext;
        _pTextService->_pCaretAnchorContext->AddRef();
        Global::LogToFileVerbose("CaretAnchor: started ctx=%p", _pContext);
        return S_OK;
    }
};

class CStartInitialCaretAnchorEditSession : public CEditSessionBase
{
public:
    CStartInitialCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ const WCHAR *pText)
        : CEditSessionBase(pTextService, pContext)
    {
        if (pText != nullptr)
        {
            _text.assign(pText);
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_text.empty())
        {
            return S_FALSE;
        }

        if (_pTextService->_pCaretAnchorComposition != nullptr && _pTextService->_pCaretAnchorContext == _pContext)
        {
            return S_OK;
        }

        ITfInsertAtSelection *pInsertAtSelection = nullptr;
        ITfContextComposition *pContextComposition = nullptr;
        ITfRange *pInsertionRange = nullptr;

        HRESULT hr = _pContext->QueryInterface(IID_ITfInsertAtSelection, (void **)&pInsertAtSelection);
        if (FAILED(hr) || pInsertAtSelection == nullptr)
        {
            return S_OK;
        }

        hr = pInsertAtSelection->InsertTextAtSelection(ec, TF_IAS_QUERYONLY, _text.c_str(), static_cast<LONG>(_text.length()), &pInsertionRange);
        pInsertAtSelection->Release();
        if (FAILED(hr) || pInsertionRange == nullptr)
        {
            return S_OK;
        }

        hr = _pContext->QueryInterface(IID_ITfContextComposition, (void **)&pContextComposition);
        if (FAILED(hr) || pContextComposition == nullptr)
        {
            pInsertionRange->Release();
            return S_OK;
        }

        if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
        {
            _pTextService->_pCaretAnchorCompositionSink = new (std::nothrow) CCaretAnchorCompositionSink(_pTextService);
            if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
            {
                pInsertionRange->Release();
                pContextComposition->Release();
                return E_OUTOFMEMORY;
            }
        }

        ITfComposition *pComposition = nullptr;
        hr = pContextComposition->StartComposition(ec, pInsertionRange, _pTextService->_pCaretAnchorCompositionSink, &pComposition);
        pContextComposition->Release();
        if (FAILED(hr) || pComposition == nullptr)
        {
            pInsertionRange->Release();
            Global::LogToFileVerbose("CaretAnchor: StartInitialComposition failed hr=0x%08X", static_cast<unsigned>(hr));
            return S_OK;
        }

        _pTextService->_pCaretAnchorComposition = pComposition;
        _pTextService->_pCaretAnchorContext = _pContext;
        _pTextService->_pCaretAnchorContext->AddRef();

        hr = pInsertionRange->SetText(ec, 0, _text.c_str(), static_cast<LONG>(_text.length()));
        if (SUCCEEDED(hr))
        {
            ITfRange *pDisplayRange = nullptr;
            if (SUCCEEDED(pInsertionRange->Clone(&pDisplayRange)) && pDisplayRange != nullptr)
            {
                ApplyInputDisplayAttribute(_pContext, ec, pDisplayRange);
                pDisplayRange->Release();
            }

            pInsertionRange->Collapse(ec, TF_ANCHOR_END);
            TF_SELECTION selection = {};
            selection.range = pInsertionRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = TRUE;
            _pContext->SetSelection(ec, 1, &selection);
            _pTextService->_lastAnchorInputBuffer = _text;
        }

        pInsertionRange->Release();
        Global::LogToFileVerbose("CaretAnchor: started_initial ctx=%p len=%u hr=0x%08X", _pContext, static_cast<unsigned>(_text.length()), static_cast<unsigned>(hr));
        return hr;
    }

private:
    std::wstring _text;
};

class CEndCaretAnchorEditSession : public CEditSessionBase
{
public:
    CEndCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfComposition *pComposition)
        : CEditSessionBase(pTextService, pContext)
    {
        _pComposition = pComposition;
        if (_pComposition != nullptr)
        {
            _pComposition->AddRef();
        }
    }

    ~CEndCaretAnchorEditSession() override
    {
        if (_pComposition != nullptr)
        {
            _pComposition->Release();
            _pComposition = nullptr;
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pComposition == nullptr)
        {
            return S_OK;
        }

        HRESULT hr = _pComposition->EndComposition(ec);
        if (FAILED(hr))
        {
            Global::LogToFileVerbose("CaretAnchor: EndComposition failed hr=0x%08X", static_cast<unsigned>(hr));
        }
        else
        {
            Global::LogToFileVerbose("CaretAnchor: ended");
        }
        return S_OK;
    }

private:
    ITfComposition *_pComposition = nullptr;
};

class CUpdateCaretAnchorEditSession : public CEditSessionBase
{
public:
    CUpdateCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ const WCHAR *pText)
        : CEditSessionBase(pTextService, pContext)
    {
        if (pText != nullptr)
        {
            _text.assign(pText);
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        ITfRange *pRange = nullptr;
        if (!_pTextService->_GetCaretAnchorRange(&pRange))
        {
            return S_FALSE;
        }

        HRESULT hr = pRange->SetText(ec, 0, _text.c_str(), static_cast<LONG>(_text.length()));
        if (SUCCEEDED(hr))
        {
            ITfRange *pDisplayRange = nullptr;
            if (SUCCEEDED(pRange->Clone(&pDisplayRange)) && pDisplayRange != nullptr)
            {
                ApplyInputDisplayAttribute(_pContext, ec, pDisplayRange);
                pDisplayRange->Release();
            }

            pRange->Collapse(ec, TF_ANCHOR_END);
            TF_SELECTION selection = {};
            selection.range = pRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = TRUE;
            _pContext->SetSelection(ec, 1, &selection);

            ITfContextView *pContextView = nullptr;
            if (SUCCEEDED(_pContext->GetActiveView(&pContextView)) && pContextView != nullptr)
            {
                RECT rc = {0, 0, 0, 0};
                BOOL isClipped = TRUE;
                if (SUCCEEDED(pContextView->GetTextExt(ec, pRange, &rc, &isClipped)))
                {
                    LONG width = rc.right - rc.left;
                    LONG height = rc.bottom - rc.top;
                    if (width <= 0)
                    {
                        width = 2;
                    }
                    if (height <= 0)
                    {
                        height = 20;
                    }
                    Global::LogToFileVerbose("CaretAnchor: update_text x=%ld y=%ld w=%ld h=%ld len=%u", rc.left, rc.bottom, width, height, static_cast<unsigned>(_text.length()));
                }
                pContextView->Release();
            }
        }

        pRange->Release();
        return hr;
    }

private:
    std::wstring _text;
};

class CDeferredReopenCaretAnchorEditSession : public CEditSessionBase
{
public:
    CDeferredReopenCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ const WCHAR *pText)
        : CEditSessionBase(pTextService, pContext)
    {
        if (pText != nullptr)
        {
            _text.assign(pText);
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_text.empty())
        {
            return S_OK;
        }

        if (_pTextService->_pCaretAnchorComposition == nullptr || _pTextService->_pCaretAnchorContext != _pContext)
        {
            ITfContextComposition *pContextComposition = nullptr;
            HRESULT hr = _pContext->QueryInterface(IID_ITfContextComposition, (void **)&pContextComposition);
            if (FAILED(hr) || pContextComposition == nullptr)
            {
                return S_FALSE;
            }

            TF_SELECTION selection = {};
            ULONG fetched = 0;
            hr = _pContext->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched);
            if (FAILED(hr) || fetched != 1 || selection.range == nullptr)
            {
                pContextComposition->Release();
                return S_FALSE;
            }

            if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
            {
                _pTextService->_pCaretAnchorCompositionSink = new (std::nothrow) CCaretAnchorCompositionSink(_pTextService);
                if (_pTextService->_pCaretAnchorCompositionSink == nullptr)
                {
                    selection.range->Release();
                    pContextComposition->Release();
                    return E_OUTOFMEMORY;
                }
            }

            ITfComposition *pComposition = nullptr;
            hr = pContextComposition->StartComposition(ec, selection.range, _pTextService->_pCaretAnchorCompositionSink, &pComposition);
            selection.range->Release();
            pContextComposition->Release();

            if (FAILED(hr) || pComposition == nullptr)
            {
                Global::LogToFileVerbose("CaretAnchor: deferred StartComposition failed hr=0x%08X", static_cast<unsigned>(hr));
                return S_FALSE;
            }

            if (_pTextService->_pCaretAnchorComposition != nullptr)
            {
                _pTextService->_pCaretAnchorComposition->Release();
                _pTextService->_pCaretAnchorComposition = nullptr;
            }
            if (_pTextService->_pCaretAnchorContext != nullptr)
            {
                _pTextService->_pCaretAnchorContext->Release();
                _pTextService->_pCaretAnchorContext = nullptr;
            }

            _pTextService->_pCaretAnchorComposition = pComposition;
            _pTextService->_pCaretAnchorContext = _pContext;
            _pTextService->_pCaretAnchorContext->AddRef();
            Global::LogToFileVerbose("CaretAnchor: deferred started ctx=%p", _pContext);
        }

        ITfRange *pRange = nullptr;
        if (!_pTextService->_GetCaretAnchorRange(&pRange))
        {
            return S_FALSE;
        }

        HRESULT hr = pRange->SetText(ec, 0, _text.c_str(), static_cast<LONG>(_text.length()));
        if (SUCCEEDED(hr))
        {
            ITfRange *pDisplayRange = nullptr;
            if (SUCCEEDED(pRange->Clone(&pDisplayRange)) && pDisplayRange != nullptr)
            {
                ApplyInputDisplayAttribute(_pContext, ec, pDisplayRange);
                pDisplayRange->Release();
            }

            pRange->Collapse(ec, TF_ANCHOR_END);
            TF_SELECTION selection = {};
            selection.range = pRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = TRUE;
            _pContext->SetSelection(ec, 1, &selection);

            ITfContextView *pContextView = nullptr;
            if (SUCCEEDED(_pContext->GetActiveView(&pContextView)) && pContextView != nullptr)
            {
                RECT rc = {0, 0, 0, 0};
                BOOL isClipped = TRUE;
                if (SUCCEEDED(pContextView->GetTextExt(ec, pRange, &rc, &isClipped)))
                {
                    LONG width = rc.right - rc.left;
                    LONG height = rc.bottom - rc.top;
                    if (width <= 0)
                    {
                        width = 2;
                    }
                    if (height <= 0)
                    {
                        height = 20;
                    }
                    Global::LogToFileVerbose("CaretAnchor: deferred update_text x=%ld y=%ld w=%ld h=%ld len=%u", rc.left, rc.bottom, width, height, static_cast<unsigned>(_text.length()));
                }
                pContextView->Release();
            }
        }

        _pTextService->_ScheduleCaretTrackingPrime();

        pRange->Release();
        return hr;
    }

private:
    std::wstring _text;
};

class CAnchorPrimeTextExtentEditSession : public CEditSessionBase
{
public:
    CAnchorPrimeTextExtentEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfContextView *pContextView)
        : CEditSessionBase(pTextService, pContext)
    {
        _pContextView = pContextView;
        if (_pContextView != nullptr)
        {
            _pContextView->AddRef();
        }
    }

    ~CAnchorPrimeTextExtentEditSession() override
    {
        if (_pContextView != nullptr)
        {
            _pContextView->Release();
            _pContextView = nullptr;
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pContextView == nullptr)
        {
            return S_OK;
        }

        ITfRange *pRange = nullptr;
        if (!_pTextService->_GetCaretAnchorRange(&pRange))
        {
            return S_OK;
        }

        RECT rc = {0, 0, 0, 0};
        BOOL isClipped = TRUE;
        HRESULT hr = _pContextView->GetTextExt(ec, pRange, &rc, &isClipped);
        pRange->Release();

        if (SUCCEEDED(hr))
        {
            LONG width = rc.right - rc.left;
            LONG height = rc.bottom - rc.top;
            if (width <= 0)
            {
                width = 2;
            }
            if (height <= 0)
            {
                height = 20;
            }

            _pTextService->_SendCaretMessage(rc.left, rc.bottom, width, height, CARET_SOURCE_ANCHOR);
            Global::LogToFileVerbose("CaretTrack: anchor_prime x=%ld y=%ld w=%ld h=%ld", rc.left, rc.bottom, width, height);
        }

        return S_OK;
    }

private:
    ITfContextView *_pContextView = nullptr;
};

class CCancelCaretAnchorEditSession : public CEditSessionBase
{
public:
    CCancelCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfComposition *pComposition)
        : CEditSessionBase(pTextService, pContext)
    {
        _pComposition = pComposition;
        if (_pComposition != nullptr)
        {
            _pComposition->AddRef();
        }
    }

    ~CCancelCaretAnchorEditSession() override
    {
        if (_pComposition != nullptr)
        {
            _pComposition->Release();
            _pComposition = nullptr;
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pComposition == nullptr)
        {
            return S_FALSE;
        }

        ITfRange *pRange = nullptr;
        HRESULT hr = _pComposition->GetRange(&pRange);
        if (FAILED(hr) || pRange == nullptr)
        {
            return FAILED(hr) ? hr : S_FALSE;
        }

        hr = pRange->SetText(ec, 0, L"", 0);
        if (SUCCEEDED(hr))
        {
            ClearRangeDisplayAttribute(_pContext, ec, pRange);
            pRange->Collapse(ec, TF_ANCHOR_END);

            TF_SELECTION selection = {};
            selection.range = pRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = FALSE;
            _pContext->SetSelection(ec, 1, &selection);

            hr = _pComposition->EndComposition(ec);
            if (FAILED(hr))
            {
                Global::LogToFileVerbose("CaretAnchor: cancel EndComposition failed hr=0x%08X", static_cast<unsigned>(hr));
            }
        }

        _pTextService->_ScheduleCaretTrackingPrime();

        pRange->Release();
        return hr;
    }

private:
    ITfComposition *_pComposition = nullptr;
};

class CClearTerminatedCaretAnchorEditSession : public CEditSessionBase
{
public:
    CClearTerminatedCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfComposition *pComposition)
        : CEditSessionBase(pTextService, pContext)
    {
        _pComposition = pComposition;
        if (_pComposition != nullptr)
        {
            _pComposition->AddRef();
        }
    }

    ~CClearTerminatedCaretAnchorEditSession() override
    {
        if (_pComposition != nullptr)
        {
            _pComposition->Release();
            _pComposition = nullptr;
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pComposition == nullptr)
        {
            return S_FALSE;
        }

        ITfRange *pRange = nullptr;
        HRESULT hr = _pComposition->GetRange(&pRange);
        if (FAILED(hr) || pRange == nullptr)
        {
            return FAILED(hr) ? hr : S_FALSE;
        }

        hr = pRange->SetText(ec, 0, L"", 0);
        if (SUCCEEDED(hr))
        {
            ClearRangeDisplayAttribute(_pContext, ec, pRange);
        }

        pRange->Release();
        return hr;
    }

private:
    ITfComposition *_pComposition = nullptr;
};

HRESULT CSampleIME::_StartCaretAnchorCompositionAtInsertionRange(_In_ ITfContext *pContext)
{
    if (pContext == nullptr)
    {
        return E_INVALIDARG;
    }

    if (_pCaretAnchorComposition != nullptr && _pCaretAnchorContext == pContext)
    {
        return S_OK;
    }

    _EndCaretAnchorComposition(nullptr);

    _StartCaretTrackingOnContext(pContext);

    CStartCaretAnchorEditSession *pEditSession = new (std::nothrow) CStartCaretAnchorEditSession(this, pContext);
    if (pEditSession == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);

    pEditSession->Release();

    if (FAILED(hr))
    {
        return hr;
    }

    if (hrSession == S_OK)
    {
        _ScheduleCaretTrackingPrime();
    }

    return (hrSession == S_OK) ? S_OK : S_FALSE;
}

HRESULT CSampleIME::_EnsureCaretAnchorComposition(_In_ ITfContext *pContext)
{
    return _StartCaretAnchorCompositionAtInsertionRange(pContext);
}

void CSampleIME::_ScheduleCaretTrackingPrime()
{
    if (_msgWndHandle == nullptr || _caretTrackingPrimePending)
    {
        return;
    }

    _caretTrackingPrimePending = TRUE;
    if (!PostMessageW(_msgWndHandle, WM_PrimeCaretTrackingFromAnchor, 0, 0))
    {
        _caretTrackingPrimePending = FALSE;
    }
}

void CSampleIME::_PrimeCaretTrackingFromAnchor()
{
    if (_pCaretAnchorContext == nullptr)
    {
        return;
    }

    _StartCaretTrackingOnContext(_pCaretAnchorContext);

    ITfContextView *pContextView = nullptr;
    if (SUCCEEDED(_pCaretAnchorContext->GetActiveView(&pContextView)) && pContextView != nullptr)
    {
        CAnchorPrimeTextExtentEditSession *pEditSession = new (std::nothrow) CAnchorPrimeTextExtentEditSession(this, _pCaretAnchorContext, pContextView);
        if (pEditSession != nullptr)
        {
            HRESULT hrSession = S_OK;
            HRESULT hr = _pCaretAnchorContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_ASYNCDONTCARE | TF_ES_READ, &hrSession);
            if (FAILED(hr))
            {
                Global::LogToFileVerbose("CaretTrack: prime RequestEditSession failed hr=0x%08X session=0x%08X", static_cast<unsigned>(hr), static_cast<unsigned>(hrSession));
            }
            pEditSession->Release();
        }
        pContextView->Release();
    }
}

void CSampleIME::_EndCaretAnchorComposition(_In_opt_ ITfContext *pContext)
{
    ITfComposition *pComposition = _pCaretAnchorComposition;
    ITfContext *pTargetContext = (pContext != nullptr) ? pContext : _pCaretAnchorContext;
    _lastAnchorInputBuffer.clear();

    if (pComposition != nullptr)
    {
        pComposition->AddRef();
    }
    if (pTargetContext != nullptr)
    {
        pTargetContext->AddRef();
    }

    if (_pCaretAnchorComposition != nullptr)
    {
        _pCaretAnchorComposition->Release();
        _pCaretAnchorComposition = nullptr;
    }
    if (_pCaretAnchorContext != nullptr)
    {
        _pCaretAnchorContext->Release();
        _pCaretAnchorContext = nullptr;
    }

    if (pComposition != nullptr && pTargetContext != nullptr)
    {
        _suppressExternalCompositionCanceledNotify = TRUE;
        CEndCaretAnchorEditSession *pEditSession = new (std::nothrow) CEndCaretAnchorEditSession(this, pTargetContext, pComposition);
        if (pEditSession != nullptr)
        {
            HRESULT hrSession = S_OK;
            HRESULT hr = pTargetContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);
            if (FAILED(hr) || hrSession != S_OK)
            {
                Global::LogToFileVerbose("CaretAnchor: RequestEditSession(end) failed hr=0x%08X session=0x%08X", static_cast<unsigned>(hr), static_cast<unsigned>(hrSession));
            }
            pEditSession->Release();
        }
        _suppressExternalCompositionCanceledNotify = FALSE;
    }

    if (pComposition != nullptr)
    {
        pComposition->Release();
    }
    if (pTargetContext != nullptr)
    {
        pTargetContext->Release();
    }
}

void CSampleIME::_ClearDeferredCaretAnchorReopen()
{
    _deferredReopenInputBuffer.clear();
    if (_pDeferredReopenContext != nullptr)
    {
        _pDeferredReopenContext->Release();
        _pDeferredReopenContext = nullptr;
    }
}

BOOL CSampleIME::_ScheduleDeferredCaretAnchorReopen(_In_ ITfContext *pContext, _In_ const std::wstring &inputBuffer)
{
    if (pContext == nullptr || inputBuffer.empty())
    {
        return FALSE;
    }

    if (_msgWndHandle == nullptr)
    {
        return FALSE;
    }

    _ClearDeferredCaretAnchorReopen();
    _pDeferredReopenContext = pContext;
    _pDeferredReopenContext->AddRef();
    _deferredReopenInputBuffer = inputBuffer;

    if (!PostMessageW(_msgWndHandle, WM_DeferredReopenCaretAnchorComposition, 0, 0))
    {
        _ClearDeferredCaretAnchorReopen();
        return FALSE;
    }

    return TRUE;
}

void CSampleIME::_HandleDeferredCaretAnchorReopen()
{
    ITfContext *pContext = _pDeferredReopenContext;
    std::wstring inputBuffer = _deferredReopenInputBuffer;
    _pDeferredReopenContext = nullptr;
    _deferredReopenInputBuffer.clear();

    if (pContext == nullptr || inputBuffer.empty())
    {
        if (pContext != nullptr)
        {
            pContext->Release();
        }
        return;
    }

    if (_pThreadMgr == nullptr)
    {
        pContext->Release();
        return;
    }

    CDeferredReopenCaretAnchorEditSession *pEditSession = new (std::nothrow) CDeferredReopenCaretAnchorEditSession(this, pContext, inputBuffer.c_str());
    if (pEditSession == nullptr)
    {
        pContext->Release();
        return;
    }

    HRESULT hrSession = S_OK;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_ASYNCDONTCARE | TF_ES_READWRITE, &hrSession);
    pEditSession->Release();

    if (SUCCEEDED(hr))
    {
        _lastAnchorInputBuffer = inputBuffer;
    }
    else
    {
        Global::LogToFileVerbose("CaretAnchor: deferred RequestEditSession failed hr=0x%08X session=0x%08X", static_cast<unsigned>(hr), static_cast<unsigned>(hrSession));
    }

    pContext->Release();
}

HRESULT CSampleIME::_CancelCaretAnchorComposition(_In_ ITfContext *pContext)
{
    if (pContext == nullptr)
    {
        return E_INVALIDARG;
    }

    HRESULT hrEnsure = _EnsureCaretAnchorComposition(pContext);
    if (FAILED(hrEnsure))
    {
        return hrEnsure;
    }

    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext != pContext)
    {
        return E_FAIL;
    }

    ITfComposition *pComposition = _pCaretAnchorComposition;
    pComposition->AddRef();
    _lastAnchorInputBuffer.clear();

    CCancelCaretAnchorEditSession *pEditSession = new (std::nothrow) CCancelCaretAnchorEditSession(this, pContext, pComposition);
    if (pEditSession == nullptr)
    {
        pComposition->Release();
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    _suppressExternalCompositionCanceledNotify = TRUE;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);
    _suppressExternalCompositionCanceledNotify = FALSE;
    pEditSession->Release();

    if (FAILED(hr))
    {
        pComposition->Release();
        return hr;
    }

    if (hrSession == S_OK)
    {
        _OnCaretAnchorCompositionTerminated(pComposition);
    }

    pComposition->Release();
    return hrSession;
}

void CSampleIME::_OnCaretAnchorCompositionTerminated(_In_opt_ ITfComposition *pComposition)
{
    if (_pCaretAnchorComposition == nullptr)
    {
        return;
    }

    if (pComposition != nullptr && pComposition != _pCaretAnchorComposition)
    {
        return;
    }

    Global::LogToFileVerbose("CaretAnchor: terminated");

    _pCaretAnchorComposition->Release();
    _pCaretAnchorComposition = nullptr;
    _lastAnchorInputBuffer.clear();

    if (_pCaretAnchorContext != nullptr)
    {
        _pCaretAnchorContext->Release();
        _pCaretAnchorContext = nullptr;
    }
}

void CSampleIME::_OnCaretAnchorCompositionExternallyTerminated(_In_opt_ ITfComposition *pComposition)
{
    const BOOL shouldNotifyCore = !_suppressExternalCompositionCanceledNotify;
    if (shouldNotifyCore && _pCaretAnchorContext != nullptr && pComposition != nullptr)
    {
        HRESULT hrClear = _ClearCaretAnchorTextForExternallyTerminatedComposition(_pCaretAnchorContext, pComposition);
        Global::LogToFileVerbose("CaretAnchor: external_terminated clear_text hr=0x%08X", static_cast<unsigned>(hrClear));
    }

    _OnCaretAnchorCompositionTerminated(pComposition);

    if (shouldNotifyCore)
    {
        Global::LogToFileVerbose("CaretAnchor: external_terminated notify_core=1");
        _SendCompositionCanceledMessage();
    }
    else
    {
        Global::LogToFileVerbose("CaretAnchor: external_terminated notify_core=0");
    }
}

HRESULT CSampleIME::_ClearCaretAnchorTextForExternallyTerminatedComposition(_In_ ITfContext *pContext, _In_ ITfComposition *pComposition)
{
    if (pContext == nullptr || pComposition == nullptr)
    {
        return E_INVALIDARG;
    }

    CClearTerminatedCaretAnchorEditSession *pEditSession = new (std::nothrow) CClearTerminatedCaretAnchorEditSession(this, pContext, pComposition);
    if (pEditSession == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);
    pEditSession->Release();

    if (FAILED(hr))
    {
        return hr;
    }

    return hrSession;
}

BOOL CSampleIME::_GetCaretAnchorRange(_Outptr_result_maybenull_ ITfRange **ppRange)
{
    if (ppRange == nullptr)
    {
        return FALSE;
    }

    *ppRange = nullptr;
    if (_pCaretAnchorComposition == nullptr)
    {
        return FALSE;
    }

    ITfRange *pRange = nullptr;
    HRESULT hr = _pCaretAnchorComposition->GetRange(&pRange);
    if (FAILED(hr) || pRange == nullptr)
    {
        Global::LogToFileVerbose("CaretAnchor: GetRange failed hr=0x%08X", static_cast<unsigned>(hr));
        return FALSE;
    }

    *ppRange = pRange;
    return TRUE;
}

HRESULT CSampleIME::_UpdateCaretAnchorText(_In_ ITfContext *pContext, _In_ const WCHAR *pText)
{
    if (pContext == nullptr)
    {
        return E_INVALIDARG;
    }

    HRESULT hr = _EnsureCaretAnchorComposition(pContext);
    if (FAILED(hr))
    {
        return hr;
    }

    const WCHAR *textToSet = (pText != nullptr) ? pText : L"";
    CUpdateCaretAnchorEditSession *pEditSession = new (std::nothrow) CUpdateCaretAnchorEditSession(this, pContext, textToSet);
    if (pEditSession == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);

    pEditSession->Release();

    if (FAILED(hr))
    {
        return hr;
    }

    return (hrSession == S_OK) ? S_OK : S_FALSE;
}

HRESULT CSampleIME::_SetInitialCaretAnchorInputString(_In_ ITfContext *pContext, _In_ const WCHAR *pText)
{
    if (pContext == nullptr)
    {
        return E_INVALIDARG;
    }

    if (pText == nullptr || *pText == L'\0')
    {
        return S_FALSE;
    }

    _EndCaretAnchorComposition(nullptr);
    _StartCaretTrackingOnContext(pContext);

    CStartInitialCaretAnchorEditSession *pEditSession = new (std::nothrow) CStartInitialCaretAnchorEditSession(this, pContext, pText);
    if (pEditSession == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);
    pEditSession->Release();

    if (FAILED(hr))
    {
        return hr;
    }

    if (hrSession == S_OK)
    {
        _ScheduleCaretTrackingPrime();
    }

    return (hrSession == S_OK) ? S_OK : S_FALSE;
}

BOOL CSampleIME::_SyncCaretAnchorForResponse(_In_opt_ ITfContext *pContext, _Inout_ BimeResponse *pResponse)
{
    if (pResponse == nullptr)
    {
        return FALSE;
    }

    ITfContext *pEffectiveContext = (pContext != nullptr) ? pContext : _pCaretAnchorContext;
    BOOL compositionApplied = FALSE;

    if (!pResponse->handled && !pResponse->cancelComposition)
    {
        return FALSE;
    }

    if (pResponse->hasKeyboardOpen && !pResponse->keyboardOpen)
    {
        pResponse->inputBuffer.clear();
    }

    if (pEffectiveContext == nullptr)
    {
        return FALSE;
    }

    _ClearDeferredCaretAnchorReopen();

    if (pResponse->cancelComposition)
    {
        if (_pCaretAnchorComposition != nullptr && _pCaretAnchorContext == pEffectiveContext)
        {
            HRESULT hrCancelOnly = _CancelCaretAnchorComposition(pEffectiveContext);
            if (SUCCEEDED(hrCancelOnly))
            {
                compositionApplied = TRUE;
            }
            else
            {
                Global::LogToFileVerbose("CaretAnchor: cancel_before_pass not_applied hr=0x%08X", static_cast<unsigned>(hrCancelOnly));
            }
        }

        _lastAnchorInputBuffer.clear();
        if (!pResponse->handled)
        {
            return compositionApplied;
        }
    }

    const BOOL shouldDeferReopen = (!pResponse->textToOutput.empty() && !pResponse->inputBuffer.empty()) ? TRUE : FALSE;

    if (!pResponse->textToOutput.empty())
    {
        const std::wstring commitText = pResponse->textToOutput;
        BOOL commitByPieces = FALSE;
        if (commitText.length() >= 2 && commitText.length() <= 6 && commitText[0] == L'/')
        {
            commitByPieces = TRUE;
            for (size_t i = 1; i < commitText.length(); ++i)
            {
                const wchar_t ch = commitText[i];
                if (ch < L'a' || ch > L'z')
                {
                    commitByPieces = FALSE;
                    break;
                }
            }
        }

        HRESULT hrCommit = S_OK;
        if (commitByPieces)
        {
            for (size_t i = 0; i < commitText.length(); ++i)
            {
                WCHAR oneChar[2] = { commitText[i], L'\0' };
                hrCommit = _CommitAndEndCaretAnchorComposition(pEffectiveContext, oneChar);
                if (hrCommit != S_OK)
                {
                    break;
                }

                if (i + 1 < commitText.length())
                {
                    Sleep(20);
                }
            }
        }
        else
        {
            hrCommit = _CommitAndEndCaretAnchorComposition(pEffectiveContext, commitText.c_str());
        }

        // S_FALSE means the synchronous TSF edit was not applied. Neither it
        // nor an asynchronous/scheduled request is evidence of committed text.
        if (!pResponse->learningReceipt.empty())
        {
            if (_pPipeClient != nullptr)
                _pPipeClient->SendLearningCommit(pResponse->learningReceipt, hrCommit == S_OK);
            pResponse->learningReceipt.clear();
        }
        if (hrCommit == S_OK)
        {
            compositionApplied = TRUE;
            pResponse->textToOutput.clear();
        }
        else
        {
            Global::LogToFileVerbose("CaretAnchor: commit_text not_applied hr=0x%08X", static_cast<unsigned>(hrCommit));
        }
    }

    if (!pResponse->inputBuffer.empty())
    {
        if (shouldDeferReopen)
        {
            if (_ScheduleDeferredCaretAnchorReopen(pEffectiveContext, pResponse->inputBuffer))
            {
                compositionApplied = TRUE;
            }
            else
            {
                HRESULT hrUpdate = _UpdateCaretAnchorText(pEffectiveContext, pResponse->inputBuffer.c_str());
                if (hrUpdate == S_OK)
                {
                    _lastAnchorInputBuffer = pResponse->inputBuffer;
                    compositionApplied = TRUE;
                }
                else
                {
                    Global::LogToFileVerbose("CaretAnchor: deferred_update_text schedule_failed and immediate_update_not_applied hr=0x%08X", static_cast<unsigned>(hrUpdate));
                }
            }
        }
        else if (_lastAnchorInputBuffer != pResponse->inputBuffer ||
                 _pCaretAnchorComposition == nullptr ||
                 _pCaretAnchorContext != pEffectiveContext)
        {
            const BOOL needsInitialComposition = (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext != pEffectiveContext) ? TRUE : FALSE;
            HRESULT hrUpdate = needsInitialComposition
                ? _SetInitialCaretAnchorInputString(pEffectiveContext, pResponse->inputBuffer.c_str())
                : _UpdateCaretAnchorText(pEffectiveContext, pResponse->inputBuffer.c_str());
            if (hrUpdate == S_OK)
            {
                _lastAnchorInputBuffer = pResponse->inputBuffer;
                compositionApplied = TRUE;
            }
            else
            {
                Global::LogToFileVerbose("CaretAnchor: update_text not_applied hr=0x%08X", static_cast<unsigned>(hrUpdate));
            }
        }

        return compositionApplied;
    }

    if (_pCaretAnchorComposition != nullptr && _pCaretAnchorContext == pEffectiveContext)
    {
        HRESULT hrCancel = _CancelCaretAnchorComposition(pEffectiveContext);
        if (SUCCEEDED(hrCancel))
        {
            compositionApplied = TRUE;
        }
        else
        {
            Global::LogToFileVerbose("CaretAnchor: cancel not_applied hr=0x%08X", static_cast<unsigned>(hrCancel));
        }
    }

    return compositionApplied;
}
class CCommitCaretAnchorEditSession : public CEditSessionBase
{
public:
    CCommitCaretAnchorEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfComposition *pComposition, _In_ const WCHAR *pText)
        : CEditSessionBase(pTextService, pContext)
    {
        _pComposition = pComposition;
        if (_pComposition != nullptr)
        {
            _pComposition->AddRef();
        }

        if (pText != nullptr)
        {
            _text.assign(pText);
        }
    }

    ~CCommitCaretAnchorEditSession() override
    {
        if (_pComposition != nullptr)
        {
            _pComposition->Release();
            _pComposition = nullptr;
        }
    }

    STDMETHODIMP DoEditSession(TfEditCookie ec) override
    {
        if (_pComposition == nullptr)
        {
            return S_FALSE;
        }

        ITfRange *pRange = nullptr;
        HRESULT hr = _pComposition->GetRange(&pRange);
        if (FAILED(hr) || pRange == nullptr)
        {
            return FAILED(hr) ? hr : E_FAIL;
        }

        hr = pRange->SetText(ec, 0, _text.c_str(), static_cast<LONG>(_text.length()));
        if (SUCCEEDED(hr))
        {
            ClearRangeDisplayAttribute(_pContext, ec, pRange);
            pRange->Collapse(ec, TF_ANCHOR_END);

            TF_SELECTION selection = {};
            selection.range = pRange;
            selection.style.ase = TF_AE_NONE;
            selection.style.fInterimChar = FALSE;
            _pContext->SetSelection(ec, 1, &selection);

            hr = _pComposition->EndComposition(ec);
            if (FAILED(hr))
            {
                Global::LogToFileVerbose("CaretAnchor: commit EndComposition failed hr=0x%08X", static_cast<unsigned>(hr));
            }
        }

        pRange->Release();
        return hr;
    }

private:
    ITfComposition *_pComposition = nullptr;
    std::wstring _text;
};

HRESULT CSampleIME::_CommitAndEndCaretAnchorComposition(_In_ ITfContext *pContext, _In_ const WCHAR *pText)
{
    if (pContext == nullptr || pText == nullptr || pText[0] == L'\0')
    {
        return E_INVALIDARG;
    }

    HRESULT hrEnsure = _EnsureCaretAnchorComposition(pContext);
    if (FAILED(hrEnsure))
    {
        return hrEnsure;
    }

    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext != pContext)
    {
        return E_FAIL;
    }

    ITfComposition *pComposition = _pCaretAnchorComposition;
    pComposition->AddRef();
    _lastAnchorInputBuffer.clear();

    CCommitCaretAnchorEditSession *pEditSession = new (std::nothrow) CCommitCaretAnchorEditSession(this, pContext, pComposition, pText);
    if (pEditSession == nullptr)
    {
        pComposition->Release();
        return E_OUTOFMEMORY;
    }

    HRESULT hrSession = S_OK;
    _suppressExternalCompositionCanceledNotify = TRUE;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_SYNC | TF_ES_READWRITE, &hrSession);
    _suppressExternalCompositionCanceledNotify = FALSE;
    pEditSession->Release();

    if (FAILED(hr))
    {
        pComposition->Release();
        return hr;
    }

    if (hrSession == S_OK)
    {
        _OnCaretAnchorCompositionTerminated(pComposition);
    }

    pComposition->Release();
    return hrSession;
}


static BOOL IsWindowClassEquals(_In_opt_ HWND hwnd, _In_z_ const WCHAR *className)
{
    if (hwnd == nullptr || className == nullptr)
    {
        return FALSE;
    }

    WCHAR currentClass[128] = {};
    if (GetClassNameW(hwnd, currentClass, ARRAYSIZE(currentClass)) <= 0)
    {
        return FALSE;
    }

    return (_wcsicmp(currentClass, className) == 0) ? TRUE : FALSE;
}

static BOOL IsImmersiveWindowClass(_In_opt_ HWND hwnd)
{
    if (IsWindowClassEquals(hwnd, L"Windows.UI.Core.CoreWindow"))
    {
        return TRUE;
    }

    // UWP desktop bridge / app frame host.
    if (IsWindowClassEquals(hwnd, L"ApplicationFrameWindow"))
    {
        return TRUE;
    }

    return FALSE;
}

void CSampleIME::_RefreshImmersiveState(_In_opt_ const char *source, _In_opt_ HWND hwndForeground)
{
    BOOL byActivateFlag = _IsStoreAppMode();
    BOOL byThreadMgrFlag = FALSE;
    BOOL byForegroundWindow = IsImmersiveWindowClass(hwndForeground);

    DWORD threadMgrActiveFlags = 0;
    HRESULT hrActiveFlags = E_FAIL;
    ITfThreadMgrEx *pThreadMgrEx = nullptr;
    if (_pThreadMgr != nullptr && SUCCEEDED(_pThreadMgr->QueryInterface(IID_ITfThreadMgrEx, (void **)&pThreadMgrEx)) && pThreadMgrEx != nullptr)
    {
        hrActiveFlags = pThreadMgrEx->GetActiveFlags(&threadMgrActiveFlags);
        if (SUCCEEDED(hrActiveFlags) && ((threadMgrActiveFlags & TF_TMF_IMMERSIVEMODE) != 0))
        {
            byThreadMgrFlag = TRUE;
        }
        pThreadMgrEx->Release();
    }

    BOOL newImmersiveState = (byActivateFlag || byThreadMgrFlag || byForegroundWindow) ? TRUE : FALSE;
    if (newImmersiveState != _isImmersiveSession)
    {
        Global::LogToFileVerbose("ImmersiveState %s changed=%d by_activate=%d by_threadmgr=%d by_window=%d tm_flags=0x%08X hr=0x%08X",
                                 source ? source : "<none>",
                                 newImmersiveState,
                                 byActivateFlag,
                                 byThreadMgrFlag,
                                 byForegroundWindow,
                                 static_cast<unsigned>(threadMgrActiveFlags),
                                 static_cast<unsigned>(hrActiveFlags));
    }
    else
    {
        Global::LogToFileVerbose("ImmersiveState %s stable=%d by_activate=%d by_threadmgr=%d by_window=%d tm_flags=0x%08X hr=0x%08X",
                                 source ? source : "<none>",
                                 _isImmersiveSession,
                                 byActivateFlag,
                                 byThreadMgrFlag,
                                 byForegroundWindow,
                                 static_cast<unsigned>(threadMgrActiveFlags),
                                 static_cast<unsigned>(hrActiveFlags));
    }

    _isImmersiveSession = newImmersiveState;
}

BOOL CSampleIME::_IsStartupGuardActive()
{
    if (!_isDevenvHost)
    {
        return FALSE;
    }

    if (_activateTick == 0)
    {
        return FALSE;
    }

    if (_pPipeClient != nullptr && _pPipeClient->IsConnected())
    {
        return FALSE;
    }

    const ULONGLONG kStartupGuardWindowMs = 150;
    ULONGLONG elapsed = GetTickCount64() - _activateTick;
    return (elapsed < kStartupGuardWindowMs) ? TRUE : FALSE;
}

BOOL CSampleIME::_InitCtrlSpacePreservedKey()
{
    // Bridge mode: Ctrl+Space is handled by forwarding key events to BimeCore.
    // Do not register PreservedKey to avoid system-level toggle conflicts.
    _ctrlSpacePreservedKeyRegistered = FALSE;
    Global::LogToFileVerbose("PreservedKey Ctrl+Space disabled (core key pipeline)");
    return TRUE;
}

void CSampleIME::_UninitCtrlSpacePreservedKey()
{
    _ctrlSpacePreservedKeyRegistered = FALSE;
}

BOOL CSampleIME::_InitBridgeLanguageBar()
{

    if (_pThreadMgr == nullptr || _tfClientId == TF_CLIENTID_NULL)
    {
        return FALSE;
    }

    if (_pLangBarItem != nullptr)
    {
        return TRUE;
    }

    _pLangBarItem = new (std::nothrow) CLangBarItemButton(
        GUID_LBI_INPUTMODE,
        Global::LangbarImeModeDescription,
        Global::ImeModeDescription,
        Global::ImeModeOnIcoIndex,
        Global::ImeModeOffIcoIndex,
        _IsSecureMode());
    if (_pLangBarItem == nullptr)
    {
        return FALSE;
    }

    HRESULT hr = _pLangBarItem->_AddItem(_pThreadMgr);
    if (FAILED(hr))
    {
        Global::LogToFile("BridgeLanguageBar: AddItem failed hr=0x%08X", static_cast<unsigned>(hr));
        _pLangBarItem->Release();
        _pLangBarItem = nullptr;
        return FALSE;
    }

    if (!_pLangBarItem->_RegisterCompartment(_pThreadMgr, _tfClientId, GUID_COMPARTMENT_KEYBOARD_OPENCLOSE))
    {
        Global::LogToFile("BridgeLanguageBar: RegisterCompartment failed");
        _pLangBarItem->CleanUp();
        _pLangBarItem->Release();
        _pLangBarItem = nullptr;
        return FALSE;
    }

    Global::LogToFileVerbose("BridgeLanguageBar: initialized");
    return TRUE;
}

void CSampleIME::_UninitBridgeLanguageBar()
{
    if (_pLangBarItem == nullptr)
    {
        return;
    }

    _pLangBarItem->CleanUp();
    _pLangBarItem->Release();
    _pLangBarItem = nullptr;

    Global::LogToFileVerbose("BridgeLanguageBar: uninitialized");
}

void CSampleIME::_SyncKeyboardOpenCompartment(BOOL isOpen)
{
    if (_pLangBarItem != nullptr)
    {
        _pLangBarItem->SetKeyboardOpenState(isOpen);
    }

    if (_pThreadMgr == nullptr || _tfClientId == TF_CLIENTID_NULL)
    {
        return;
    }

    CCompartment compartmentKeyboardOpen(_pThreadMgr, _tfClientId, GUID_COMPARTMENT_KEYBOARD_OPENCLOSE);
    BOOL currentOpen = FALSE;
    if (SUCCEEDED(compartmentKeyboardOpen._GetCompartmentBOOL(currentOpen)) && currentOpen == isOpen)
    {
        return;
    }

    compartmentKeyboardOpen._SetCompartmentBOOL(isOpen);
    Global::LogToFileVerbose("CompartmentSync keyboard_open=%d", isOpen);
}

BOOL CSampleIME::_InitCaretCoalesceWindow()
{
    if (_msgWndHandle != nullptr)
    {
        return TRUE;
    }

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = CSampleIME_WindowProc;
    wc.hInstance = Global::dllInstanceHandle;
    wc.lpszClassName = kCaretCoalesceWindowClass;

    ATOM atom = RegisterClassExW(&wc);
    if (atom == 0)
    {
        DWORD err = GetLastError();
        if (err != ERROR_CLASS_ALREADY_EXISTS)
        {
            Global::LogToFile("CaretCoalesce: RegisterClassEx failed err=%lu", err);
            return FALSE;
        }
    }

    _msgWndHandle = CreateWindowExW(0,
                                    kCaretCoalesceWindowClass,
                                    L"",
                                    0,
                                    0,
                                    0,
                                    0,
                                    0,
                                    HWND_MESSAGE,
                                    nullptr,
                                    Global::dllInstanceHandle,
                                    this);
    if (_msgWndHandle == nullptr)
    {
        Global::LogToFile("CaretCoalesce: CreateWindowEx failed err=%lu", GetLastError());
        return FALSE;
    }

    _ResetCaretCoalesceState();
    return TRUE;
}

void CSampleIME::_UninitCaretCoalesceWindow()
{
    _FlushPendingCaretMessage(TRUE);

    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kCaretCoalesceTimerId);
        KillTimer(_msgWndHandle, kFocusQueryStateTimerId);
        KillTimer(_msgWndHandle, kImeActivePublishRetryTimerId);
        KillTimer(_msgWndHandle, kCompositionRefreshTimerId);
        KillTimer(_msgWndHandle, kFailedKeyFlushTimerId);
        DestroyWindow(_msgWndHandle);
        _msgWndHandle = nullptr;
    }

    _ResetCaretCoalesceState();
}

void CSampleIME::_ScheduleCompositionRefresh(BOOL decodePending)
{
    if (_msgWndHandle == nullptr)
    {
        return;
    }

    UINT delayMs = decodePending ? kCompositionPendingRefreshMs : kCompositionTrackingRefreshMs;
    KillTimer(_msgWndHandle, kCompositionRefreshTimerId);
    _compositionRefreshScheduled =
        SetTimer(_msgWndHandle, kCompositionRefreshTimerId, delayMs, nullptr) != 0;
}

void CSampleIME::_CancelCompositionRefresh()
{
    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kCompositionRefreshTimerId);
    }
    _compositionRefreshScheduled = FALSE;
}

void CSampleIME::_HandleCompositionRefresh()
{
    _CancelCompositionRefresh();
    if (_pPipeClient == nullptr || !_EnsurePipeConnected())
    {
        return;
    }

    BimeResponse response;
    HRESULT hr = _pPipeClient->SendQueryStateAndWait(&response, kCompositionRefreshTimeoutMs);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("CompositionRefresh: query failed hr=0x%08X", static_cast<unsigned>(hr));
        return;
    }

    if (response.compositionTracking && response.compositionPending)
    {
        _ScheduleCompositionRefresh(TRUE);
        return;
    }

    ITfDocumentMgr *pDocMgrFocus = nullptr;
    ITfContext *pContext = nullptr;
    if (_pThreadMgr != nullptr &&
        SUCCEEDED(_pThreadMgr->GetFocus(&pDocMgrFocus)) &&
        pDocMgrFocus != nullptr)
    {
        pDocMgrFocus->GetTop(&pContext);
    }

    response.handled = TRUE;
    _ApplyResponseAndSyncState(pContext, &response, "CompositionRefresh");

    if (pContext != nullptr)
    {
        pContext->Release();
    }
    if (pDocMgrFocus != nullptr)
    {
        pDocMgrFocus->Release();
    }
}

void CSampleIME::_ResetCaretCoalesceState()
{
    _hasSentCaret = FALSE;
    _lastSentCaretX = 0;
    _lastSentCaretY = 0;
    _lastSentCaretWidth = 0;
    _lastSentCaretHeight = 0;
    _lastLayoutCaretTick = 0;
    _lastLayoutRequestTick = 0;
    _lastLayoutCaretX = 0;
    _lastLayoutCaretY = 0;
    _hasPendingCaret = FALSE;
    _pendingCaretX = 0;
    _pendingCaretY = 0;
    _pendingCaretWidth = 0;
    _pendingCaretHeight = 0;
    _pendingCaretSource = CARET_SOURCE_UNKNOWN;

    _forceNextCaret = TRUE;

}

void CSampleIME::_FlushPendingCaretMessage(BOOL forceSend)
{
    if (!_hasPendingCaret)
    {
        return;
    }

    if (_pPipeClient == nullptr || !_pPipeClient->IsConnected())
    {
        _hasPendingCaret = FALSE;
        _pendingCaretSource = CARET_SOURCE_UNKNOWN;
        if (_msgWndHandle != nullptr && !forceSend)
        {
            KillTimer(_msgWndHandle, kCaretCoalesceTimerId);
        }
        return;
    }

    ULONGLONG now = GetTickCount64();
    if (!forceSend && _lastCaretSentTick != 0)
    {
        ULONGLONG elapsed = (now >= _lastCaretSentTick) ? (now - _lastCaretSentTick) : 0;
        if (elapsed < kCaretSendCooldownMs)
        {
            UINT delayMs = static_cast<UINT>(kCaretSendCooldownMs - elapsed);
            if (delayMs == 0)
            {
                delayMs = 1;
            }
            if (_msgWndHandle != nullptr)
            {
                SetTimer(_msgWndHandle, kCaretCoalesceTimerId, delayMs, nullptr);
            }
            Global::LogToFileVerbose("CaretCoalesce: flush delayed cooldown=%u src=%d", delayMs, _pendingCaretSource);
            return;
        }
    }

    if (!forceSend && _msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kCaretCoalesceTimerId);
    }

    if (_pPipeClient->SendCaretMessage(_pendingCaretX, _pendingCaretY, _pendingCaretWidth, _pendingCaretHeight))
    {
        _hasSentCaret = TRUE;
        _lastSentCaretX = _pendingCaretX;
        _lastSentCaretY = _pendingCaretY;
        _lastSentCaretWidth = _pendingCaretWidth;
        _lastSentCaretHeight = _pendingCaretHeight;
        _forceNextCaret = FALSE;
        _lastCaretSentTick = GetTickCount64();
        Global::LogToFileVerbose("CaretCoalesce: flush x=%ld y=%ld w=%ld h=%ld force=%d src=%d", _pendingCaretX, _pendingCaretY, _pendingCaretWidth, _pendingCaretHeight, forceSend, _pendingCaretSource);
    }

    _hasPendingCaret = FALSE;
    _pendingCaretSource = CARET_SOURCE_UNKNOWN;
}

BOOL CSampleIME::_ResolveBimeCoreRelativePath(_Out_writes_(pathCount) WCHAR *path, size_t pathCount)
{
    if (path == nullptr || pathCount < 2)
    {
        return FALSE;
    }

    path[0] = L'\0';

    if (TryResolveBimeCorePathFromRegistry(path, pathCount))
    {
        Global::LogToFileVerbose("PipeBridge: resolved core path via registry: %ls", path);
        return TRUE;
    }

    WCHAR modulePath[MAX_PATH] = {};
    DWORD moduleLen = GetModuleFileNameW(Global::dllInstanceHandle, modulePath, ARRAYSIZE(modulePath));
    if (moduleLen == 0 || moduleLen >= ARRAYSIZE(modulePath))
    {
        return FALSE;
    }

    WCHAR *lastSlash = wcsrchr(modulePath, L'\\');
    if (lastSlash == nullptr)
    {
        return FALSE;
    }
    *lastSlash = L'\0';

    HRESULT hr = StringCchPrintfW(path, pathCount, L"%s\\%s", modulePath, kCoreExeFileName);
    if (SUCCEEDED(hr) && IsRegularFilePath(path))
    {
        return TRUE;
    }

    WCHAR parentPath[MAX_PATH] = {};
    hr = StringCchCopyW(parentPath, ARRAYSIZE(parentPath), modulePath);
    if (FAILED(hr))
    {
        return FALSE;
    }

    lastSlash = wcsrchr(parentPath, L'\\');
    if (lastSlash == nullptr)
    {
        return FALSE;
    }
    *lastSlash = L'\0';

    hr = StringCchPrintfW(path, pathCount, L"%s\\%s", parentPath, kCoreExeFileName);
    if (SUCCEEDED(hr) && IsRegularFilePath(path))
    {
        return TRUE;
    }

    path[0] = L'\0';
    return FALSE;
}

BOOL CSampleIME::_TryLaunchBimeCore()
{
    if (!TigerClawStartup::CanLaunchCore())
    {
        Global::LogToFileVerbose("PipeBridge: launch_core skip=non_user_desktop");
        return FALSE;
    }
    WCHAR corePath[MAX_PATH] = {};
    if (!_ResolveBimeCoreRelativePath(corePath, ARRAYSIZE(corePath)))
    {
        Global::LogToFileVerbose("PipeBridge: launch_core skip=path_not_found");
        return FALSE;
    }

    WCHAR commandLine[MAX_PATH * 2] = {};
    HRESULT hr = StringCchPrintfW(commandLine, ARRAYSIZE(commandLine), L"\"%s\" --autorun --silent", corePath);
    if (FAILED(hr))
    {
        hr = StringCchPrintfW(commandLine, ARRAYSIZE(commandLine), L"\"%s\"", corePath);
        if (FAILED(hr))
        {
            Global::LogToFile("PipeBridge: launch_core skip=command_line_too_long");
            return FALSE;
        }
    }

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};

    BOOL launched = CreateProcessW(corePath, commandLine, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi);
    if (!launched)
    {
        DWORD err = GetLastError();
        Global::LogToFile("PipeBridge: launch_core failed err=%lu", err);
        return FALSE;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    Global::LogToFileVerbose("PipeBridge: launch_core success");
    return TRUE;
}

DWORD WINAPI CSampleIME::_LaunchCoreWorkerProc(_In_ LPVOID param)
{
    CSampleIME *pTextService = reinterpret_cast<CSampleIME *>(param);
    if (pTextService == nullptr)
    {
        return 0;
    }

    pTextService->_TryLaunchBimeCore();
    InterlockedExchange(&pTextService->_coreLaunchWorkerRunning, 0);
    pTextService->Release();
    return 0;
}

void CSampleIME::_ScheduleCoreLaunch()
{
    if (!TigerClawStartup::CanLaunchCore()) return;
    if (_pPipeClient != nullptr && _pPipeClient->IsConnected())
    {
        return;
    }

    ULONGLONG now = GetTickCount64();
    if (_lastCoreLaunchAttemptTick != 0)
    {
        ULONGLONG elapsed = (now >= _lastCoreLaunchAttemptTick) ? (now - _lastCoreLaunchAttemptTick) : kCoreLaunchThrottleMs;
        if (elapsed < kCoreLaunchThrottleMs)
        {
            Global::LogToFileVerbose("PipeBridge: launch_core throttled elapsed=%llu", elapsed);
            return;
        }
    }

    if (InterlockedCompareExchange(&_coreLaunchWorkerRunning, 1, 0) != 0)
    {
        return;
    }

    _lastCoreLaunchAttemptTick = now;

    AddRef();
    HANDLE hWorker = CreateThread(nullptr, 0, &CSampleIME::_LaunchCoreWorkerProc, this, 0, nullptr);
    if (hWorker == nullptr)
    {
        DWORD err = GetLastError();
        InterlockedExchange(&_coreLaunchWorkerRunning, 0);
        Release();
        Global::LogToFile("PipeBridge: launch_core worker_create_failed err=%lu", err);
        return;
    }

    CloseHandle(hWorker);
    Global::LogToFileVerbose("PipeBridge: launch_core scheduled");
}

BOOL CSampleIME::_EnsurePipeConnected()
{
    if (TigerClawInput::IsProtectedEnvironment(_IsSecureMode() != FALSE) ||
        TigerClawInput::HasPasswordFocus()) return FALSE;
    if (_pPipeClient == nullptr || _pPipeClient->IsBusy())
    {
        return FALSE;
    }

    if (_pPipeClient->IsConnected())
    {
        _pPipeClient->EnsureHelloHandshake(kPipeHelloTimeoutMs);
        return TRUE;
    }

    if (!_pPipeClient->Connect())
    {
        _ScheduleCoreLaunch();
        return FALSE;
    }

    _pPipeClient->EnsureHelloHandshake(kPipeHelloTimeoutMs);
    return TRUE;
}

void CSampleIME::_SendFocusMessage()
{
    HWND hwnd = GetForegroundWindow();
    if (hwnd == nullptr)
    {
        Global::LogToFileVerbose("FocusSync skip=no_foreground_window");
        return;
    }

    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);

    _focusQueryPending = FALSE;
    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kFocusQueryStateTimerId);
    }

    ULONGLONG now = GetTickCount64();
    BOOL skipDuplicate = FALSE;
    if (_lastFocusHwnd == static_cast<LONGLONG>(reinterpret_cast<LONG_PTR>(hwnd)) &&
        _lastFocusProcessId == processId &&
        _lastFocusSentTick != 0)
    {
        ULONGLONG elapsed = (now >= _lastFocusSentTick) ? (now - _lastFocusSentTick) : 0;
        if (elapsed < kFocusSyncDebounceMs)
        {
            skipDuplicate = TRUE;
            Global::LogToFileVerbose("FocusSync debounce_skip hwnd=0x%p pid=%lu elapsed=%llu", hwnd, processId, elapsed);
        }
    }

    BOOL sent = FALSE;
    if (!skipDuplicate)
    {
        if (!_EnsurePipeConnected())
        {
            Global::LogToFileVerbose("FocusSync skip=pipe_disconnected hwnd=0x%p pid=%lu", hwnd, processId);
        }
        else
        {
            sent = _pPipeClient->SendFocusMessage(static_cast<LONGLONG>(reinterpret_cast<LONG_PTR>(hwnd)), processId);
            Global::LogToFileVerbose("FocusSync sent=%d hwnd=0x%p pid=%lu", sent, hwnd, processId);
            if (sent)
            {
                _lastFocusHwnd = static_cast<LONGLONG>(reinterpret_cast<LONG_PTR>(hwnd));
                _lastFocusProcessId = processId;
                _lastFocusSentTick = now;
                _pendingFocusHwnd = _lastFocusHwnd;
                _pendingFocusProcessId = processId;
                _focusQueryPending = TRUE;
                _ScheduleFocusStateQuery();
            }
        }
    }

    _forceNextCaret = TRUE;
    _lastLayoutCaretTick = 0;
    _lastLayoutRequestTick = 0;
    _lastLayoutCaretX = 0;
    _lastLayoutCaretY = 0;
    _hasPendingCaret = FALSE;
    _pendingCaretSource = CARET_SOURCE_UNKNOWN;
    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kCaretCoalesceTimerId);
    }

    _failedKeyQueue.clear();

    if (sent && _isDevenvHost && _activateTick != 0)
    {
        _activateTick = 0;
        Global::LogToFileVerbose("FocusSync startup_guard_cleared");
    }
}

void CSampleIME::_PublishImeActive()
{
    // Query focus editability live (not cached): switching IME / Win+Space fires OnActivated but
    // not OnSetFocus, so a cached value goes stale. Re-derive from the current thread focus each time.
    BOOL focusEditable = FALSE;
    if (_pThreadMgr != nullptr)
    {
        ITfDocumentMgr *pDocMgrFocus = nullptr;
        if (SUCCEEDED(_pThreadMgr->GetFocus(&pDocMgrFocus)) && pDocMgrFocus != nullptr)
        {
            ITfContext *pTopContext = nullptr;
            if (SUCCEEDED(pDocMgrFocus->GetTop(&pTopContext)) && pTopContext != nullptr)
            {
                focusEditable = TRUE;
                pTopContext->Release();
            }
            pDocMgrFocus->Release();
        }
    }

    // Active = our IME selected (profile) AND focus on an editable doc. Either false => inactive.
    BOOL active = (_profileActive && focusEditable) ? TRUE : FALSE;

    // Debounce: only send when the value changed, to avoid spamming on frequent OnSetFocus/OnActivated.
    if (_hasSentImeActive && _lastImeActiveSent == active)
    {
        _CancelImeActivePublishRetry();
        return;
    }

    if (_pPipeClient == nullptr)
    {
        return;
    }

    if (!_EnsurePipeConnected())
    {
        Global::LogToFileVerbose("ImeActiveSync: pipe unavailable active=%d", active);
        // First launch: Core not connected yet. If we want to show (active=TRUE), retry until Core is up.
        if (active)
        {
            _ScheduleImeActivePublishRetry();
        }
        return;
    }

    BOOL sent = _pPipeClient->SendImeActiveMessage(active);
    Global::LogToFileVerbose("ImeActiveSync: sent=%d active=%d profile=%d focus=%d", sent, active, _profileActive, focusEditable);
    if (sent)
    {
        _lastImeActiveSent = active;
        _hasSentImeActive = TRUE;
        _CancelImeActivePublishRetry();
    }
    else if (active)
    {
        _ScheduleImeActivePublishRetry();
    }
}

void CSampleIME::_ScheduleImeActivePublishRetry()
{
    if (_msgWndHandle == nullptr)
    {
        return;
    }
    if (_imeActivePublishRetryCount >= kImeActivePublishRetryMax)
    {
        Global::LogToFileVerbose("ImeActiveSync: retry exhausted count=%d", _imeActivePublishRetryCount);
        return;
    }
    _imeActivePublishRetryCount++;
    KillTimer(_msgWndHandle, kImeActivePublishRetryTimerId);
    SetTimer(_msgWndHandle, kImeActivePublishRetryTimerId, kImeActivePublishRetryDelayMs, nullptr);
}

void CSampleIME::_CancelImeActivePublishRetry()
{
    _imeActivePublishRetryCount = 0;
    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kImeActivePublishRetryTimerId);
    }
}

void CSampleIME::_SendCompositionCanceledMessage()
{
    if (_pPipeClient == nullptr)
    {
        return;
    }

    if (!_EnsurePipeConnected())
    {
        Global::LogToFileVerbose("CompositionCancelSync: pipe unavailable");
        return;
    }

    BOOL sent = _pPipeClient->SendCompositionCanceledMessage();
    Global::LogToFileVerbose("CompositionCancelSync: sent=%d", sent);
}

void CSampleIME::_ScheduleFocusStateQuery()
{
    if (_msgWndHandle == nullptr || !_focusQueryPending)
    {
        return;
    }

    KillTimer(_msgWndHandle, kFocusQueryStateTimerId);
    SetTimer(_msgWndHandle, kFocusQueryStateTimerId, kFocusQueryStateDelayMs, nullptr);
}

void CSampleIME::_HandleDeferredFocusStateQuery()
{
    if (_msgWndHandle != nullptr)
    {
        KillTimer(_msgWndHandle, kFocusQueryStateTimerId);
    }

    if (!_focusQueryPending)
    {
        return;
    }

    LONGLONG targetHwndValue = _pendingFocusHwnd;
    DWORD targetPid = _pendingFocusProcessId;
    _focusQueryPending = FALSE;

    HWND hwndCurrent = GetForegroundWindow();
    DWORD currentPid = 0;
    if (hwndCurrent != nullptr)
    {
        GetWindowThreadProcessId(hwndCurrent, &currentPid);
    }

    if (hwndCurrent == nullptr ||
        targetHwndValue != static_cast<LONGLONG>(reinterpret_cast<LONG_PTR>(hwndCurrent)) ||
        targetPid != currentPid)
    {
        Global::LogToFileVerbose("FocusSync query_state skip=focus_changed target_hwnd=0x%llX target_pid=%lu current_hwnd=0x%p current_pid=%lu",
                                 targetHwndValue,
                                 targetPid,
                                 hwndCurrent,
                                 currentPid);
        return;
    }

    if (!_EnsurePipeConnected())
    {
        Global::LogToFileVerbose("FocusSync query_state unavailable reason=pipe_disconnected hwnd=0x%p pid=%lu", hwndCurrent, currentPid);
        return;
    }

    BimeResponse queryResponse;
    HRESULT queryHr = _pPipeClient->SendQueryStateAndWait(&queryResponse, kPipeFocusQueryTimeoutMs);
    if (SUCCEEDED(queryHr) && queryResponse.hasKeyboardOpen)
    {
        _SyncKeyboardOpenCompartment(queryResponse.keyboardOpen);
        Global::LogToFileVerbose("FocusSync query_state keyboard_open=%d", queryResponse.keyboardOpen);
    }
    else
    {
        Global::LogToFileVerbose("FocusSync query_state unavailable hr=0x%08X", static_cast<unsigned>(queryHr));
    }
}

void CSampleIME::_SendCaretMessage(LONG x, LONG y, LONG width, LONG height, int source)
{
    if ((source == CARET_SOURCE_LAYOUT || source == CARET_SOURCE_END_EDIT) &&
        (_pCaretAnchorComposition == nullptr))
    {
        return;
    }

    if (_pPipeClient == nullptr || !_pPipeClient->IsConnected())
    {
        return;
    }

    ULONGLONG now = GetTickCount64();
    const BOOL isHighPrioritySource = (source == CARET_SOURCE_LAYOUT || source == CARET_SOURCE_END_EDIT) ? TRUE : FALSE;
    const BOOL isAnchorFallbackSource = (source == CARET_SOURCE_ANCHOR || source == CARET_SOURCE_COMMIT) ? TRUE : FALSE;

    if (isAnchorFallbackSource && _lastHighPriorityCaretTick != 0)
    {
        ULONGLONG elapsedHigh = (now >= _lastHighPriorityCaretTick) ? (now - _lastHighPriorityCaretTick) : 0;
        if (elapsedHigh <= kCaretAnchorOverrideWindowMs)
        {
            Global::LogToFileVerbose("CaretCoalesce: suppress anchor src=%d elapsed_high=%llu", source, elapsedHigh);
            return;
        }
    }

    if (source == CARET_SOURCE_LAYOUT)
    {
        _lastLayoutCaretTick = now;
        _lastLayoutCaretX = x;
        _lastLayoutCaretY = y;
    }
    else if (source == CARET_SOURCE_END_EDIT && _lastLayoutCaretTick != 0)
    {
        ULONGLONG elapsed = (now >= _lastLayoutCaretTick) ? (now - _lastLayoutCaretTick) : 0;
        LONG driftX = x - _lastLayoutCaretX;
        if (driftX < 0)
        {
            driftX = -driftX;
        }

        LONG driftY = y - _lastLayoutCaretY;
        if (driftY < 0)
        {
            driftY = -driftY;
        }

        if (elapsed <= kCaretEndEditSuppressWindowMs && (driftX + driftY) <= kCaretCoalesceImmediateJumpThreshold)
        {
            Global::LogToFileVerbose("CaretCoalesce: suppress end_edit x=%ld y=%ld elapsed=%llu drift=%ld", x, y, elapsed, driftX + driftY);
            return;
        }
    }

    if (isHighPrioritySource)
    {
        _lastHighPriorityCaretTick = now;
    }

    if (source == CARET_SOURCE_LAYOUT && !_forceNextCaret)
    {
        const BOOL matchesPendingLayout =
            _hasPendingCaret &&
            _pendingCaretSource == CARET_SOURCE_LAYOUT &&
            _pendingCaretX == x &&
            _pendingCaretY == y &&
            _pendingCaretWidth == width &&
            _pendingCaretHeight == height;
        if (matchesPendingLayout)
        {
            return;
        }

        const BOOL matchesLastSentCaret =
            !_hasPendingCaret &&
            _hasSentCaret &&
            _lastSentCaretX == x &&
            _lastSentCaretY == y &&
            _lastSentCaretWidth == width &&
            _lastSentCaretHeight == height;
        if (matchesLastSentCaret)
        {
            return;
        }
    }

    LONG dx = 0;
    LONG dy = 0;
    BOOL jumpByThreshold = FALSE;
    if (_hasSentCaret)
    {
        dx = x - _lastSentCaretX;
        if (dx < 0)
        {
            dx = -dx;
        }

        dy = y - _lastSentCaretY;
        if (dy < 0)
        {
            dy = -dy;
        }

        jumpByThreshold = ((dx + dy) >= kCaretCoalesceImmediateJumpThreshold) ? TRUE : FALSE;
    }

    BOOL isLayoutSource = (source == CARET_SOURCE_LAYOUT) ? TRUE : FALSE;
    BOOL forceSend = _forceNextCaret;
    UINT cooldownDelayMs = kCaretCoalesceWindowMs;
    BOOL inCooldown = FALSE;
    if (_lastCaretSentTick != 0)
    {
        ULONGLONG elapsed = (now >= _lastCaretSentTick) ? (now - _lastCaretSentTick) : 0;
        if (elapsed < kCaretSendCooldownMs)
        {
            inCooldown = TRUE;
            UINT remain = static_cast<UINT>(kCaretSendCooldownMs - elapsed);
            if (remain > cooldownDelayMs)
            {
                cooldownDelayMs = remain;
            }
        }
    }

    BOOL sendNow = (forceSend || !_hasSentCaret || (jumpByThreshold && isLayoutSource)) ? TRUE : FALSE;
    if (isAnchorFallbackSource)
    {
        sendNow = FALSE;
        if (cooldownDelayMs < kCaretAnchorOverrideWindowMs)
        {
            cooldownDelayMs = kCaretAnchorOverrideWindowMs;
        }
    }
    if (!forceSend && inCooldown)
    {
        sendNow = FALSE;
    }
    if (sendNow)
    {
        if (_msgWndHandle != nullptr)
        {
            KillTimer(_msgWndHandle, kCaretCoalesceTimerId);
        }

        _hasPendingCaret = FALSE;
        _pendingCaretSource = CARET_SOURCE_UNKNOWN;

        if (_pPipeClient->SendCaretMessage(x, y, width, height))
        {
            _hasSentCaret = TRUE;
            _lastSentCaretX = x;
            _lastSentCaretY = y;
            _lastSentCaretWidth = width;
            _lastSentCaretHeight = height;
            _forceNextCaret = FALSE;
            _lastCaretSentTick = GetTickCount64();
            Global::LogToFileVerbose("CaretCoalesce: immediate x=%ld y=%ld w=%ld h=%ld force=%d jump=%d src=%d", x, y, width, height, forceSend ? 1 : 0, jumpByThreshold, source);
        }
        return;
    }

    BOOL replacedPending = FALSE;
    if (!_hasPendingCaret || GetCaretSourcePriority(source) >= GetCaretSourcePriority(_pendingCaretSource))
    {
        _pendingCaretX = x;
        _pendingCaretY = y;
        _pendingCaretWidth = width;
        _pendingCaretHeight = height;
        _pendingCaretSource = source;
        _hasPendingCaret = TRUE;
        replacedPending = TRUE;
    }

    if (_msgWndHandle != nullptr)
    {
        SetTimer(_msgWndHandle, kCaretCoalesceTimerId, cooldownDelayMs, nullptr);
    }
    else
    {
        _FlushPendingCaretMessage(FALSE);
    }

    Global::LogToFileVerbose("CaretCoalesce: queue x=%ld y=%ld w=%ld h=%ld src=%d replaced=%d", x, y, width, height, source, replacedPending ? 1 : 0);
}
HRESULT CSampleIME::GetType(__RPC__out GUID *pguid)
{
    HRESULT hr = E_INVALIDARG;
    if (pguid)
    {
        *pguid = Global::SampleIMECLSID;
        hr = S_OK;
    }
    return hr;
}

//+---------------------------------------------------------------------------
//
// ITfFunctionProvider::::GetDescription
//
//----------------------------------------------------------------------------
HRESULT CSampleIME::GetDescription(__RPC__deref_out_opt BSTR *pbstrDesc)
{
    HRESULT hr = E_INVALIDARG;
    if (pbstrDesc != nullptr)
    {
        *pbstrDesc = nullptr;
        hr = E_NOTIMPL;
    }
    return hr;
}

//+---------------------------------------------------------------------------
//
// ITfFunctionProvider::::GetFunction
//
//----------------------------------------------------------------------------
HRESULT CSampleIME::GetFunction(__RPC__in REFGUID rguid, __RPC__in REFIID riid, __RPC__deref_out_opt IUnknown **ppunk)
{
    if (ppunk == nullptr)
    {
        return E_INVALIDARG;
    }

    *ppunk = nullptr;

    char rguidA[128] = {0};
    char riidA[128] = {0};
    wchar_t guidW[64] = {0};

    if (StringFromGUID2(rguid, guidW, ARRAYSIZE(guidW)) > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, guidW, -1, rguidA, ARRAYSIZE(rguidA), nullptr, nullptr);
    }

    ZeroMemory(guidW, sizeof(guidW));
    if (StringFromGUID2(riid, guidW, ARRAYSIZE(guidW)) > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, guidW, -1, riidA, ARRAYSIZE(riidA), nullptr, nullptr);
    }

    const BOOL isSearchProviderRequest = IsEqualGUID(riid, __uuidof(ITfFnSearchCandidateProvider)) ? TRUE : FALSE;
    const BOOL isShowHelpRequest = IsEqualGUID(riid, __uuidof(ITfFnShowHelp)) ? TRUE : FALSE;
    Global::LogToFileVerbose("FunctionProvider::GetFunction rguid=%s riid=%s search_provider=%d show_help=%d",
                             rguidA[0] ? rguidA : "<none>",
                             riidA[0] ? riidA : "<none>",
                             isSearchProviderRequest,
                             isShowHelpRequest);

    HRESULT hr = QueryInterface(riid, (void **)ppunk);
    Global::LogToFileVerbose("FunctionProvider::GetFunction QueryInterface hr=0x%08X ppunk=%p",
                             static_cast<unsigned>(hr),
                             (ppunk != nullptr) ? *ppunk : nullptr);
    return hr;
}

//+---------------------------------------------------------------------------
//扩展功能对象基类
// ITfFunction::GetDisplayName
//
//----------------------------------------------------------------------------
HRESULT CSampleIME::GetDisplayName(_Out_ BSTR *pbstrDisplayName)
{
    HRESULT hr = E_INVALIDARG;
    if (pbstrDisplayName != nullptr)
    {
        *pbstrDisplayName = nullptr;
        hr = E_NOTIMPL;
    }
    return hr;
}

//+---------------------------------------------------------------------------
HRESULT CSampleIME::Show(_In_ HWND hwndParent)
{
    Global::LogToFileVerbose("FunctionProvider::ShowHelp hwnd=0x%p", hwndParent);
    return S_OK;
}

HRESULT CSampleIME::GetSearchCandidates(BSTR bstrQuery, BSTR bstrApplicationID, _Outptr_result_maybenull_ ITfCandidateList **pplist)
{
    if (pplist == nullptr)
    {
        return E_INVALIDARG;
    }

    *pplist = nullptr;

    const UINT queryLen = (bstrQuery != nullptr) ? static_cast<UINT>(SysStringLen(bstrQuery)) : 0;
    const UINT appIdLen = (bstrApplicationID != nullptr) ? static_cast<UINT>(SysStringLen(bstrApplicationID)) : 0;
    Global::LogToFileVerbose("FunctionProvider::GetSearchCandidates query_len=%u appid_len=%u",
                             queryLen,
                             appIdLen);

    if (queryLen == 0)
    {
        return S_OK;
    }

    ITfCandidateList *pList = nullptr;
    HRESULT hr = CTipCandidateList::CreateInstance(&pList, 1);
    if (FAILED(hr) || pList == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider::GetSearchCandidates create_list_failed hr=0x%08X", static_cast<unsigned>(hr));
        return FAILED(hr) ? hr : E_FAIL;
    }

    ITfCandidateString *pCandidate = nullptr;
    hr = CTipCandidateString::CreateInstance(IID_ITfCandidateString, (void **)&pCandidate);
    if (FAILED(hr) || pCandidate == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider::GetSearchCandidates create_candidate_failed hr=0x%08X", static_cast<unsigned>(hr));
        pList->Release();
        return FAILED(hr) ? hr : E_FAIL;
    }

    static_cast<CTipCandidateString *>(pCandidate)->SetIndex(0);
    static_cast<CTipCandidateString *>(pCandidate)->SetString(bstrQuery, queryLen);
    static_cast<CTipCandidateList *>(pList)->SetCandidate(&pCandidate);

    *pplist = pList;
    Global::LogToFileVerbose("FunctionProvider::GetSearchCandidates return_count=1");
    return S_OK;
}

HRESULT CSampleIME::SetResult(BSTR bstrQuery, BSTR bstrApplicationID, BSTR bstrResult)
{
    bstrQuery;
    bstrApplicationID;
    bstrResult;

    Global::LogToFileVerbose("FunctionProvider::SetResult");
    return S_OK;
}

//获取首选触摸键盘布局
// ITfFnGetPreferredTouchKeyboardLayout::GetLayout
// The tkblayout will be Optimized layout.
//----------------------------------------------------------------------------
HRESULT CSampleIME::GetLayout(_Out_ TKBLayoutType *ptkblayoutType, _Out_ WORD *pwPreferredLayoutId)
{
    HRESULT hr = E_INVALIDARG;
    if ((ptkblayoutType != nullptr) && (pwPreferredLayoutId != nullptr))
    {
        *ptkblayoutType = TKBLT_OPTIMIZED;
        *pwPreferredLayoutId = TKBL_OPT_SIMPLIFIED_CHINESE_PINYIN;
        hr = S_OK;
    }
    return hr;
}
