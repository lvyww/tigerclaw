#include "Punctuation.h"
#include "SendHistory.h"
namespace tiger::core
{
    namespace
    {
        constexpr int Keys[]{0xbb, 0xbc, 0xbd, 0xbe, 0xc0, 0xdb, 0xdc, 0xdd, 0xbf, 0xba};
    }
    std::optional<std::u16string> ResolveChineseSymbol(int vk, bool englishPunctuation, bool slashIsDunhao, OutputState& state)
    {
        bool decimal = state.DecimalArmed();
        if (vk == 0xbe) state.ClearDecimalArm();
        constexpr std::u16string_view values = u"=\uff0c-\u3002\u00b7\u3010\u3001\u3011\u3001\uff1b";
        for (std::size_t index = 0; index < std::size(Keys); ++index)
        {
            if (vk != Keys[index]) continue;
            if (englishPunctuation) return GuessPassThroughText(vk, false, false);
            if (vk == 0xbe && decimal) return u".";
            if (vk == 0xbf && !slashIsDunhao) return u"/";
            return std::u16string(1, values[index]);
        }
        return std::nullopt; // Quotes are handled by the smart-quote/selection path.
    }
    std::optional<std::u16string> ResolveShiftChineseSymbol(int vk, bool englishPunctuation)
    {
        constexpr std::u16string_view digitValues[]{u"\uff09", u"\uff01", u"@", u"#", u"\uffe5", u"%", u"\u2026\u2026", u"&", u"*", u"\uff08"};
        constexpr std::u16string_view values[]{u"+", u"\u300a", u"\u2014\u2014", u"\u300b", u"~", u"{", u"|", u"}", u"\uff1f", u"\uff1a"};
        if (vk >= 0x30 && vk <= 0x39)
            return englishPunctuation ? GuessPassThroughText(vk, true, false) : std::optional<std::u16string>(digitValues[vk - 0x30]);
        for (std::size_t index = 0; index < std::size(Keys); ++index)
            if (vk == Keys[index]) return englishPunctuation ? GuessPassThroughText(vk, true, false) : std::optional<std::u16string>(values[index]);
        return std::nullopt;
    }
}
