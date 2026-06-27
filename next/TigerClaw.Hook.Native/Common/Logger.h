#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    class Logger
    {
    public:
        static void Info(const wchar_t* scope, const std::wstring& message);
        static void Info(const wchar_t* scope, const wchar_t* message);
    };
}
