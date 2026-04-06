// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
//
// Copyright (c) Microsoft Corporation. All rights reserved

#include "Private.h"
#include "Globals.h"
#include "SampleIME.h"

static void LogForegroundWindowInfoThreadFocus(_In_opt_ const char *stage)
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

    Global::LogToFileVerbose("ThreadFocus %s fg_hwnd=0x%p tid=%lu pid=%lu class=%s title=%s",
                             stage ? stage : "fg",
                             hwnd,
                             static_cast<unsigned long>(tid),
                             static_cast<unsigned long>(pid),
                             className[0] ? className : "<none>",
                             windowTitle[0] ? windowTitle : "<none>");
}

static void LogActiveProfileStateThreadFocus(_In_opt_ const char *stage)
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
        Global::LogToFileVerbose("ThreadFocus %s active_profile query_failed hr=0x%08X",
                                 stage ? stage : "profile",
                                 static_cast<unsigned>(hr));
        return;
    }

    TF_INPUTPROCESSORPROFILE profile = {};
    hr = pProfileMgr->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("ThreadFocus %s active_profile get_failed hr=0x%08X",
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
    Global::LogToFileVerbose("ThreadFocus %s active_profile lang=0x%04X clsid=%s profile=%s flags=0x%08X is_ours=%d",
                             stage ? stage : "profile",
                             static_cast<unsigned>(profile.langid),
                             clsidA[0] ? clsidA : "<none>",
                             profileA[0] ? profileA : "<none>",
                             static_cast<unsigned>(profile.dwFlags),
                             isOurs);

    pProfileMgr->Release();
}

//+---------------------------------------------------------------------------
//线程输入焦点消息接收器
// ITfThreadFocusSink::OnSetThreadFocus
//
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnSetThreadFocus()
{
    // Bridge mode: candidate UI thread-focus behavior is owned by BimeCore.
    Global::LogToFileVerbose("ThreadFocus: OnSetThreadFocus bridge_mode");
    LogForegroundWindowInfoThreadFocus("OnSetThreadFocus");
    LogActiveProfileStateThreadFocus("OnSetThreadFocus");
    return S_OK;
}

//+---------------------------------------------------------------------------
//
// ITfThreadFocusSink::OnKillThreadFocus
//
//----------------------------------------------------------------------------

STDAPI CSampleIME::OnKillThreadFocus()
{
    Global::LogToFileVerbose("ThreadFocus: OnKillThreadFocus bridge_mode");
    LogForegroundWindowInfoThreadFocus("OnKillThreadFocus");
    LogActiveProfileStateThreadFocus("OnKillThreadFocus");
    return S_OK;
}

BOOL CSampleIME::_InitThreadFocusSink()
{
    // Bridge mode: disable thread focus sink to avoid legacy callbacks.
    _dwThreadFocusSinkCookie = TF_INVALID_COOKIE;
    Global::LogToFileVerbose("ThreadFocus: sink disabled in bridge mode");
    LogForegroundWindowInfoThreadFocus("InitThreadFocusSink");
    LogActiveProfileStateThreadFocus("InitThreadFocusSink");
    return TRUE;
}

void CSampleIME::_UninitThreadFocusSink()
{
    _dwThreadFocusSinkCookie = TF_INVALID_COOKIE;
}
