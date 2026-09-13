#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include "../BimeTSF2/SampleIME/CoreLaunchContext.h"
#include "../BimeTSF2/SampleIME/ProtectedInput.h"

static void Check(bool value, const char* message)
{
    if (!value) { std::printf("FAIL %s error=%lu\n", message, GetLastError()); std::exit(1); }
}

int main()
{
    for (auto type : {WinLocalSystemSid, WinLocalServiceSid, WinNetworkServiceSid})
    {
        BYTE sid[SECURITY_MAX_SID_SIZE]; DWORD size = sizeof(sid);
        Check(CreateWellKnownSid(type, nullptr, sid, &size) != FALSE, "create service SID");
        Check(TigerClawStartup::IsServiceAccount(sid), "service account blocked");
    }
    Check(TigerClawStartup::IsServiceAccount(nullptr), "unknown identity blocked");
    Check(TigerClawStartup::CanLaunchCore(), "interactive user permitted");
    Check(TigerClawInput::IsProtectedEnvironment(true), "TSF secure mode bypass");
    Check(!TigerClawInput::IsProtectedEnvironment(false), "ordinary environment permitted");
    HWND plain = CreateWindowExW(0, L"Edit", L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, nullptr, nullptr);
    HWND password = CreateWindowExW(0, L"Edit", L"", WS_POPUP | ES_PASSWORD, 0, 0, 1, 1, nullptr, nullptr, nullptr, nullptr);
    HWND other = CreateWindowExW(0, L"Static", L"", WS_POPUP | ES_PASSWORD, 0, 0, 1, 1, nullptr, nullptr, nullptr, nullptr);
    Check(plain && password && other, "isolated controls created");
    Check(!TigerClawInput::IsPasswordEdit(plain), "plain edit permitted");
    Check(TigerClawInput::IsPasswordEdit(password), "password edit bypass");
    Check(!TigerClawInput::IsPasswordEdit(other), "other class style bits ignored");
    SendMessageW(plain, EM_SETPASSWORDCHAR, '*', 0);
    Check(TigerClawInput::IsPasswordEdit(plain), "dynamic password style detected");
    DestroyWindow(plain); DestroyWindow(password); DestroyWindow(other);
    wchar_t name[128]; swprintf_s(name, L"TigerClawStartupTest%lu", GetCurrentProcessId());
    HDESK desktop = CreateDesktopW(name, nullptr, nullptr, 0, GENERIC_ALL, nullptr);
    Check(desktop != nullptr, "isolated desktop created");
    std::thread worker([&] {
        Check(SetThreadDesktop(desktop) != FALSE, "worker isolated desktop");
        Check(!TigerClawStartup::CanLaunchCore(), "non-default desktop blocked");
        Check(TigerClawInput::IsProtectedEnvironment(false), "alternate desktop input bypass");
    });
    worker.join(); CloseDesktop(desktop);
    Check(TigerClawStartup::CanLaunchCore(), "original desktop unaffected");
    std::puts("TSF startup context tests passed");
}
