// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#pragma once

#include "SampleIMEBaseStructure.h"
#include <deque>

class CLangBarItemButton;
class CPipeClient;
class CCaretLayoutSink;
class CCaretTextEditSink;
class CCaretTextExtentEditSession;
class CCaretAnchorCompositionSink;
class CStartCaretAnchorEditSession;
class CStartInitialCaretAnchorEditSession;
class CEndCaretAnchorEditSession;
class CUpdateCaretAnchorEditSession;
class CCancelCaretAnchorEditSession;
class CDeferredReopenCaretAnchorEditSession;
class CAnchorPrimeTextExtentEditSession;
struct BimeResponse;
struct FailedKeyMessage
{
    ULONGLONG tick;
    ULONGLONG eventId;
    UINT vkCode;
    UINT scanCode;
    BOOL isKeyDown;
    BOOL shift;
    BOOL ctrl;
    BOOL alt;
    BOOL win;
    BOOL capsLock;
    BOOL numLock;
    UINT repeat;
    BOOL extended;
    BOOL caretValid;
    LONG caretX;
    LONG caretY;
};


const DWORD WM_CheckGlobalCompartment = WM_USER;
const DWORD WM_DeferredReopenCaretAnchorComposition = WM_USER + 1;
const UINT_PTR kFailedKeyFlushTimerId = 5;
const DWORD WM_PrimeCaretTrackingFromAnchor = WM_USER + 2;
LRESULT CALLBACK CSampleIME_WindowProc(HWND wndHandle, UINT uMsg, WPARAM wParam, LPARAM lParam);

enum BimeCaretSource
{
    CARET_SOURCE_UNKNOWN = 0,
    CARET_SOURCE_LAYOUT = 1,
    CARET_SOURCE_END_EDIT = 2,
    CARET_SOURCE_ANCHOR = 3,
    CARET_SOURCE_COMMIT = 4
};

class CSampleIME : public ITfTextInputProcessorEx,//文本输入处理器
    public ITfThreadMgrEventSink,//线程管理器事件接收器
    public ITfKeyEventSink,//键盘事件接收器
    public ITfDisplayAttributeProvider,//display attribute provider
    public ITfFunctionProvider,//文本服务语言配置操作
    public ITfFnShowHelp,//Windows shell help function
    public ITfFnSearchCandidateProvider,//Windows search candidate provider
    public ITfFnGetPreferredTouchKeyboardLayout,//获取首选触摸键盘布局
    public ITfActiveLanguageProfileNotifySink//active language profile notify sink (status window activation)
{
public:
    CSampleIME();
    ~CSampleIME();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, _Outptr_ void **ppvObj);
    STDMETHODIMP_(ULONG) AddRef(void);
    STDMETHODIMP_(ULONG) Release(void);

    // ITfTextInputProcessor
    STDMETHODIMP Activate(ITfThreadMgr *pThreadMgr, TfClientId tfClientId) {
        return ActivateEx(pThreadMgr, tfClientId, 0);
    }
    // ITfTextInputProcessorEx
    STDMETHODIMP ActivateEx(ITfThreadMgr *pThreadMgr, TfClientId tfClientId, DWORD dwFlags);
    STDMETHODIMP Deactivate();

    // ITfThreadMgrEventSink
    STDMETHODIMP OnInitDocumentMgr(_In_ ITfDocumentMgr *pDocMgr);
    STDMETHODIMP OnUninitDocumentMgr(_In_ ITfDocumentMgr *pDocMgr);
    STDMETHODIMP OnSetFocus(_In_ ITfDocumentMgr *pDocMgrFocus, _In_ ITfDocumentMgr *pDocMgrPrevFocus);
    STDMETHODIMP OnPushContext(_In_ ITfContext *pContext);
    STDMETHODIMP OnPopContext(_In_ ITfContext *pContext);

    // ITfKeyEventSink
    STDMETHODIMP OnSetFocus(BOOL fForeground);
    STDMETHODIMP OnTestKeyDown(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten);
    STDMETHODIMP OnKeyDown(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten);
    STDMETHODIMP OnTestKeyUp(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten);
    STDMETHODIMP OnKeyUp(ITfContext *pContext, WPARAM wParam, LPARAM lParam, BOOL *pIsEaten);
    STDMETHODIMP OnPreservedKey(ITfContext *pContext, REFGUID rguid, BOOL *pIsEaten);

    // ITfDisplayAttributeProvider
    STDMETHODIMP EnumDisplayAttributeInfo(__RPC__deref_out_opt IEnumTfDisplayAttributeInfo **ppEnum);
    STDMETHODIMP GetDisplayAttributeInfo(__RPC__in REFGUID guidInfo, __RPC__deref_out_opt ITfDisplayAttributeInfo **ppInfo);

    // ITfFunctionProvider
    STDMETHODIMP GetType(__RPC__out GUID *pguid);
    STDMETHODIMP GetDescription(__RPC__deref_out_opt BSTR *pbstrDesc);
    STDMETHODIMP GetFunction(__RPC__in REFGUID rguid, __RPC__in REFIID riid, __RPC__deref_out_opt IUnknown **ppunk);

    // ITfFunction
    STDMETHODIMP GetDisplayName(_Out_ BSTR *pbstrDisplayName);

    // ITfFnShowHelp
    STDMETHODIMP Show(_In_ HWND hwndParent);

    // ITfFnSearchCandidateProvider
    STDMETHODIMP GetSearchCandidates(BSTR bstrQuery, BSTR bstrApplicationID, _Outptr_result_maybenull_ ITfCandidateList **pplist);
    STDMETHODIMP SetResult(BSTR bstrQuery, BSTR bstrApplicationID, BSTR bstrResult);

    // ITfFnGetPreferredTouchKeyboardLayout, it is the Optimized layout feature.
    STDMETHODIMP GetLayout(_Out_ TKBLayoutType *ptkblayoutType, _Out_ WORD *pwPreferredLayoutId);

    // ITfActiveLanguageProfileNotifySink
    STDMETHODIMP OnActivated(_In_ REFCLSID clsid, _In_ REFGUID guidProfile, _In_ BOOL isActivated);

    // CClassFactory factory callback
    static HRESULT CreateInstance(_In_ IUnknown *pUnkOuter, REFIID riid, _Outptr_ void **ppvObj);

    // utility function for thread manager.
    ITfThreadMgr* _GetThreadMgr() { return _pThreadMgr; }
    TfClientId _GetClientId() { return _tfClientId; }

    BOOL _EnsurePipeConnected();
    BOOL _TryLaunchBimeCore();
    static DWORD WINAPI _LaunchCoreWorkerProc(_In_ LPVOID param);
    void _ScheduleCoreLaunch();
    BOOL _ResolveBimeCoreRelativePath(_Out_writes_(pathCount) WCHAR *path, size_t pathCount);
    void _SendFocusMessage();
    void _PublishImeActive();
    void _ScheduleImeActivePublishRetry();
    void _CancelImeActivePublishRetry();
    void _ScheduleFocusStateQuery();
    void _HandleDeferredFocusStateQuery();
    void _SendCaretMessage(LONG x, LONG y, LONG width = 2, LONG height = 20, int source = CARET_SOURCE_UNKNOWN);
    BOOL _IsSecureMode(void) { return (_dwActivateFlags & TF_TMAE_SECUREMODE) ? TRUE : FALSE; }
    BOOL _IsComLess(void) { return (_dwActivateFlags & TF_TMAE_COMLESS) ? TRUE : FALSE; }
    BOOL _IsStoreAppMode(void) { return (_dwActivateFlags & TF_TMF_IMMERSIVEMODE) ? TRUE : FALSE; };
private:
    BOOL _IsKeyboardDisabled(_In_opt_ ITfContext *pContextHint = nullptr);
    BOOL _IsStartupGuardActive();
    BOOL _InitCtrlSpacePreservedKey();
    void _UninitCtrlSpacePreservedKey();
    BOOL _InitBridgeLanguageBar();
    void _UninitBridgeLanguageBar();
    void _SyncKeyboardOpenCompartment(BOOL isOpen);
    BOOL _InitCaretCoalesceWindow();
    void _UninitCaretCoalesceWindow();
    void _ResetCaretCoalesceState();
    void _FlushPendingCaretMessage(BOOL forceSend);

    HRESULT _AddComposingAndChar(TfEditCookie ec, _In_ ITfContext *pContext, _In_ CStringRange *pstrAddString);
    HRESULT _AddCharAndFinalize(TfEditCookie ec, _In_ ITfContext *pContext, _In_ CStringRange *pstrAddString);

    BOOL _FindComposingRange(TfEditCookie ec, _In_ ITfContext *pContext, _In_ ITfRange *pSelection, _Outptr_result_maybenull_ ITfRange **ppRange);
    HRESULT _SetInputString(TfEditCookie ec, _In_ ITfContext *pContext, _Out_opt_ ITfRange *pRange, _In_ CStringRange *pstrAddString, BOOL exist_composing);
    HRESULT _InsertAtSelection(TfEditCookie ec, _In_ ITfContext *pContext, _In_ CStringRange *pstrAddString, _Outptr_ ITfRange **ppCompRange);

    HRESULT _RemoveDummyCompositionForComposing(TfEditCookie ec, _In_ ITfComposition *pComposition);

    // Invoke key handler edit session
    HRESULT _InvokeKeyHandler(_In_ ITfContext *pContext, UINT code, WCHAR wch, DWORD flags, _KEYSTROKE_STATE keyState);

    // function for the language property
    BOOL _SetCompositionLanguage(TfEditCookie ec, _In_ ITfContext *pContext);

    // function for the display attribute
    void _ClearCompositionDisplayAttributes(TfEditCookie ec, _In_ ITfContext *pContext);
    BOOL _SetCompositionDisplayAttributes(TfEditCookie ec, _In_ ITfContext *pContext, TfGuidAtom gaDisplayAttribute);
    BOOL _InitThreadMgrEventSink();
    void _UninitThreadMgrEventSink();

    BOOL _InitActiveLanguageProfileNotifySink();
    void _UninitActiveLanguageProfileNotifySink();

    void _UpdateLanguageBarOnSetFocus(_In_ ITfDocumentMgr *pDocMgrFocus);

    BOOL _InitKeyEventSink();
    void _UninitKeyEventSink();
    void _ClearPendingResponseCache();
    void _StorePendingResponseCache(BOOL isKeyDown, WPARAM wParam, UINT scanCode, BOOL extended, _In_ const BimeResponse &response);
    BOOL _TryConsumePendingResponseCache(BOOL isKeyDown, WPARAM wParam, LPARAM lParam, _Out_ BimeResponse *pResponse);
    void _RefreshKeyUpWindowForKeyDown(UINT vkCode, UINT scanCode, BOOL extended, _In_ const BimeResponse &response);
    void _RefreshKeyUpWindowForFailedKeyDown(UINT vkCode, UINT scanCode, BOOL extended);
    BOOL _ShouldForwardKeyUp(WPARAM wParam, LPARAM lParam) const;
    void _AdvanceKeyUpWindow(WPARAM wParam, LPARAM lParam);
    void _ClearPendingKeyEvent();
    void _StorePendingKeyEvent(BOOL isKeyDown, WPARAM wParam, UINT scanCode, BOOL extended, ULONGLONG eventId);
    BOOL _TryConsumePendingKeyEvent(BOOL isKeyDown, WPARAM wParam, LPARAM lParam, _Out_ ULONGLONG *pEventId);
    void _ScheduleCompositionRefresh(BOOL decodePending);
    void _CancelCompositionRefresh();
    void _HandleCompositionRefresh();
    void _PruneFailedKeyQueue(ULONGLONG nowTick);
    void _EnqueueFailedKeyMessage(UINT vkCode,
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
                                  ULONGLONG eventId = 0);
    BOOL _FlushFailedKeyQueue(_In_opt_ ITfContext *pContext, _In_z_ const char *stageTag);
    void _ScheduleFailedKeyFlush();
    void _HandleFailedKeyFlush();
    BOOL _ApplyResponseAndSyncState(_In_opt_ ITfContext *pContext, _Inout_ BimeResponse *pResponse, _In_z_ const char *stageTag, _Out_opt_ BOOL *pCommittedViaAnchor = nullptr);
    void _AdjustKeySinkModeForForegroundWindow(_In_opt_ HWND hwndForeground);
    void _RefreshImmersiveState(_In_opt_ const char *source, _In_opt_ HWND hwndForeground);

    BOOL _IsRangeCovered(TfEditCookie ec, _In_ ITfRange *pRangeTest, _In_ ITfRange *pRangeCover);
    VOID _DeleteCandidateList(BOOL fForce, _In_opt_ ITfContext *pContext);

    BOOL _InitFunctionProviderSink();
    void _UninitFunctionProviderSink();
    void _StartCaretTracking(_In_opt_ ITfDocumentMgr *pDocMgrFocus);
    void _StartCaretTrackingOnContext(_In_opt_ ITfContext *pContext);
    void _StopCaretTracking();
    void _RefreshCaretTrackingFromThreadFocus();
    void _ScheduleCaretTrackingPrime();
    void _PrimeCaretTrackingFromAnchor();
    void _HandleLayoutChange(_In_ ITfContext *pContext, TfLayoutCode lcode, _In_ ITfContextView *pContextView);
    void _HandleTextEdit(_In_ ITfContext *pContext, TfEditCookie ecReadOnly, _In_opt_ ITfEditRecord *pEditRecord);
    void _OnCaretAnchorCompositionTerminated(_In_opt_ ITfComposition *pComposition);
    void _OnCaretAnchorCompositionExternallyTerminated(_In_opt_ ITfComposition *pComposition);
    BOOL _GetCaretAnchorRange(_Outptr_result_maybenull_ ITfRange **ppRange);
    BOOL _SyncCaretAnchorForResponse(_In_opt_ ITfContext *pContext, _Inout_ BimeResponse *pResponse);
    void _ClearDeferredCaretAnchorReopen();
    BOOL _ScheduleDeferredCaretAnchorReopen(_In_ ITfContext *pContext, _In_ const std::wstring &inputBuffer);
    void _HandleDeferredCaretAnchorReopen();
    HRESULT _StartCaretAnchorCompositionAtInsertionRange(_In_ ITfContext *pContext);
    HRESULT _EnsureCaretAnchorComposition(_In_ ITfContext *pContext);
    void _EndCaretAnchorComposition(_In_opt_ ITfContext *pContext);
    HRESULT _CancelCaretAnchorComposition(_In_ ITfContext *pContext);
    HRESULT _SetInitialCaretAnchorInputString(_In_ ITfContext *pContext, _In_ const WCHAR *pText);
    HRESULT _UpdateCaretAnchorText(_In_ ITfContext *pContext, _In_ const WCHAR *pText);
    HRESULT _CommitAndEndCaretAnchorComposition(_In_ ITfContext *pContext, _In_ const WCHAR *pText);
    HRESULT _ClearCaretAnchorTextForExternallyTerminatedComposition(_In_ ITfContext *pContext, _In_ ITfComposition *pComposition);
    void _SendCompositionCanceledMessage();

    friend LRESULT CALLBACK CSampleIME_WindowProc(HWND wndHandle, UINT uMsg, WPARAM wParam, LPARAM lParam);
    friend class CCaretLayoutSink;
    friend class CCaretTextEditSink;
    friend class CCaretTextExtentEditSession;
    friend class CCaretAnchorCompositionSink;
    friend class CStartCaretAnchorEditSession;
    friend class CStartInitialCaretAnchorEditSession;
    friend class CEndCaretAnchorEditSession;
    friend class CUpdateCaretAnchorEditSession;
    friend class CCancelCaretAnchorEditSession;
    friend class CDeferredReopenCaretAnchorEditSession;
    friend class CAnchorPrimeTextExtentEditSession;
    friend class CCommitCaretAnchorEditSession;

private:
    ITfThreadMgr* _pThreadMgr;
    TfClientId _tfClientId;
    DWORD _dwActivateFlags;

    // The cookie of ThreadMgrEventSink
    DWORD _threadMgrEventSinkCookie;

    // The cookie of ActiveLanguageProfileNotifySink (active language profile notify)
    DWORD _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;

    // Language bar item object.
    CLangBarItemButton* _pLangBarItem;

    ITfDocumentMgr* _pDocMgrLastFocused;
    ITfContext* _pCaretTrackingContext = nullptr;
    ITfTextLayoutSink* _pCaretLayoutSink = nullptr;
    DWORD _caretLayoutSinkCookie = TF_INVALID_COOKIE;
    ITfTextEditSink* _pCaretTextEditSink = nullptr;
    DWORD _caretTextEditSinkCookie = TF_INVALID_COOKIE;
    ITfComposition* _pCaretAnchorComposition = nullptr;
    ITfContext* _pCaretAnchorContext = nullptr;
    ITfCompositionSink* _pCaretAnchorCompositionSink = nullptr;

    CPipeClient* _pPipeClient;

    ITfCompartment* _pSIPIMEOnOffCompartment;
    DWORD _dwSIPIMEOnOffCompartmentSinkCookie;

    HWND _msgWndHandle; 

    ULONGLONG _activateTick;
    BOOL _isDevenvHost;
    BOOL _ctrlSpacePreservedKeyRegistered;
    BOOL _keySinkUseForeground;
    BOOL _isImmersiveSession;
    BOOL _trialExpired;
    ULONGLONG _lastCoreLaunchAttemptTick;
    volatile LONG _coreLaunchWorkerRunning;
    LONGLONG _lastFocusHwnd;
    DWORD _lastFocusProcessId;
    ULONGLONG _lastFocusSentTick;
    LONGLONG _pendingFocusHwnd;
    DWORD _pendingFocusProcessId;
    BOOL _focusQueryPending;

    // Status window shows/hides with TSF activation: profile selected? last reported value / sent flag
    BOOL _profileActive;
    BOOL _lastImeActiveSent;
    BOOL _hasSentImeActive;
    int _imeActivePublishRetryCount;

    BOOL _hasSentCaret;
    ULONGLONG _lastCaretSentTick;
    LONG _lastSentCaretX;
    LONG _lastSentCaretY;
    LONG _lastSentCaretWidth;
    LONG _lastSentCaretHeight;
    ULONGLONG _lastLayoutCaretTick;
    ULONGLONG _lastHighPriorityCaretTick;
    ULONGLONG _lastLayoutRequestTick;
    LONG _lastLayoutCaretX;
    LONG _lastLayoutCaretY;
    BOOL _hasPendingCaret;
    LONG _pendingCaretX;
    LONG _pendingCaretY;
    LONG _pendingCaretWidth;
    LONG _pendingCaretHeight;
    int _pendingCaretSource;
    BOOL _forceNextCaret;
    BOOL _caretTrackingPrimePending;
    BOOL _suppressExternalCompositionCanceledNotify;


    // Cache the response generated in OnTestKey* for the following OnKey* call.
    BOOL _pendingResponseValid;
    BOOL _pendingResponseIsKeyDown;
    WPARAM _pendingResponseWParam;
    UINT _pendingResponseScanCode;
    BOOL _pendingResponseExtended;
    BOOL _pendingResponseHandled;
    BOOL _pendingResponseExpectKeyUp;
    BOOL _pendingResponseHasKeyboardOpen;
    BOOL _pendingResponseKeyboardOpen;
    BOOL _pendingResponseCancelComposition;
    BOOL _pendingResponseCompositionTracking;
    BOOL _pendingResponseCompositionPending;
    std::wstring _pendingResponseTextToOutput;
    std::wstring _pendingResponseInputBuffer;
    std::wstring _lastAnchorInputBuffer;
    ITfContext *_pDeferredReopenContext = nullptr;
    std::wstring _deferredReopenInputBuffer;
    std::deque<FailedKeyMessage> _failedKeyQueue;
    bool _failedKeyFlushActive = false;
    BOOL _pendingKeyEventValid = FALSE;
    BOOL _pendingKeyEventIsKeyDown = FALSE;
    WPARAM _pendingKeyEventWParam = 0;
    UINT _pendingKeyEventScanCode = 0;
    BOOL _pendingKeyEventExtended = FALSE;
    ULONGLONG _pendingKeyEventId = 0;
    BOOL _compositionRefreshScheduled = FALSE;
    UINT _keyUpForwardBudget = 0;

    LONG _refCount;

};
