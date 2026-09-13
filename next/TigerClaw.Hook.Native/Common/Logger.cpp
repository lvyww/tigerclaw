#include "Logger.h"

#include <cstdio>
#include <sstream>

namespace TigerClawHookNative
{
    void Logger::Info(const wchar_t* scope, const std::wstring& message)
    {
        // Opt-in diagnostics only. Never publish a user's keystroke stream by default.
        static const bool enabled = [] {
            wchar_t value[8] = {};
            return GetEnvironmentVariableW(L"TIGERCLAW_HOOK_DIAGNOSTICS", value, 8) == 1 && value[0] == L'1';
        }();
        if (!enabled) return;
        std::wstringstream stream;
        stream << L"[Hook.Native][" << scope << L"] " << message << L"\r\n";
        OutputDebugStringW(stream.str().c_str());
#if defined(_DEBUG)
        ::wprintf(L"%s", stream.str().c_str());
#endif
    }

    void Logger::Info(const wchar_t* scope, const wchar_t* message)
    {
        Info(scope, std::wstring(message != nullptr ? message : L""));
    }
}
