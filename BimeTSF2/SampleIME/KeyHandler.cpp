// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"
#include "SampleIME.h"

//////////////////////////////////////////////////////////////////////
//
// CSampleIME class (bridge mode stubs)
//
//////////////////////////////////////////////////////////////////////

BOOL CSampleIME::_IsRangeCovered(TfEditCookie ec, _In_ ITfRange *pRangeTest, _In_ ITfRange *pRangeCover)
{
    ec;
    pRangeTest;
    pRangeCover;
    return FALSE;
}

VOID CSampleIME::_DeleteCandidateList(BOOL isForce, _In_opt_ ITfContext *pContext)
{
    isForce;
    pContext;

    // Bridge mode: candidate UI is owned by BimeCore.
    _pCandidateListUIPresenter = nullptr;
    _candidateMode = CANDIDATE_NONE;
    _isCandidateWithWildcard = FALSE;
}

HRESULT CSampleIME::_HandleComplete(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return S_OK;
}

HRESULT CSampleIME::_HandleCancel(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionInput(TfEditCookie ec, _In_ ITfContext *pContext, WCHAR wch)
{
    ec;
    pContext;
    wch;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionInputWorker(_In_ CCompositionProcessorEngine *pCompositionProcessorEngine, TfEditCookie ec, _In_ ITfContext *pContext)
{
    pCompositionProcessorEngine;
    ec;
    pContext;
    return S_OK;
}

HRESULT CSampleIME::_CreateAndStartCandidate(_In_ CCompositionProcessorEngine *pCompositionProcessorEngine, TfEditCookie ec, _In_ ITfContext *pContext)
{
    pCompositionProcessorEngine;
    ec;
    pContext;

    // Bridge mode: candidate UI is owned by BimeCore.
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCompositionFinalize(TfEditCookie ec, _In_ ITfContext *pContext, BOOL isCandidateList)
{
    ec;
    pContext;
    isCandidateList;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionConvert(TfEditCookie ec, _In_ ITfContext *pContext, BOOL isWildcardSearch)
{
    ec;
    pContext;
    isWildcardSearch;

    // Bridge mode: candidate UI is owned by BimeCore.
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCompositionBackspace(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionArrowKey(TfEditCookie ec, _In_ ITfContext *pContext, KEYSTROKE_FUNCTION keyFunction)
{
    ec;
    pContext;
    keyFunction;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionPunctuation(TfEditCookie ec, _In_ ITfContext *pContext, WCHAR wch)
{
    ec;
    pContext;
    wch;
    return S_OK;
}

HRESULT CSampleIME::_HandleCompositionDoubleSingleByte(TfEditCookie ec, _In_ ITfContext *pContext, WCHAR wch)
{
    ec;
    pContext;
    wch;
    return S_OK;
}

HRESULT CSampleIME::_HandleCandidateFinalize(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCandidateConvert(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCandidateArrowKey(TfEditCookie ec, _In_ ITfContext *pContext, _In_ KEYSTROKE_FUNCTION keyFunction)
{
    ec;
    pContext;
    keyFunction;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCandidateSelectByNumber(TfEditCookie ec, _In_ ITfContext *pContext, _In_ UINT uCode)
{
    ec;
    pContext;
    uCode;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandlePhraseFinalize(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandlePhraseArrowKey(TfEditCookie ec, _In_ ITfContext *pContext, _In_ KEYSTROKE_FUNCTION keyFunction)
{
    ec;
    pContext;
    keyFunction;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandlePhraseSelectByNumber(TfEditCookie ec, _In_ ITfContext *pContext, _In_ UINT uCode)
{
    ec;
    pContext;
    uCode;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_HandleCandidateWorker(TfEditCookie ec, _In_ ITfContext *pContext)
{
    ec;
    pContext;
    return E_NOTIMPL;
}

HRESULT CSampleIME::_InvokeKeyHandler(_In_ ITfContext *pContext, UINT code, WCHAR wch, DWORD flags, _KEYSTROKE_STATE keyState)
{
    pContext;
    code;
    wch;
    flags;
    keyState;

    Global::LogToFileVerbose("KeyHandler: _InvokeKeyHandler disabled in bridge mode");
    return S_OK;
}
