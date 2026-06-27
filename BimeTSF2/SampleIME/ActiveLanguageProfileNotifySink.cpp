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

// 查询当前激活的键盘 profile 是否为本 IME（用于在 advise 之后 seed _profileActive，
// 因为我们已是当前激活输入法时不会再收到自身的 OnActivated 回调）。
static BOOL IsOurProfileActiveNow()
{
    ITfInputProcessorProfileMgr *pProfileMgr = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
                                  nullptr,
                                  CLSCTX_INPROC_SERVER,
                                  IID_ITfInputProcessorProfileMgr,
                                  (void **)&pProfileMgr);
    if (FAILED(hr) || pProfileMgr == nullptr)
    {
        return FALSE;
    }

    TF_INPUTPROCESSORPROFILE profile = {};
    hr = pProfileMgr->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &profile);
    BOOL isOurs = FALSE;
    if (SUCCEEDED(hr))
    {
        isOurs = (IsEqualCLSID(profile.clsid, Global::SampleIMECLSID) && IsEqualGUID(profile.guidProfile, Global::SampleIMEGuidProfile)) ? TRUE : FALSE;
    }

    pProfileMgr->Release();
    return isOurs;
}

//+---------------------------------------------------------------------------
//�������ü�����Ϣ������::����
// ITfActiveLanguageProfileNotifySink::OnActivated
//�����ļ������������ļ�ʱ����ܵ��ý�������
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

    // 状态窗随激活显隐：仅维护“本 IME 的 profile 是否被选中”，不恢复任何 legacy composition 逻辑。
    const BOOL isOurs = (IsEqualCLSID(clsid, Global::SampleIMECLSID) && IsEqualGUID(guidProfile, Global::SampleIMEGuidProfile)) ? TRUE : FALSE;
    if (isOurs)
    {
        _profileActive = isActivated;
    }
    else if (isActivated)
    {
        // 另一个输入法 profile 被激活 → 本 IME 不再是当前选中输入法
        _profileActive = FALSE;
    }
    _PublishImeActive();

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
    ITfSource *pSource = nullptr;
    BOOL ret = FALSE;

    _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;

    if (_pThreadMgr == nullptr || FAILED(_pThreadMgr->QueryInterface(IID_ITfSource, (void **)&pSource)))
    {
        Global::LogToFileVerbose("ActiveLanguageProfile: init failed (no source)");
        return ret;
    }

    if (FAILED(pSource->AdviseSink(IID_ITfActiveLanguageProfileNotifySink, (ITfActiveLanguageProfileNotifySink *)this, &_activeLanguageProfileNotifySinkCookie)))
    {
        _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;
        Global::LogToFileVerbose("ActiveLanguageProfile: AdviseSink failed");
        goto Exit;
    }

    ret = TRUE;
    // seed：若本 IME 已是当前激活输入法，则不会收到自身 OnActivated，需主动查询初始化。
    _profileActive = IsOurProfileActiveNow();
    Global::LogToFileVerbose("ActiveLanguageProfile: sink advised cookie=0x%08X seeded_profileActive=%d",
                             _activeLanguageProfileNotifySinkCookie, _profileActive);

Exit:
    pSource->Release();
    LogForegroundWindowInfoActiveLang("InitActiveLanguageProfileNotifySink");
    LogActiveProfileStateActiveLang("InitActiveLanguageProfileNotifySink");
    return ret;
}

//+---------------------------------------------------------------------------
//
// _UninitActiveLanguageProfileNotifySink
//
// Unadvise a active language profile notify sink.  Assumes we have advised one already.
//----------------------------------------------------------------------------

void CSampleIME::_UninitActiveLanguageProfileNotifySink()
{
    ITfSource *pSource = nullptr;

    if (_activeLanguageProfileNotifySinkCookie == TF_INVALID_COOKIE)
    {
        return;
    }

    if (_pThreadMgr != nullptr && SUCCEEDED(_pThreadMgr->QueryInterface(IID_ITfSource, (void **)&pSource)))
    {
        pSource->UnadviseSink(_activeLanguageProfileNotifySinkCookie);
        pSource->Release();
    }

    _activeLanguageProfileNotifySinkCookie = TF_INVALID_COOKIE;
}
