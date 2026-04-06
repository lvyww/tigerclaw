#include "HookRuntime.h"

#include <windows.h>
#include <shellscalingapi.h>

namespace
{
    HANDLE SingleInstanceMutex = nullptr;

    void InitializeDpiAwareness();

    int RunApplication()
    {
        InitializeDpiAwareness();
#if defined(_DEBUG)
        SetConsoleTitleW(L"TigerClaw.Hook.Native");
#endif

        SingleInstanceMutex = CreateMutexW(nullptr, TRUE, L"Local\\TigerClaw.Hook.Native.SingleInstance");
        if (SingleInstanceMutex == nullptr || GetLastError() == ERROR_ALREADY_EXISTS)
        {
            if (SingleInstanceMutex != nullptr)
            {
                CloseHandle(SingleInstanceMutex);
                SingleInstanceMutex = nullptr;
            }

            return 0;
        }

        TigerClawHookNative::HookRuntime runtime;
        const int exitCode = runtime.Run();

        if (SingleInstanceMutex != nullptr)
        {
            ReleaseMutex(SingleInstanceMutex);
            CloseHandle(SingleInstanceMutex);
            SingleInstanceMutex = nullptr;
        }

        return exitCode;
    }

    void InitializeDpiAwareness()
    {
        if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            return;
        }

        SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
    }
}

#if defined(_DEBUG)
int wmain()
{
    return RunApplication();
}
#else
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    return RunApplication();
}
#endif
