#include "OutputServices.h"
#include <stdexcept>
#include "WindowsLocale.h"
#include <system_error>
#include <vector>
#ifdef _WIN32
#include <windows.h>
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
            // Keep invariant English names independent of user regional overrides.
            constexpr std::u16string_view names[]{u"Sunday", u"Monday", u"Tuesday", u"Wednesday", u"Thursday", u"Friday", u"Saturday"};
            return std::u16string(names[dayOfWeek]);
        }
#ifdef _WIN32
        const auto localeName = WindowsLocaleName(locale);
        // Windows numbers weekdays Monday=1 .. Sunday=7; engine uses Sunday=0.
        const auto kind = static_cast<LCTYPE>(LOCALE_SDAYNAME1 + (dayOfWeek + 6) % 7);
        int length = GetLocaleInfoEx(localeName.c_str(), kind, nullptr, 0);
        if (!length) throw std::system_error(static_cast<int>(GetLastError()),
            std::system_category(), "Size Windows weekday name");
        std::vector<wchar_t> buffer(static_cast<std::size_t>(length));
        if (!GetLocaleInfoEx(localeName.c_str(), kind, buffer.data(), length))
            throw std::system_error(static_cast<int>(GetLastError()),
                std::system_category(), "Read Windows weekday name");
        return std::u16string(buffer.begin(), buffer.end() - 1);
#else
        (void)locale;
        throw std::runtime_error("Windows output culture service requires Windows NLS");
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
