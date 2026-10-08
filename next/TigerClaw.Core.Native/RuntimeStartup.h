#pragma once
#include <filesystem>
#include <string>
#include <windows.h>

namespace tiger::core
{
    bool CanStartCore();
    bool IsTsfRegistered();
    std::filesystem::path CoreExecutablePath();
    void SyncCoreAutoStart(bool enabled) noexcept;
    void SignalHookExit() noexcept;
    // Same-session, exact executable identity only. Returned handle is owned.
    HANDLE FindRuntimeProcess(const std::filesystem::path& executable);
    void StopRuntimeProcess(HANDLE process);
    class CoreInstance
    {
        HANDLE _mutex=nullptr;
    public:
        explicit CoreInstance(const std::filesystem::path& executable);
        ~CoreInstance();
        bool Acquired() const { return _mutex!=nullptr; }
    };
}
