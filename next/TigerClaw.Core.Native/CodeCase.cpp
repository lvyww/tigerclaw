#include "CodeCase.h"
#include "DotNetCaseTable.h"
#include "LexiconText.h"
#include <algorithm>
#include <span>

namespace tiger::core
{
    namespace
    {
        std::u16string Map(std::u16string_view text, std::span<const CaseMapping> table)
        {
            std::u16string result;
            result.reserve(text.size());
            for (std::size_t i = 0; i < text.size(); ++i)
            {
                std::uint32_t scalar = text[i];
                if (scalar >= 0xd800 && scalar <= 0xdbff && i + 1 < text.size() && text[i+1] >= 0xdc00 && text[i+1] <= 0xdfff)
                { scalar = 0x10000 + ((scalar - 0xd800) << 10) + (text[++i] - 0xdc00); }
                auto match = std::lower_bound(table.begin(), table.end(), scalar,
                    [](const CaseMapping& item, std::uint32_t value) { return item.source < value; });
                if (match != table.end() && match->source == scalar) scalar = match->target;
                if (scalar < 0x10000) result.push_back(static_cast<char16_t>(scalar));
                else
                { scalar -= 0x10000; result.push_back(char16_t(0xd800 + (scalar >> 10))); result.push_back(char16_t(0xdc00 + (scalar & 1023))); }
            }
            return result;
        }
    }
    std::u16string NormalizeCode(std::u16string_view code) { return Map(TrimText(code), LowerCaseTable); }
    std::u16string FoldOrdinalCode(std::u16string_view code) { return Map(code, OrdinalCaseTable); }
}
