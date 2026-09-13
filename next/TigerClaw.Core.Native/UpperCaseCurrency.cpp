#include "UpperCaseComposition.h"
#include <algorithm>

namespace tiger::core
{
    namespace
    {
        std::string StripZeros(std::string text)
        {
            auto first = text.find_first_not_of('0');
            return first == text.npos ? "0" : text.substr(first);
        }
        std::string Round(std::string_view digits, std::size_t drop, bool tiesEven)
        {
            if (!drop) return std::string(digits);
            if (drop > digits.size()) return "0";
            auto keep = digits.size() - drop;
            bool up = digits[keep] > '5';
            if (digits[keep] == '5')
            {
                bool tail = digits.substr(keep + 1).find_first_not_of('0') != digits.npos;
                up = !tiesEven || tail || (keep && (digits[keep - 1] - '0') % 2 != 0);
            }
            std::string result(digits.substr(0, keep));
            if (up)
            {
                auto index = result.size();
                while (index && result[index - 1] == '9') result[--index] = '0';
                if (index) ++result[index - 1];
                else result.insert(result.begin(), '1');
            }
            return result.empty() ? "0" : result;
        }
    }
    std::u16string ConvertUpperCaseCurrency(std::u16string_view code)
    {
        const std::u16string error = u"\u6570\u5b57\u683c\u5f0f\u9519\u8bef!";
        if (ClassifyUpperCaseCommand(code) != UpperCaseCommand::Currency) return error;
        std::string digits;
        bool fraction = false;
        std::size_t fractionalDigits = 0;
        for (char16_t c : code.substr(1))
        {
            if (c == u',' || c == u'\n') continue;
            if (c == u'.') { fraction = true; continue; }
            digits.push_back(static_cast<char>(c));
            if (fraction) ++fractionalDigits;
        }
        if (digits.empty()) return error;
        digits = StripZeros(std::move(digits));
        // .NET Decimal retains a 96-bit integer coefficient and scale <= 28.
        // Parsing rounds discarded fractional digits to even; custom formatting
        // subsequently rounds to two places away from zero on a midpoint.
        constexpr std::string_view maxCoefficient = "79228162514264337593543950335";
        auto scale = std::min<std::size_t>(fractionalDigits, 28);
        std::string coefficient;
        while (true)
        {
            coefficient = Round(digits, fractionalDigits - scale, true);
            if (coefficient.size() < maxCoefficient.size() ||
                (coefficient.size() == maxCoefficient.size() && coefficient <= maxCoefficient)) break;
            if (scale == 0) return error;
            --scale;
        }
        std::string cents = scale > 2 ? Round(coefficient, scale - 2, false) : coefficient + std::string(2 - scale, '0');
        if (cents.size() < 3) cents.insert(0, 3 - cents.size(), '0');
        int jiao = cents[cents.size() - 2] - '0';
        int fen = cents.back() - '0';
        std::string integer = StripZeros(cents.substr(0, cents.size() - 2));
        constexpr std::u16string_view numerals = u"\u96f6\u58f9\u8d30\u53c1\u8086\u4f0d\u9646\u67d2\u634c\u7396";
        constexpr std::u16string_view small = u" \u62fe\u4f70\u4edf";
        constexpr std::u16string_view groups = u" \u4e07\u4ebf\u5146\u4eac\u5793\u79ed\u7a70";
        std::u16string result;
        bool zero = false;
        if (integer != "0")
        {
            for (std::size_t pos = 0; pos < integer.size(); ++pos)
            {
                auto place = integer.size() - pos - 1;
                int digit = integer[pos] - '0';
                if (digit)
                {
                    if (zero && !result.empty()) result += numerals[0];
                    zero = false;
                    result += numerals[digit];
                    if (place % 4) result += small[place % 4];
                }
                else zero = true;
                if (place % 4 == 0 && place > 0)
                {
                    auto start = pos >= 3 ? pos - 3 : 0;
                    bool groupNonzero = integer.substr(start, pos - start + 1).find_first_not_of('0') != integer.npos;
                    if (groupNonzero)
                    {
                        result += groups[place / 4];
                        zero = false; // trailing group zeros do not precede a full next group
                    }
                }
            }
            result += u'\u5143';
        }
        if (jiao) { result += numerals[jiao]; result += u'\u89d2'; }
        if (fen)
        {
            if (!jiao && integer != "0") result += numerals[0];
            result += numerals[fen]; result += u'\u5206';
        }
        if (!jiao && !fen && integer != "0") result += u'\u6574';
        return result;
    }
}
