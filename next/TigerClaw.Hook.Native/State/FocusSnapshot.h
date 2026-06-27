#pragma once

#include <windows.h>

#include <string>

namespace TigerClawHookNative
{
    struct FocusSnapshot
    {
        HWND Window = nullptr;
        DWORD ProcessId = 0;
        std::wstring ProcessName;
        std::wstring ClassName;
        std::wstring WindowTitle;

        bool IsValid() const
        {
            return Window != nullptr;
        }

        bool IsGdqLike() const
        {
            const std::wstring gainDutchTitle = L"GainDutch";
            std::wstring painTitle = L"Pain";
            painTitle.push_back(static_cast<wchar_t>(0x6253));
            painTitle.push_back(static_cast<wchar_t>(0x5668));
            std::wstring findTitle;
            findTitle.push_back(static_cast<wchar_t>(0x67E5));
            findTitle.push_back(static_cast<wchar_t>(0x627E));
            std::wstring touchTypeToken;
            touchTypeToken.push_back(static_cast<wchar_t>(0x8DDF));
            touchTypeToken.push_back(static_cast<wchar_t>(0x6253));
            const bool isGdqLike =
                WindowTitle == gainDutchTitle ||
                WindowTitle == painTitle ||
                WindowTitle == findTitle ||
                WindowTitle.find(touchTypeToken) != std::wstring::npos;

            return isGdqLike;
        }

        bool Equals(const FocusSnapshot& other) const
        {
            return Window == other.Window &&
                   ProcessId == other.ProcessId;
        }
    };
}
