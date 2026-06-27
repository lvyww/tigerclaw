// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "SampleIME.h"
#include "Globals.h"

//+---------------------------------------------------------------------------
//
// _InitFunctionProviderSink
//???????
//----------------------------------------------------------------------------

BOOL CSampleIME::_InitFunctionProviderSink()
{
    ITfSourceSingle* pSourceSingle = nullptr;
    HRESULT hr = E_FAIL;

    if (_pThreadMgr == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider: init failed no_thread_mgr");
        return FALSE;
    }

    Global::LogToFileVerbose("FunctionProvider: init tfClientId=%u store_mode=%d secure_mode=%d comless=%d",
                             static_cast<unsigned>(_tfClientId),
                             _IsStoreAppMode(),
                             _IsSecureMode(),
                             _IsComLess());

    hr = _pThreadMgr->QueryInterface(IID_ITfSourceSingle, (void **)&pSourceSingle);
    if (FAILED(hr) || pSourceSingle == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider: QueryInterface(IID_ITfSourceSingle) failed hr=0x%08X", static_cast<unsigned>(hr));
        return FALSE;
    }

    IUnknown* punk = nullptr;
    hr = QueryInterface(IID_IUnknown, (void **)&punk);
    if (FAILED(hr) || punk == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider: QueryInterface(IID_IUnknown) failed hr=0x%08X", static_cast<unsigned>(hr));
        pSourceSingle->Release();
        return FALSE;
    }

    hr = pSourceSingle->AdviseSingleSink(_tfClientId, IID_ITfFunctionProvider, punk);
    punk->Release();
    pSourceSingle->Release();

    if (FAILED(hr))
    {
        Global::LogToFileVerbose("FunctionProvider: AdviseSingleSink failed hr=0x%08X", static_cast<unsigned>(hr));
        return FALSE;
    }

    Global::LogToFileVerbose("FunctionProvider: sink advised");
    return TRUE;
}

//+---------------------------------------------------------------------------
//
// _UninitFunctionProviderSink
//
//----------------------------------------------------------------------------

void CSampleIME::_UninitFunctionProviderSink()
{
    if (_pThreadMgr == nullptr || _tfClientId == TF_CLIENTID_NULL)
    {
        return;
    }

    ITfSourceSingle* pSourceSingle = nullptr;
    HRESULT hr = _pThreadMgr->QueryInterface(IID_ITfSourceSingle, (void **)&pSourceSingle);
    if (FAILED(hr) || pSourceSingle == nullptr)
    {
        Global::LogToFileVerbose("FunctionProvider: uninit QueryInterface failed hr=0x%08X", static_cast<unsigned>(hr));
        return;
    }

    hr = pSourceSingle->UnadviseSingleSink(_tfClientId, IID_ITfFunctionProvider);
    pSourceSingle->Release();

    Global::LogToFileVerbose("FunctionProvider: sink unadvised hr=0x%08X", static_cast<unsigned>(hr));
}
