// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "SampleIME.h"
#include "LanguageBar.h"
#include "Globals.h"
#include "Compartment.h"
#include "PipeClient.h"

//+---------------------------------------------------------------------------
//
// CSampleIME::_UpdateLanguageBarOnSetFocus
//
//----------------------------------------------------------------------------

void CSampleIME::_UpdateLanguageBarOnSetFocus(_In_ ITfDocumentMgr *pDocMgrFocus)
{
    // Bridge mode: never force-disable the language bar button.
    // Some hosts (Start/search) may not expose document context, but we still want
    // stable indicator behavior instead of "X".
    BOOL needDisableButtons = FALSE;
    pDocMgrFocus;

    // Bridge mode: keep language bar enabled/disabled state in sync.
    if (_pLangBarItem != nullptr)
    {
        _pLangBarItem->SetStatus(TF_LBI_STATUS_DISABLED, needDisableButtons);
        Global::LogToFileVerbose("LanguageBar: bridge status updated disabled=%d", needDisableButtons);
    }
    else
    {
        Global::LogToFileVerbose("LanguageBar: bridge status skip=no_langbar_item");
    }
}

//+---------------------------------------------------------------------------
//
// CLangBarItemButton::ctor
//语言栏按钮项信息
//----------------------------------------------------------------------------

CLangBarItemButton::CLangBarItemButton(REFGUID guidLangBar, LPCWSTR description, LPCWSTR tooltip, DWORD onIconIndex, DWORD offIconIndex, BOOL isSecureMode)
{
    DWORD bufLen = 0;

    DllAddRef();

    // initialize TF_LANGBARITEMINFO structure.
    _tfLangBarItemInfo.clsidService = Global::SampleIMECLSID;												    // This LangBarItem belongs to this TextService.
    _tfLangBarItemInfo.guidItem = guidLangBar;															        // GUID of this LangBarItem.
    _tfLangBarItemInfo.dwStyle = (TF_LBI_STYLE_BTN_BUTTON | TF_LBI_STYLE_SHOWNINTRAY);						    // This LangBar is a button type.
    _tfLangBarItemInfo.ulSort = 0;																			    // The position of this LangBar Item is not specified.
    StringCchCopy(_tfLangBarItemInfo.szDescription, ARRAYSIZE(_tfLangBarItemInfo.szDescription), description);  // Set the description of this LangBar Item.

    // Initialize the sink pointer to NULL.
    _pLangBarItemSink = nullptr;

    // Initialize ICON index and file name.
    _onIconIndex = onIconIndex;
    _offIconIndex = offIconIndex;

    // Initialize compartment.
    _pCompartment = nullptr;
    _pCompartmentEventSink = nullptr;

    _isAddedToLanguageBar = FALSE;
    _isSecureMode = isSecureMode;
    _keyboardOpen = FALSE;
    _status = 0;

    _refCount = 1;

    // Initialize Tooltip
    _pTooltipText = nullptr;
    if (tooltip)
    {
		size_t len = 0;
		if (StringCchLength(tooltip, STRSAFE_MAX_CCH, &len) != S_OK)
        {
            len = 0; 
        }
        bufLen = static_cast<DWORD>(len) + 1;
        _pTooltipText = (LPCWSTR) new (std::nothrow) WCHAR[ bufLen ];
        if (_pTooltipText)
        {
            StringCchCopy((LPWSTR)_pTooltipText, bufLen, tooltip);
        }
    }   
}

//+---------------------------------------------------------------------------
//
// CLangBarItemButton::dtor
//
//----------------------------------------------------------------------------

CLangBarItemButton::~CLangBarItemButton()
{
    DllRelease();
    CleanUp();
}

//+---------------------------------------------------------------------------
//
// CLangBarItemButton::CleanUp
//
//----------------------------------------------------------------------------

void CLangBarItemButton::CleanUp()
{
    if (_pTooltipText)
    {
        delete [] _pTooltipText;
        _pTooltipText = nullptr;
    }

    ITfThreadMgr* pThreadMgr = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_TF_ThreadMgr, 
        NULL, 
        CLSCTX_INPROC_SERVER, 
        IID_ITfThreadMgr, 
        (void**)&pThreadMgr);
    if (SUCCEEDED(hr))
    {
        _UnregisterCompartment(pThreadMgr);

        _RemoveItem(pThreadMgr);
        pThreadMgr->Release();
        pThreadMgr = nullptr;
    }

    if (_pCompartment)
    {
        delete _pCompartment;
        _pCompartment = nullptr;
    }

    if (_pCompartmentEventSink)
    {
        delete _pCompartmentEventSink;
        _pCompartmentEventSink = nullptr;
    }
}

//+---------------------------------------------------------------------------
//
// CLangBarItemButton::QueryInterface
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::QueryInterface(REFIID riid, _Outptr_ void **ppvObj)
{
    if (ppvObj == nullptr)
    {
        return E_INVALIDARG;
    }

    *ppvObj = nullptr;

    if (IsEqualIID(riid, IID_IUnknown) ||
        IsEqualIID(riid, IID_ITfLangBarItem) ||
        IsEqualIID(riid, IID_ITfLangBarItemButton))
    {
        *ppvObj = (ITfLangBarItemButton *)this;
    }
    else if (IsEqualIID(riid, IID_ITfSource))
    {
        *ppvObj = (ITfSource *)this;
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
// CLangBarItemButton::AddRef
//
//----------------------------------------------------------------------------

STDAPI_(ULONG) CLangBarItemButton::AddRef()
{
    return ++_refCount;
}

//+---------------------------------------------------------------------------
//
// CLangBarItemButton::Release
//
//----------------------------------------------------------------------------

STDAPI_(ULONG) CLangBarItemButton::Release()
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
//
// GetInfo
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::GetInfo(_Out_ TF_LANGBARITEMINFO *pInfo)
{
    _tfLangBarItemInfo.dwStyle |= TF_LBI_STYLE_SHOWNINTRAY;
    *pInfo = _tfLangBarItemInfo;
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// GetStatus
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::GetStatus(_Out_ DWORD *pdwStatus)
{
    if (pdwStatus == nullptr)
    {
        E_INVALIDARG;
    }

    *pdwStatus = _status;
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// SetStatus
//
//----------------------------------------------------------------------------

void CLangBarItemButton::SetStatus(DWORD dwStatus, BOOL fSet)
{
    BOOL isChange = FALSE;

    if (fSet) 
    {
        if (!(_status & dwStatus)) 
        {
            _status |= dwStatus;
            isChange = TRUE;
        }
    } 
    else 
    {
        if (_status & dwStatus) 
        {
            _status &= ~dwStatus;
            isChange = TRUE;
        }
    }

    if (isChange && _pLangBarItemSink) 
    {
        _pLangBarItemSink->OnUpdate(TF_LBI_STATUS | TF_LBI_ICON);
    }

    return;
}

void CLangBarItemButton::SetKeyboardOpenState(BOOL isOpen)
{
    if (_keyboardOpen == isOpen)
    {
        return;
    }

    _keyboardOpen = isOpen;
    if (_pLangBarItemSink)
    {
        _pLangBarItemSink->OnUpdate(TF_LBI_STATUS | TF_LBI_ICON);
    }
}

//+---------------------------------------------------------------------------
//
// Show
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::Show(BOOL fShow)
{
	fShow;
    if (_pLangBarItemSink)
    {
        _pLangBarItemSink->OnUpdate(TF_LBI_STATUS);
    }
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// GetTooltipString
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::GetTooltipString(_Out_ BSTR *pbstrToolTip)
{
    *pbstrToolTip = SysAllocString(_pTooltipText);

    return (*pbstrToolTip == nullptr) ? E_OUTOFMEMORY : S_OK;
}

//+---------------------------------------------------------------------------
//
// OnClick
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::OnClick(TfLBIClick click, POINT pt, _In_ const RECT *prcArea)
{
    pt;
    prcArea;

    if (IsEqualGUID(_tfLangBarItemInfo.guidItem, GUID_LBI_INPUTMODE))
    {
        CPipeClient pipeClient;

        if (click == TF_LBI_CLK_RIGHT)
        {
            if (pipeClient.Connect())
            {
                BimeResponse response;
                HRESULT hr = pipeClient.SendShowMenuAndWait(&response, 200);
                Global::LogToFileVerbose("LangBar OnClick: input_mode right_click show_menu hr=0x%08X", static_cast<unsigned>(hr));
            }
            else
            {
                Global::LogToFileVerbose("LangBar OnClick: input_mode right_click show_menu pipe disconnected");
            }

            return S_OK;
        }

        if (click == TF_LBI_CLK_LEFT)
        {
            if (pipeClient.Connect())
            {
                BimeResponse response;
                HRESULT hr = pipeClient.SendCtrlSpaceAndWait(&response, 200);
                if (SUCCEEDED(hr) && response.hasKeyboardOpen)
                {
                    SetKeyboardOpenState(response.keyboardOpen ? TRUE : FALSE);
                    if (_pCompartment != nullptr)
                    {
                        _pCompartment->_SetCompartmentBOOL(response.keyboardOpen ? TRUE : FALSE);
                    }
                    Global::LogToFileVerbose("LangBar OnClick: input_mode via core keyboard_open=%d", response.keyboardOpen);
                    return S_OK;
                }
                Global::LogToFileVerbose("LangBar OnClick: input_mode core sync unavailable hr=0x%08X", static_cast<unsigned>(hr));
            }
            else
            {
                Global::LogToFileVerbose("LangBar OnClick: input_mode pipe disconnected");
            }

            // Bridge mode contract: status source is BimeCore response only.
            return S_OK;
        }
    }

    if (_pCompartment != nullptr)
    {
        BOOL isOn = _keyboardOpen;
        _pCompartment->_SetCompartmentBOOL(isOn ? FALSE : TRUE);
    }

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// InitMenu
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::InitMenu(_In_ ITfMenu *pMenu)
{
    pMenu;

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// OnMenuSelect
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::OnMenuSelect(UINT wID)
{
    wID;

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// GetIcon
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::GetIcon(_Out_ HICON *phIcon)
{
    if (!phIcon)
    {
        return E_FAIL;
    }
    *phIcon = nullptr;

    BOOL isOn = _keyboardOpen;

    DWORD status = 0;
    GetStatus(&status);

	// If IME is working on the UAC mode, the size of ICON should be 24 x 24.
    int desiredSize = 16;
    if (_isSecureMode) // detect UAC mode
    {
        desiredSize = _isSecureMode ? 24 : 16;
    }

    if (isOn && !(status & TF_LBI_STATUS_DISABLED))
    {
        if (Global::dllInstanceHandle)
        {
            *phIcon = reinterpret_cast<HICON>(LoadImage(Global::dllInstanceHandle, MAKEINTRESOURCE(_onIconIndex), IMAGE_ICON, desiredSize, desiredSize, 0));
        }
    }
    else
    {
        if (Global::dllInstanceHandle)
        {
            *phIcon = reinterpret_cast<HICON>(LoadImage(Global::dllInstanceHandle, MAKEINTRESOURCE(_offIconIndex), IMAGE_ICON, desiredSize, desiredSize, 0));
        }
    }

    return (*phIcon != NULL) ? S_OK : E_FAIL;
}

//+---------------------------------------------------------------------------
//
// GetText
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::GetText(_Out_ BSTR *pbstrText)
{
    *pbstrText = SysAllocString(_tfLangBarItemInfo.szDescription);

    return (*pbstrText == nullptr) ? E_OUTOFMEMORY : S_OK;
}

//+---------------------------------------------------------------------------
//
// AdviseSink
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::AdviseSink(__RPC__in REFIID riid, __RPC__in_opt IUnknown *punk, __RPC__out DWORD *pdwCookie)
{
    // We allow only ITfLangBarItemSink interface.
    if (!IsEqualIID(IID_ITfLangBarItemSink, riid))
    {
        return CONNECT_E_CANNOTCONNECT;
    }

    // We support only one sink once.
    if (_pLangBarItemSink != nullptr)
    {
        return CONNECT_E_ADVISELIMIT;
    }

    // Query the ITfLangBarItemSink interface and store it into _pLangBarItemSink.
    if (punk == nullptr)
    {
        return E_INVALIDARG;
    }
    if (punk->QueryInterface(IID_ITfLangBarItemSink, (void **)&_pLangBarItemSink) != S_OK)
    {
        _pLangBarItemSink = nullptr;
        return E_NOINTERFACE;
    }

    // return our cookie.
    *pdwCookie = _cookie;
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// UnadviseSink
//
//----------------------------------------------------------------------------

STDAPI CLangBarItemButton::UnadviseSink(DWORD dwCookie)
{
    // Check the given cookie.
    if (dwCookie != _cookie)
    {
        return CONNECT_E_NOCONNECTION;
    }

    // If there is nno connected sink, we just fail.
    if (_pLangBarItemSink == nullptr)
    {
        return CONNECT_E_NOCONNECTION;
    }

    _pLangBarItemSink->Release();
    _pLangBarItemSink = nullptr;

    return S_OK;
}

//+---------------------------------------------------------------------------
//
// _AddItem
//
//----------------------------------------------------------------------------

HRESULT CLangBarItemButton::_AddItem(_In_ ITfThreadMgr *pThreadMgr)
{
    HRESULT hr = S_OK;
    ITfLangBarItemMgr* pLangBarItemMgr = nullptr;

    if (_isAddedToLanguageBar)
    {
        return S_OK;
    }

    hr = pThreadMgr->QueryInterface(IID_ITfLangBarItemMgr, (void **)&pLangBarItemMgr);
    if (SUCCEEDED(hr))
    {
        hr = pLangBarItemMgr->AddItem(this);
        if (SUCCEEDED(hr))
        {
            _isAddedToLanguageBar = TRUE;
        }
        pLangBarItemMgr->Release();
    }

    return hr;
}

//+---------------------------------------------------------------------------
//
// _RemoveItem
//
//----------------------------------------------------------------------------

HRESULT CLangBarItemButton::_RemoveItem(_In_ ITfThreadMgr *pThreadMgr)
{
    HRESULT hr = S_OK;
    ITfLangBarItemMgr* pLangBarItemMgr = nullptr;

    if (!_isAddedToLanguageBar)
    {
        return S_OK;
    }

    hr = pThreadMgr->QueryInterface(IID_ITfLangBarItemMgr, (void **)&pLangBarItemMgr);
    if (SUCCEEDED(hr))
    {
        hr = pLangBarItemMgr->RemoveItem(this);
        if (SUCCEEDED(hr))
        {
            _isAddedToLanguageBar = FALSE;
        }
        pLangBarItemMgr->Release();
    }

    return hr;
}

//+---------------------------------------------------------------------------
//
// _RegisterCompartment
//
//----------------------------------------------------------------------------

BOOL CLangBarItemButton::_RegisterCompartment(_In_ ITfThreadMgr *pThreadMgr, TfClientId tfClientId, REFGUID guidCompartment)
{
    _pCompartment = new (std::nothrow) CCompartment(pThreadMgr, tfClientId, guidCompartment);
    if (_pCompartment)
    {
        BOOL compartmentState = FALSE;
        if (SUCCEEDED(_pCompartment->_GetCompartmentBOOL(compartmentState)))
        {
            _keyboardOpen = compartmentState;
        }

        // Advice ITfCompartmentEventSink
        _pCompartmentEventSink = new (std::nothrow) CCompartmentEventSink(_CompartmentCallback, this);
        if (_pCompartmentEventSink)
        {
            _pCompartmentEventSink->_Advise(pThreadMgr, guidCompartment);
        }
        else
        {
            delete _pCompartment;
            _pCompartment = nullptr;
        }
    }

    return _pCompartment ? TRUE : FALSE;
}

//+---------------------------------------------------------------------------
//
// _UnregisterCompartment
//
//----------------------------------------------------------------------------

BOOL CLangBarItemButton::_UnregisterCompartment(_In_ ITfThreadMgr *pThreadMgr)
{
	pThreadMgr;
    if (_pCompartment)
    {
        // Unadvice ITfCompartmentEventSink
        if (_pCompartmentEventSink)
        {
            _pCompartmentEventSink->_Unadvise();
        }

        // clear ITfCompartment
        _pCompartment->_ClearCompartment();
    }

    return TRUE;
}

//+---------------------------------------------------------------------------
//
// _CompartmentCallback
//
//----------------------------------------------------------------------------

// static
HRESULT CLangBarItemButton::_CompartmentCallback(_In_ void *pv, REFGUID guidCompartment)
{
    CLangBarItemButton* fakeThis = (CLangBarItemButton*)pv;

    GUID guid = GUID_NULL;
    fakeThis->_pCompartment->_GetGUID(&guid);

    if (IsEqualGUID(guid, guidCompartment))
    {
        BOOL currentOpen = FALSE;
        if (fakeThis->_pCompartment != nullptr &&
            SUCCEEDED(fakeThis->_pCompartment->_GetCompartmentBOOL(currentOpen)) &&
            currentOpen != fakeThis->_keyboardOpen)
        {
            fakeThis->_pCompartment->_SetCompartmentBOOL(fakeThis->_keyboardOpen);
            Global::LogToFileVerbose("LanguageBar: reject external compartment change current=%d core=%d", currentOpen, fakeThis->_keyboardOpen);
        }

        if (fakeThis->_pLangBarItemSink)
        {
            fakeThis->_pLangBarItemSink->OnUpdate(TF_LBI_STATUS | TF_LBI_ICON);
        }
    }

    return S_OK;
}
