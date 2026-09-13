#include "OutputServices.h"
#include <stdexcept>
#include <memory>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#include <icu.h>
#endif
namespace tiger::core
{
    OutputRandom::OutputRandom() : _generator(std::random_device{}()) {}
    std::size_t OutputRandom::Choose(std::size_t count)
    {
        if (!count) throw std::invalid_argument("Empty random output set");
        return std::uniform_int_distribution<std::size_t>(0, count - 1)(_generator);
    }
    std::u16string LocalizedOutputDayName(int dayOfWeek, const std::string* locale)
    {
        if (dayOfWeek < 0 || dayOfWeek > 6) throw std::invalid_argument("Invalid weekday");
        if (locale && locale->empty())
        {
            // .NET invariant data uses full English names; ICU root uses Sun,
            // Mon, etc. even for the wide-name symbol request.
            constexpr std::u16string_view names[]{u"Sunday", u"Monday", u"Tuesday", u"Wednesday", u"Thursday", u"Friday", u"Saturday"};
            return std::u16string(names[dayOfWeek]);
        }
#ifdef _WIN32
        std::string localeName;
        if (locale) localeName = *locale;
        else
        {
            wchar_t name[LOCALE_NAME_MAX_LENGTH];
            if (!GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH)) throw std::runtime_error("Cannot get output locale");
            for (auto p = name; *p; ++p)
            {
                if (*p > 127) throw std::runtime_error("Non-ASCII locale identifier");
                localeName.push_back(static_cast<char>(*p));
            }
        }
        UErrorCode error = U_ZERO_ERROR;
        std::unique_ptr<UDateFormat, decltype(&udat_close)> formatter(
            udat_open(UDAT_DEFAULT, UDAT_DEFAULT, localeName.c_str(), nullptr, 0, nullptr, 0, &error), &udat_close);
        if (U_FAILURE(error) || !formatter) throw std::runtime_error("Cannot open weekday formatter");
        int32_t length = udat_getSymbols(formatter.get(), UDAT_STANDALONE_WEEKDAYS, dayOfWeek + 1, nullptr, 0, &error);
        if (error != U_BUFFER_OVERFLOW_ERROR && U_FAILURE(error)) throw std::runtime_error("Cannot size weekday name");
        error = U_ZERO_ERROR;
        std::vector<UChar> buffer(static_cast<std::size_t>(length) + 1);
        length = udat_getSymbols(formatter.get(), UDAT_STANDALONE_WEEKDAYS, dayOfWeek + 1, buffer.data(), static_cast<int32_t>(buffer.size()), &error);
        if (U_FAILURE(error) || length < 0) throw std::runtime_error("Cannot read weekday name");
        return std::u16string(buffer.begin(), buffer.begin() + length);
#else
        (void)locale;
        throw std::runtime_error("Windows output culture service requires Windows ICU");
#endif
    }
    OutputClock ReadLocalOutputClock(const std::string* locale)
    {
#ifdef _WIN32
        SYSTEMTIME time{};
        GetLocalTime(&time);
        return {time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wDayOfWeek,
            LocalizedOutputDayName(time.wDayOfWeek, locale)};
#else
        (void)locale;
        throw std::runtime_error("Windows local output clock requires Windows");
#endif
    }
}
