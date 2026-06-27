#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    inline std::wstring GetLastErrorMessage(DWORD errorCode)
    {
        LPWSTR buffer = nullptr;
        DWORD length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            errorCode,
            MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
            reinterpret_cast<LPWSTR>(&buffer),
            0,
            nullptr);

        std::wstring message;
        if (length != 0 && buffer != nullptr)
        {
            message.assign(buffer, length);
            LocalFree(buffer);
        }
        else
        {
            message = L"Unknown error";
        }

        return message;
    }

    inline std::string Utf8FromWide(const std::wstring& value)
    {
        if (value.empty())
        {
            return std::string();
        }

        int required = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (required <= 0)
        {
            return std::string();
        }

        std::string result(static_cast<size_t>(required), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), required, nullptr, nullptr);
        return result;
    }

    inline std::wstring WideFromUtf8(const std::string& value)
    {
        if (value.empty())
        {
            return std::wstring();
        }

        int required = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (required <= 0)
        {
            return std::wstring();
        }

        std::wstring result(static_cast<size_t>(required), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), required);
        return result;
    }
}
