#pragma once

#include <windows.h>

namespace TigerClawStartup
{
    inline bool IsServiceAccount(PSID sid)
    {
        return !sid || IsWellKnownSid(sid, WinLocalSystemSid) ||
            IsWellKnownSid(sid, WinLocalServiceSid) || IsWellKnownSid(sid, WinNetworkServiceSid);
    }

    inline bool IsNamedUserObject(HANDLE object, const wchar_t* expected)
    {
        wchar_t name[256] = {};
        DWORD needed = 0;
        return object && GetUserObjectInformationW(object, UOI_NAME, name, sizeof(name), &needed) &&
            _wcsicmp(name, expected) == 0;
    }

    // CreateProcess inherits the TSF host's primary token and desktop. Never
    // let a logon/service host claim the user's Core pipe and singleton.
    inline bool CanLaunchCore()
    {
        DWORD session = 0;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || session == 0)
            return false;
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        alignas(TOKEN_USER) BYTE user[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE] = {};
        DWORD needed = 0;
        bool allowed = GetTokenInformation(token, TokenUser, user, sizeof(user), &needed) &&
            !IsServiceAccount(reinterpret_cast<TOKEN_USER*>(user)->User.Sid);
        CloseHandle(token);
        return allowed && IsNamedUserObject(GetProcessWindowStation(), L"WinSta0") &&
            IsNamedUserObject(GetThreadDesktop(GetCurrentThreadId()), L"Default");
    }
}
