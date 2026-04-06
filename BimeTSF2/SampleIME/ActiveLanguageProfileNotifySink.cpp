// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"
#include "SampleIME.h"

static void GuidToUtf8(_In_ REFGUID guid, _Out_writes_(bufferSize) char *buffer, size_t bufferSize)
{
    if (buffer == nullptr || bufferSize == 0)
    {
        return;
    }

    buffer[0] = '\0';
    wchar_t guidW[64] = {0};
    if (StringFromGUID2(guid, guidW, ARRAYSIZE(guidW)) > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, guidW, -1, buffer, static_cast<int>(bufferSize), nullptr, nullptr);
    }
}

static void LogForegroundWindowInfoActiveLang(_In_opt_ const char *stage)
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

    Global::LogToFileVerbose("ActiveLanguageProfile %s fg_hwnd=0x%p tid=%lu pid=%lu class=%s title=%s",
                             stage ? stage : "fg",
                             hwnd,
                             static_cast<unsigned long>(tid),
                             static_cast<unsigned long>(pid),
                             className[0] ? className : "<none>",
                             windowTitle[0] ? windowTitle : "<none>");
}

static void LogActiveProfileStateActiveLang(_In_opt_ const char *stage)
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
        Global::LogToFileVerbose("ActiveLanguageProfile %s active_profile query_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        return;
    }

    TF_INPUTPROCESSORPROFILE profile = {};
    hr = pProfileMgr->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("ActiveLanguageProfile %s active_profile get_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        pProfileMgr->Release();
        return;
    }

    char clsidA[128] = {0};
    char profileA[128] = {0};
    GuidToUtf8(profile.clsid, clsidA, ARRAYSIZE(clsidA));
    GuidToUtf8(profile.guidProfile, profileA, ARRAYSIZE(profileA));

    const BOOL isOurs = (IsEqualCLSID(profile.clsid, Global::SampleIMECLSID) && IsEqualGUID(profile.guidProfile, Global::SampleIMEGuidProfile)) ? TRUE : FALSE;
    Global::LogToFileVerbose("ActiveLanguageProfile %s active_profile lang=0x%04X clsid=%s profile=%s flags=0x%08X is_ours=%d",
                             stage ? stage : "profile",
                             static_cast<unsigned>(profile.langid),
                             clsidA[0] ? clsidA : "<none>",
                             profileA[0] ? profileA : "<none>",
                             static_cast<unsigned>(profile.dwFlags),
                             isOurs);

    pProfileMgr->Release();
}

//+---------------------------------------------------------------------------
//语言配置激活消息接收器::激活
// ITfActiveLanguageProfileNotifySink::OnActivated
//当更改激活语言配置文件时，框架调用接收器。
// Sink called by the framework when changes activate language profile.
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnActivated(_In_ REFCLSID clsid, _In_ REFGUID guidProfile, _In_ BOOL isActivated)
{
    char clsidA[128] = {0};
    char profileA[128] = {0};
    GuidToUtf8(clsid, clsidA, ARRAYSIZE(clsidA));
    GuidToUtf8(guidProfile, profileA, ARRAYSIZE(profileA));

    Global::LogToFileVerbose("ActiveLanguageProfile OnActivated isActivated=%d clsid=%s profile=%s",
                             isActivated,
                             clsidA[0] ? clsidA : "<none>",
                             profileA[0] ? profileA : "<none>");
    LogForegroundWindowInfoActiveLang("OnActivated");
    LogActiveProfileStateActiveLang("OnActivated");

    // Bridge mode: language profile activation is owned by BimeCore pipeline.
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// _InitActiveLanguageProfileNotifySink
//
// Advise a active language profile notify sink.
//----------------------------------------------------------------------------

BOOL CSampleIME::_InitActiveLanguageProfileNotifySink()
{
    // Bridge mode: disable active language profile sink to avoid legacy callbacks.
    _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;
    Global::LogToFileVerbose("ActiveLanguageProfile: sink disabled in bridge mode");
    LogForegroundWindowInfoActiveLang("InitActiveLanguageProfileNotifySink");
    LogActiveProfileStateActiveLang("InitActiveLanguageProfileNotifySink");
    return TRUE;
}

//+---------------------------------------------------------------------------
//
// _UninitActiveLanguageProfileNotifySink
//
// Unadvise a active language profile notify sink.  Assumes we have advised one already.
//----------------------------------------------------------------------------

void CSampleIME::_UninitActiveLanguageProfileNotifySink()
{
    _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;
}
