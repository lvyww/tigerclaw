// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "globals.h"
#include "SampleIME.h"

//+---------------------------------------------------------------------------
//编辑会话完成消息接收器
// ITfTextEditSink::OnEndEdit
//
// Called by the system whenever anyone releases a write-access document lock.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnEndEdit(__RPC__in_opt ITfContext *pContext, TfEditCookie ecReadOnly, __RPC__in_opt ITfEditRecord *pEditRecord)
{
    pContext;
    ecReadOnly;
    pEditRecord;
    // Bridge mode: composition is owned by BimeCore; ignore legacy text-edit callbacks.
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// _InitTextEditSink
//
// Init a text edit sink on the topmost context of the document.
// Always release any previous sink.
//----------------------------------------------------------------------------

BOOL CSampleIME::_InitTextEditSink(_In_ ITfDocumentMgr *pDocMgr)
{
    ITfSource* pSource = nullptr;

    // Always clear any previous sink first.
    if (_textEditSinkCookie != TF_INVALID_COOKIE)
    {
        if (SUCCEEDED(_pTextEditSinkContext->QueryInterface(IID_ITfSource, (void **)&pSource)))
        {
            pSource->UnadviseSink(_textEditSinkCookie);
            pSource->Release();
        }

        _pTextEditSinkContext->Release();
        _pTextEditSinkContext = nullptr;
        _textEditSinkCookie = TF_INVALID_COOKIE;
    }

    pDocMgr;
    // Bridge mode: do not install ITfTextEditSink, avoid legacy composition callbacks.
    return TRUE;
}
