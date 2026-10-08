#pragma once
#ifdef _WIN32
#include <windows.h>
#include <string>
#include <stdexcept>
namespace tiger::core
{
    // nullptr means current user; an explicit empty name means invariant.
    inline std::wstring WindowsLocaleName(const std::string* locale)
    {
        if (!locale)
        {
            wchar_t name[LOCALE_NAME_MAX_LENGTH]{};
            if (!GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH))
                throw std::runtime_error("Cannot read Windows user locale");
            return name;
        }
        std::wstring name;
        for (unsigned char c : *locale)
        {
            if (!c || c > 127) throw std::invalid_argument("Invalid Windows locale name");
            name.push_back(c == '_' ? L'-' : static_cast<wchar_t>(c));
        }
        if (!name.empty() && !IsValidLocaleName(name.c_str()))
            throw std::invalid_argument("Invalid Windows locale name");
        return name;
    }
}
#endif
