// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"
#include "SampleIME.h"
#include "EditSession.h"

static const UINT kCaretLayoutRequestMinIntervalMs = 30;

static void LogForegroundWindowInfoThreadMgr(_In_opt_ const char *stage)
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

    Global::LogToFileVerbose("ThreadMgrEvent %s fg_hwnd=0x%p tid=%lu pid=%lu class=%s title=%s",
                             stage ? stage : "fg",
                             hwnd,
                             static_cast<unsigned long>(tid),
                             static_cast<unsigned long>(pid),
                             className[0] ? className : "<none>",
                             windowTitle[0] ? windowTitle : "<none>");
}

static void LogActiveProfileStateThreadMgr(_In_opt_ const char *stage)
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
        Global::LogToFileVerbose("ThreadMgrEvent %s active_profile query_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        return;
    }

    TF_INPUTPROCESSORPROFILE profile = {};
    hr = pProfileMgr->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("ThreadMgrEvent %s active_profile get_failed hr=0x%08X",
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
    Global::LogToFileVerbose("ThreadMgrEvent %s active_profile lang=0x%04X clsid=%s profile=%s flags=0x%08X is_ours=%d",
                             stage ? stage : "profile",
                             static_cast<unsigned>(profile.langid),
                             clsidA[0] ? clsidA : "<none>",
                             profileA[0] ? profileA : "<none>",
                             static_cast<unsigned>(profile.dwFlags),
                             isOurs);

    pProfileMgr->Release();
}

static void LogDocMgrFocusState(_In_opt_ ITfDocumentMgr *pDocMgrFocus)
{
    if (!Global::IsVerboseLoggingEnabledRuntime())
    {
        return;
    }

    ITfContext *pTopContext = nullptr;
    HRESULT hrTop = E_FAIL;
    if (pDocMgrFocus != nullptr)
    {
        hrTop = pDocMgrFocus->GetTop(&pTopContext);
    }

    Global::LogToFileVerbose("ThreadMgrEvent focus docmgr=%p hr_top=0x%08X top_context=%p",
                             pDocMgrFocus,
                             static_cast<unsigned>(hrTop),
                             pTopContext);

    if (pTopContext != nullptr)
    {
        pTopContext->Release();
    }
}

class CCaretTextExtentEditSession : public CEditSessionBase
{
public:
    CCaretTextExtentEditSession(_In_ CSampleIME *pTextService, _In_ ITfContext *pContext, _In_ ITfContextView *pContextView)
        : CEditSessionBase(pTextService, pContext)
    {
        _pContextView = pContextView;
        if (_pContextView != nullptr)
        {
            _pContextView->AddRef();
        }
    }

    ~CCaretTextExtentEditSession() override
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

            _pTextService->_SendCaretMessage(rc.left, rc.bottom, width, height, CARET_SOURCE_LAYOUT);
            Global::LogToFileVerbose("CaretTrack: text_ext x=%ld y=%ld w=%ld h=%ld", rc.left, rc.bottom, width, height);
        }

        return S_OK;
    }

private:
    ITfContextView *_pContextView;
};

class CCaretLayoutSink : public ITfTextLayoutSink
{
public:
    explicit CCaretLayoutSink(_In_ CSampleIME *pTextService)
    {
        _pTextService = pTextService;
        _refCount = 1;
        _pTextService->AddRef();
        DllAddRef();
    }

    ~CCaretLayoutSink()
    {
        if (_pTextService != nullptr)
        {
            _pTextService->Release();
            _pTextService = nullptr;
        }
        DllRelease();
    }

    STDMETHODIMP QueryInterface(REFIID riid, _Outptr_ void **ppvObj) override
    {
        if (ppvObj == nullptr)
        {
            return E_INVALIDARG;
        }

        *ppvObj = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfTextLayoutSink))
        {
            *ppvObj = static_cast<ITfTextLayoutSink *>(this);
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
        return ++_refCount;
    }

    STDMETHODIMP_(ULONG) Release(void) override
    {
        LONG cr = --_refCount;
        assert(_refCount >= 0);
        if (cr == 0)
        {
            delete this;
        }
        return cr;
    }

    STDMETHODIMP OnLayoutChange(_In_ ITfContext *pContext, TfLayoutCode lcode, _In_ ITfContextView *pContextView) override
    {
        _pTextService->_HandleLayoutChange(pContext, lcode, pContextView);
        return S_OK;
    }

private:
    CSampleIME *_pTextService;
    LONG _refCount;
};

class CCaretTextEditSink : public ITfTextEditSink
{
public:
    explicit CCaretTextEditSink(_In_ CSampleIME *pTextService)
    {
        _pTextService = pTextService;
        _refCount = 1;
        _pTextService->AddRef();
        DllAddRef();
    }

    ~CCaretTextEditSink()
    {
        if (_pTextService != nullptr)
        {
            _pTextService->Release();
            _pTextService = nullptr;
        }
        DllRelease();
    }

    STDMETHODIMP QueryInterface(REFIID riid, _Outptr_ void **ppvObj) override
    {
        if (ppvObj == nullptr)
        {
            return E_INVALIDARG;
        }
        *ppvObj = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfTextEditSink))
        {
            *ppvObj = static_cast<ITfTextEditSink *>(this);
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
        return ++_refCount;
    }

    STDMETHODIMP_(ULONG) Release(void) override
    {
        LONG cr = --_refCount;
        assert(_refCount >= 0);
        if (cr == 0)
        {
            delete this;
        }
        return cr;
    }

    STDMETHODIMP OnEndEdit(_In_ ITfContext *pContext, TfEditCookie ecReadOnly, _In_ ITfEditRecord *pEditRecord) override
    {
        _pTextService->_HandleTextEdit(pContext, ecReadOnly, pEditRecord);
        return S_OK;
    }

private:
    CSampleIME *_pTextService;
    LONG _refCount;
};

void CSampleIME::_StartCaretTracking(_In_opt_ ITfDocumentMgr *pDocMgrFocus)
{
    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext == nullptr)
    {
        _StopCaretTracking();
        return;
    }

    if (pDocMgrFocus == nullptr)
    {
        _StopCaretTracking();
        return;
    }

    ITfContext *pContext = nullptr;
    if (SUCCEEDED(pDocMgrFocus->GetTop(&pContext)) && pContext != nullptr)
    {
        _StartCaretTrackingOnContext(pContext);
        pContext->Release();
    }
    else
    {
        _StopCaretTracking();
    }
}

void CSampleIME::_StartCaretTrackingOnContext(_In_opt_ ITfContext *pContext)
{
    if (pContext == nullptr)
    {
        _StopCaretTracking();
        return;
    }

    if (_pCaretTrackingContext == pContext && _caretLayoutSinkCookie != TF_INVALID_COOKIE)
    {
        return;
    }

    _StopCaretTracking();

    ITfSource *pSource = nullptr;
    if (FAILED(pContext->QueryInterface(IID_ITfSource, (void **)&pSource)) || pSource == nullptr)
    {
        return;
    }

    if (_pCaretLayoutSink == nullptr)
    {
        _pCaretLayoutSink = new (std::nothrow) CCaretLayoutSink(this);
        if (_pCaretLayoutSink == nullptr)
        {
            pSource->Release();
            return;
        }
    }

    HRESULT hr = pSource->AdviseSink(IID_ITfTextLayoutSink, _pCaretLayoutSink, &_caretLayoutSinkCookie);
    if (FAILED(hr))
    {
        pSource->Release();
        Global::LogToFileVerbose("CaretTrack: AdviseSink layout failed hr=0x%08X", static_cast<unsigned>(hr));
        _caretLayoutSinkCookie = TF_INVALID_COOKIE;
        return;
    }

    if (_pCaretTextEditSink == nullptr)
    {
        _pCaretTextEditSink = new (std::nothrow) CCaretTextEditSink(this);
    }

    if (_pCaretTextEditSink != nullptr)
    {
        hr = pSource->AdviseSink(IID_ITfTextEditSink, _pCaretTextEditSink, &_caretTextEditSinkCookie);
        if (FAILED(hr))
        {
            _caretTextEditSinkCookie = TF_INVALID_COOKIE;
            Global::LogToFileVerbose("CaretTrack: AdviseSink text_edit failed hr=0x%08X", static_cast<unsigned>(hr));
        }
    }

    pSource->Release();

    _pCaretTrackingContext = pContext;
    _pCaretTrackingContext->AddRef();

    ITfContextView *pContextView = nullptr;
    if (SUCCEEDED(pContext->GetActiveView(&pContextView)) && pContextView != nullptr)
    {
        _HandleLayoutChange(pContext, TF_LC_CHANGE, pContextView);
        pContextView->Release();
    }

    Global::LogToFileVerbose("CaretTrack: started");
}

void CSampleIME::_StopCaretTracking()
{
    if (_pCaretTrackingContext != nullptr && (_caretLayoutSinkCookie != TF_INVALID_COOKIE || _caretTextEditSinkCookie != TF_INVALID_COOKIE))
    {
        ITfSource *pSource = nullptr;
        if (SUCCEEDED(_pCaretTrackingContext->QueryInterface(IID_ITfSource, (void **)&pSource)) && pSource != nullptr)
        {
            if (_caretLayoutSinkCookie != TF_INVALID_COOKIE)
            {
                pSource->UnadviseSink(_caretLayoutSinkCookie);
            }
            if (_caretTextEditSinkCookie != TF_INVALID_COOKIE)
            {
                pSource->UnadviseSink(_caretTextEditSinkCookie);
            }
            pSource->Release();
        }
    }

    _caretLayoutSinkCookie = TF_INVALID_COOKIE;
    _caretTextEditSinkCookie = TF_INVALID_COOKIE;

    if (_pCaretTrackingContext != nullptr)
    {
        _pCaretTrackingContext->Release();
        _pCaretTrackingContext = nullptr;
    }
}

void CSampleIME::_RefreshCaretTrackingFromThreadFocus()
{
    if (_pThreadMgr == nullptr)
    {
        _StopCaretTracking();
        return;
    }

    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext == nullptr)
    {
        _StopCaretTracking();
        return;
    }

    if (_pCaretTrackingContext == _pCaretAnchorContext && _caretLayoutSinkCookie != TF_INVALID_COOKIE)
    {
        return;
    }

    _StartCaretTrackingOnContext(_pCaretAnchorContext);
}

void CSampleIME::_HandleLayoutChange(_In_ ITfContext *pContext, TfLayoutCode lcode, _In_ ITfContextView *pContextView)
{
    if (pContext == nullptr || pContext != _pCaretTrackingContext)
    {
        return;
    }

    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext != pContext)
    {
        return;
    }

    if (lcode == TF_LC_DESTROY)
    {
        Global::LogToFileVerbose("CaretTrack: layout destroyed, refresh tracking");
        _RefreshCaretTrackingFromThreadFocus();
        return;
    }

    if (lcode != TF_LC_CHANGE || pContextView == nullptr)
    {
        return;
    }

    ULONGLONG nowTick = GetTickCount64();
    if (_lastLayoutRequestTick != 0)
    {
        ULONGLONG elapsed = (nowTick >= _lastLayoutRequestTick) ? (nowTick - _lastLayoutRequestTick) : 0;
        if (elapsed < kCaretLayoutRequestMinIntervalMs)
        {
            return;
        }
    }
    _lastLayoutRequestTick = nowTick;

    CCaretTextExtentEditSession *pEditSession = new (std::nothrow) CCaretTextExtentEditSession(this, pContext, pContextView);
    if (pEditSession == nullptr)
    {
        return;
    }

    HRESULT hrSession = S_OK;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, pEditSession, TF_ES_ASYNC | TF_ES_READ, &hrSession);

    pEditSession->Release();

    if (FAILED(hr))
    {
        Global::LogToFileVerbose("CaretTrack: RequestEditSession failed hr=0x%08X", static_cast<unsigned>(hr));
    }
}

//+---------------------------------------------------------------------------
//�̹߳������¼�������
// ITfThreadMgrEventSink::OnInitDocumentMgr
//
// Sink called by the framework just before the first context is pushed onto
// a document.
//----------------------------------------------------------------------------

void CSampleIME::_HandleTextEdit(_In_ ITfContext *pContext, TfEditCookie ecReadOnly, _In_opt_ ITfEditRecord *pEditRecord)
{
    if (pContext == nullptr || pContext != _pCaretTrackingContext)
    {
        return;
    }

    if (_pCaretAnchorComposition == nullptr || _pCaretAnchorContext != pContext)
    {
        return;
    }

    if (pEditRecord != nullptr)
    {
        BOOL selectionChanged = FALSE;
        if (SUCCEEDED(pEditRecord->GetSelectionStatus(&selectionChanged)) && !selectionChanged)
        {
            return;
        }
    }

    ITfRange *pRange = nullptr;
    if (!_GetCaretAnchorRange(&pRange))
    {
        return;
    }


    TF_SELECTION tfSelection = {};
    ULONG fetched = 0;
    HRESULT hrSelection = pContext->GetSelection(ecReadOnly, TF_DEFAULT_SELECTION, 1, &tfSelection, &fetched);
    if (SUCCEEDED(hrSelection) && fetched == 1 && tfSelection.range != nullptr)
    {
        if (!_IsRangeCovered(ecReadOnly, tfSelection.range, pRange))
        {
            tfSelection.range->Release();
            pRange->Release();

            Global::LogToFileVerbose("CaretTrack: selection moved outside anchor, cancel composition");
            HRESULT hrCancel = _CancelCaretAnchorComposition(pContext);
            Global::LogToFileVerbose("CaretTrack: cancel_on_selection_change hr=0x%08X", static_cast<unsigned>(hrCancel));
            if (SUCCEEDED(hrCancel))
            {
                _SendCompositionCanceledMessage();
            }
            return;
        }

        tfSelection.range->Release();
    }

    ITfContextView *pContextView = nullptr;
    HRESULT hr = pContext->GetActiveView(&pContextView);
    if (SUCCEEDED(hr) && pContextView != nullptr)
    {
        RECT rc = {0, 0, 0, 0};
        BOOL isClipped = TRUE;
        hr = pContextView->GetTextExt(ecReadOnly, pRange, &rc, &isClipped);
        if (SUCCEEDED(hr))
        {
            LONG width = rc.right - rc.left;
            LONG height = rc.bottom - rc.top;
            if (width <= 0){ width = 2; }
            if (height <= 0){ height = 20; }
            _SendCaretMessage(rc.left, rc.bottom, width, height, CARET_SOURCE_END_EDIT);
            Global::LogToFileVerbose("CaretTrack: end_edit x=%ld y=%ld w=%ld h=%ld", rc.left, rc.bottom, width, height);
        }
        pContextView->Release();
    }

    pRange->Release();
}

STDAPI CSampleIME::OnInitDocumentMgr(_In_ ITfDocumentMgr *pDocMgr)
{
    pDocMgr;
    return E_NOTIMPL;
}

//+---------------------------------------------------------------------------
//
// ITfThreadMgrEventSink::OnUninitDocumentMgr
//
// Sink called by the framework just after the last context is popped off a
// document.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnUninitDocumentMgr(_In_ ITfDocumentMgr *pDocMgr)
{
    pDocMgr;
    return E_NOTIMPL;
}

//+---------------------------------------------------------------------------
//
// ITfThreadMgrEventSink::OnSetFocus
//
// Sink called by the framework when focus changes from one document to
// another.  Either document may be NULL, meaning previously there was no
// focus document, or now no document holds the input focus.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnSetFocus(_In_ ITfDocumentMgr *pDocMgrFocus, _In_ ITfDocumentMgr *pDocMgrPrevFocus)
{
    pDocMgrPrevFocus;
    HWND hwndForeground = GetForegroundWindow();
    LogDocMgrFocusState(pDocMgrFocus);
    LogForegroundWindowInfoThreadMgr("OnSetFocus");
    LogActiveProfileStateThreadMgr("OnSetFocus");

    _AdjustKeySinkModeForForegroundWindow(hwndForeground);
    _UpdateLanguageBarOnSetFocus(pDocMgrFocus);

    // Bridge mode: candidate UI focus behavior is owned by BimeCore.
    Global::LogToFileVerbose("ThreadMgrEvent: skip_candidate_focus_bridge_mode");

    if (_pDocMgrLastFocused)
    {
        _pDocMgrLastFocused->Release();
        _pDocMgrLastFocused = nullptr;
    }

    _pDocMgrLastFocused = pDocMgrFocus;

    if (_pDocMgrLastFocused)
    {
        _pDocMgrLastFocused->AddRef();
    }

    _StopCaretTracking();
    _SendFocusMessage();

    // 状态窗随激活显隐：焦点落在可编辑文档（docMgr 非空且有 top context）才算可输入。
    BOOL focusEditable = FALSE;
    if (pDocMgrFocus != nullptr)
    {
        ITfContext *pTopContext = nullptr;
        if (SUCCEEDED(pDocMgrFocus->GetTop(&pTopContext)) && pTopContext != nullptr)
        {
            focusEditable = TRUE;
            pTopContext->Release();
        }
    }
    _focusEditable = focusEditable;
    _PublishImeActive();

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfThreadMgrEventSink::OnPushContext
//
// Sink called by the framework when a context is pushed.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnPushContext(_In_ ITfContext *pContext)
{
    Global::LogToFileVerbose("ThreadMgrEvent OnPushContext context=%p", pContext);
    LogForegroundWindowInfoThreadMgr("OnPushContext");
    LogActiveProfileStateThreadMgr("OnPushContext");
    _RefreshCaretTrackingFromThreadFocus();
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfThreadMgrEventSink::OnPopContext
//
// Sink called by the framework when a context is popped.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnPopContext(_In_ ITfContext *pContext)
{
    Global::LogToFileVerbose("ThreadMgrEvent OnPopContext context=%p", pContext);
    LogForegroundWindowInfoThreadMgr("OnPopContext");
    LogActiveProfileStateThreadMgr("OnPopContext");
    _RefreshCaretTrackingFromThreadFocus();
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// _InitThreadMgrEventSink
//
// Advise our sink.
//----------------------------------------------------------------------------

BOOL CSampleIME::_InitThreadMgrEventSink()
{
    ITfSource* pSource = nullptr;
    BOOL ret = FALSE;

    if (FAILED(_pThreadMgr->QueryInterface(IID_ITfSource, (void **)&pSource)))
    {
        return ret;
    }

    if (FAILED(pSource->AdviseSink(IID_ITfThreadMgrEventSink, (ITfThreadMgrEventSink *)this, &_threadMgrEventSinkCookie)))
    {
        _threadMgrEventSinkCookie = TF_INVALID_COOKIE;
        goto Exit;
    }

    ret = TRUE;

Exit:
    pSource->Release();
    return ret;
}

//+---------------------------------------------------------------------------
//
// _UninitThreadMgrEventSink
//
// Unadvise our sink.
//----------------------------------------------------------------------------

void CSampleIME::_UninitThreadMgrEventSink()
{
    _StopCaretTracking();
    if (_pCaretLayoutSink != nullptr)
    {
        _pCaretLayoutSink->Release();
        _pCaretLayoutSink = nullptr;
    }
    if (_pCaretTextEditSink != nullptr)
    {
        _pCaretTextEditSink->Release();
        _pCaretTextEditSink = nullptr;
    }

    ITfSource* pSource = nullptr;

    if (_threadMgrEventSinkCookie == TF_INVALID_COOKIE)
    {
        return; 
    }

    if (SUCCEEDED(_pThreadMgr->QueryInterface(IID_ITfSource, (void **)&pSource)))
    {
        pSource->UnadviseSink(_threadMgrEventSinkCookie);
        pSource->Release();
    }

    _threadMgrEventSinkCookie = TF_INVALID_COOKIE;
}

