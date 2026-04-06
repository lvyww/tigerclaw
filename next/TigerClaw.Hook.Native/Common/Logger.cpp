#include "Logger.h"

#include <cstdio>
#include <sstream>

namespace TigerClawHookNative
{
    void Logger::Info(const wchar_t* scope, const std::wstring& message)
    {
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
