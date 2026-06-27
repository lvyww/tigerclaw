#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    class CoreLaunchHelper
    {
    public:
        void TryLaunchCoreIfNeeded(const wchar_t* reason);

    private:
        static constexpr long long LaunchCooldownMs = 5000;

        static bool IsCoreRunning();
        static std::wstring ResolveCorePath();
        static long long GetNowTick();

        long long _lastLaunchAttemptTick = 0;
    };
}
