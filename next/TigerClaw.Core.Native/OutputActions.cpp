#include "OutputActions.h"
#include <vector>
#include <stdexcept>
namespace tiger::core
{
    OutputResult NormalizeOutputAction(std::u16string_view text, const OutputContext& context)
    {
        if (text == u"{\u6dfb\u52a0}" || text == u"{\u52a0\u8bcd}") return {OutputAction::OpenAddWord, {}};
        if (text == u"{\u9690\u85cf\u5019\u9009}") return {OutputAction::ToggleHideCandidates, {}};
        if (text.size() > 3 && text.front() == u'{' && text.back() == u'}' && text.find(u'|') != text.npos)
        {
            auto body = text.substr(1, text.size() - 2);
            std::vector<std::u16string_view> choices;
            while (!body.empty())
            {
                auto separator = body.find(u'|');
                auto item = body.substr(0, separator);
                if (!item.empty()) choices.push_back(item);
                if (separator == body.npos) break;
                body.remove_prefix(separator + 1);
            }
            if (!choices.empty())
            {
                auto index = context.randomIndex(choices.size());
                if (index >= choices.size()) throw std::out_of_range("Random output index");
                return {OutputAction::Text, std::u16string(choices[index])}; // Never recursively expand selected text.
            }
        }
        if (text == u"\u3002" && context.dotAfterDigit) return {OutputAction::Text, u"."};
        if (text == u"{\u91cd\u590d\u4e0a\u5c4f}") return {OutputAction::Text, std::u16string(context.repeatBuffer)};
        int format = -1;
        constexpr std::u16string_view tokens[]{u"{\u65e5\u671f}", u"{\u65e5\u671f.}", u"{\u65e5\u671f-}", u"{\u65e5\u671f/}",
            u"{\u65f6\u5206\u79d2}", u"{\u65f6\u5206}", u"{\u661f\u671f}", u"{\u5468}"};
        for (int index = 0; index < 8; ++index) if (text == tokens[index]) { format = index; break; }
        if (format < 0) return {OutputAction::Text, std::u16string(text)};
        auto time = context.clock();
        if (time.year < 1 || time.year > 9999 || time.month < 1 || time.month > 12 || time.day < 1 || time.day > 31 ||
            time.hour < 0 || time.hour > 23 || time.minute < 0 || time.minute > 59 || time.second < 0 || time.second > 59 || time.dayOfWeek < 0 || time.dayOfWeek > 6)
            throw std::invalid_argument("Invalid output clock fields");
        std::u16string result;
        auto digits = [&](int number, int width)
        {
            auto start = result.size(); result.resize(start + width, u'0');
            while (width) { result[start + --width] = static_cast<char16_t>(u'0' + number % 10); number /= 10; }
        };
        if (format <= 3)
        {
            char16_t separator = format == 0 ? u'\u5e74' : format == 1 ? u'.' : format == 2 ? u'-' : u'/';
            digits(time.year, 4); result += separator; digits(time.month, 2);
            result += format == 0 ? u'\u6708' : separator; digits(time.day, 2);
            if (format == 0) result += u'\u65e5';
        }
        else if (format <= 5)
        {
            digits(time.hour, 2); result += u':'; digits(time.minute, 2);
            if (format == 4) { result += u':'; digits(time.second, 2); }
        }
        else if (format == 6) result = time.localizedDayName;
        else { result = u"\u5468"; result += std::u16string_view(u"\u65e5\u4e00\u4e8c\u4e09\u56db\u4e94\u516d")[time.dayOfWeek]; }
        return {OutputAction::Text, std::move(result)};
    }
}
