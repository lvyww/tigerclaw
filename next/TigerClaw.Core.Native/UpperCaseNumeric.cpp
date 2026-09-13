#include "UpperCaseComposition.h"
#include "LexiconText.h"
#include "CodeCase.h"
#include <stdexcept>

namespace tiger::core
{
    UpperCaseCommand ClassifyUpperCaseCommand(std::u16string_view code)
    {
        UpperCaseCommand command;
        if (code.starts_with(u"Ds") || code.starts_with(u"DS"))
        {
            command = UpperCaseCommand::Timer;
            code.remove_prefix(2);
        }
        else if (code.starts_with(u"S"))
        {
            command = UpperCaseCommand::Currency;
            code.remove_prefix(1);
        }
        else return UpperCaseCommand::Literal;
        // .NET regex '$' accepts one final LF, not CRLF or arbitrary whitespace.
        if (!code.empty() && code.back() == u'\n') code.remove_suffix(1);
        bool dot = false;
        std::size_t finalDigitsOrCommas = 0;
        for (char16_t c : code)
        {
            if ((c >= u'0' && c <= u'9') || c == u',') ++finalDigitsOrCommas;
            else if (c == u'.' && !dot) { dot = true; finalDigitsOrCommas = 0; }
            else return UpperCaseCommand::Literal;
        }
        // This is command recognition, not number validation: even ',' matches
        // the reference regex and must reach the corresponding service.
        return finalDigitsOrCommas ? command : UpperCaseCommand::Literal;
    }
    UpperCaseServices MakeUpperCaseServices(
        std::function<std::u16string(std::u16string_view)> currency,
        std::function<void(std::u16string_view)> scheduleTimer)
    {
        if (!currency || !scheduleTimer) throw std::invalid_argument("Uppercase command services are required");
        return {[currency = std::move(currency), scheduleTimer = std::move(scheduleTimer)](std::u16string_view code)
        {
            switch (ClassifyUpperCaseCommand(code))
            {
            case UpperCaseCommand::Timer: scheduleTimer(code); return std::u16string{};
            case UpperCaseCommand::Currency: return currency(code);
            default: return std::u16string(code);
            }
        }};
    }
    UpperCaseServices MakeUpperCaseServices(std::function<void(std::u16string_view)> scheduleTimer)
    {
        return MakeUpperCaseServices(ConvertUpperCaseCurrency, std::move(scheduleTimer));
    }
    bool IsUpperCaseNumericPrefix(std::u16string_view code)
    {
        if (code.size() < 2 || (code.front() != u'D' && code.front() != u'd' && code.front() != u'S')) return false;
        auto number = code.substr(1);
        // .NET's special-value fallback trims Unicode whitespace, unlike the
        // ordinary numeric grammar. Magnitude need not be converted here:
        // double overflow/underflow still counts as successful parsing.
        auto special = FoldOrdinalCode(TrimText(number));
        if (!special.empty() && (special.front() == u'+' || special.front() == u'-')) special.erase(0, 1);
        if (special == u"NAN" || special == u"INFINITY") return true;
        auto white = [](char16_t c) { return c == u' ' || (c >= 9 && c <= 13); };
        while (!number.empty() && white(number.front())) number.remove_prefix(1);
        if (!number.empty() && (number.front() == u'+' || number.front() == u'-')) number.remove_prefix(1);
        bool digit = false;
        auto digits = [&]
        {
            std::size_t count = 0;
            while (!number.empty() && number.front() >= u'0' && number.front() <= u'9')
            { number.remove_prefix(1); ++count; }
            return count != 0;
        };
        digit = digits();
        if (!number.empty() && number.front() == u'.')
        {
            number.remove_prefix(1);
            digit = digits() || digit;
        }
        if (!digit) return false;
        if (!number.empty() && (number.front() == u'e' || number.front() == u'E'))
        {
            number.remove_prefix(1);
            if (!number.empty() && (number.front() == u'+' || number.front() == u'-')) number.remove_prefix(1);
            if (!digits()) return false;
        }
        while (!number.empty() && white(number.front())) number.remove_prefix(1);
        for (char16_t c : number) if (c != 0) return false;
        return true;
    }
}
