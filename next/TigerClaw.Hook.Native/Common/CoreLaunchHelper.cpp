#include "CoreLaunchHelper.h"

#include "Logger.h"
#include "NativeHelpers.h"

#include <tlhelp32.h>

namespace
{
    const wchar_t* CoreProcessName = L"TigerClaw.Core.exe";
}

namespace TigerClawHookNative
{
    void CoreLaunchHelper::TryLaunchCoreIfNeeded(const wchar_t* reason)
    {
        if (IsCoreRunning())
        {
            Logger::Info(L"core", std::wstring(L"launch skipped, core already running: ") + (reason != nullptr ? reason : L""));
            return;
        }

        const long long now = GetNowTick();
        if (now - _lastLaunchAttemptTick < LaunchCooldownMs)
        {
            return;
        }

        _lastLaunchAttemptTick = now;

        const std::wstring corePath = ResolveCorePath();
        if (corePath.empty() || GetFileAttributesW(corePath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            Logger::Info(L"core", std::wstring(L"launch skipped, core exe not found: ") + corePath);
            return;
        }

        std::wstring commandLine = L"\"" + corePath + L"\" --with-overlay";
        STARTUPINFOW startupInfo = {};
        startupInfo.cb = sizeof(startupInfo);
        PROCESS_INFORMATION processInfo = {};

        std::wstring workingDirectory = corePath;
        const size_t slash = workingDirectory.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            workingDirectory.resize(slash);
        }

        if (!CreateProcessW(
                corePath.c_str(),
                commandLine.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                workingDirectory.c_str(),
                &startupInfo,
                &processInfo))
        {
            Logger::Info(L"core", std::wstring(L"launch failed: ") + GetLastErrorMessage(GetLastError()));
            return;
        }

        CloseHandle(processInfo.hProcess);
        CloseHandle(processInfo.hThread);
        Logger::Info(L"core", std::wstring(L"launch attempted: ") + (reason != nullptr ? reason : L""));
    }

    bool CoreLaunchHelper::IsCoreRunning()
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        PROCESSENTRY32W entry = {};
        entry.dwSize = sizeof(entry);
        bool running = false;
        if (Process32FirstW(snapshot, &entry))
        {
            do
            {
                if (_wcsicmp(entry.szExeFile, CoreProcessName) == 0)
                {
                    running = true;
                    break;
                }
            } while (Process32NextW(snapshot, &entry));
        }

        CloseHandle(snapshot);
        return running;
    }

    std::wstring CoreLaunchHelper::ResolveCorePath()
    {
        wchar_t modulePath[MAX_PATH] = {};
        if (GetModuleFileNameW(nullptr, modulePath, ARRAYSIZE(modulePath)) == 0)
        {
            return std::wstring();
        }

        std::wstring basePath(modulePath);
        const size_t slash = basePath.find_last_of(L"\\/");
        if (slash == std::wstring::npos)
        {
            return std::wstring();
        }

        basePath.resize(slash);
        wchar_t fullPath[MAX_PATH] = {};
        const std::wstring sameDirectoryCandidate = basePath + L"\\" + CoreProcessName;
        if (GetFileAttributesW(sameDirectoryCandidate.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return sameDirectoryCandidate;
        }

        const std::wstring debugCandidate = basePath + L"\\..\\x64\\" + std::wstring(CoreProcessName);
        if (GetFullPathNameW(debugCandidate.c_str(), ARRAYSIZE(fullPath), fullPath, nullptr) == 0)
        {
            return debugCandidate;
        }

        return fullPath;
    }

    long long CoreLaunchHelper::GetNowTick()
    {
        return static_cast<unsigned long>(GetTickCount());
    }
}
