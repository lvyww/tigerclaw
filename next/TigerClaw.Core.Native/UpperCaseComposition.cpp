#include "UpperCaseComposition.h"
#include <stdexcept>

namespace tiger::core
{
    namespace
    {
        bool EnglishSymbolKey(int vk)
        {
            switch (vk)
            {
            case 0xbb: case 0xbc: case 0xbd: case 0xbe: case 0xc0:
            case 0xdb: case 0xdc: case 0xdd: case 0xbf: case 0xba: case 0xde:
                return true;
            default: return false;
            }
        }
    }
    UpperCaseComposition::UpperCaseComposition(UpperCaseServices services) : _services(std::move(services))
    {
        if (!_services.commit || !_services.allowsNumericSeparator)
            throw std::invalid_argument("Uppercase composition requires commit and numeric-prefix services");
    }
    PostProcessResult UpperCaseComposition::State(std::u16string output) const
    {
        return {true, std::move(output), _raw, Active()};
    }
    PostProcessResult UpperCaseComposition::Start(char16_t uppercaseLetter)
    {
        if (uppercaseLetter < u'A' || uppercaseLetter > u'Z')
            throw std::invalid_argument("Uppercase entry requires an uppercase ASCII letter");
        _raw.assign(1, uppercaseLetter);
        return State();
    }
    PostProcessResult UpperCaseComposition::Complete(std::u16string_view suffix)
    {
        auto output = _services.commit(_raw);
        output += suffix;
        Clear();
        return State(std::move(output));
    }
    PostProcessResult UpperCaseComposition::KeyDown(int vk, bool shift, bool tabClear)
    {
        if (!Active()) throw std::logic_error("Uppercase composition is not active");
        if (vk == 32 || vk == 13) return Complete(); // Enter-clear does not apply in this mode.
        if (vk == 9)
        {
            if (!tabClear) return {};
            Clear();
            return State();
        }
        bool symbol = EnglishSymbolKey(vk);
        if (shift && (symbol || (vk >= 0x30 && vk <= 0x39)))
            return Complete(*GuessPassThroughText(vk, true, false));
        if (symbol)
        {
            auto text = *GuessPassThroughText(vk, false, false);
            if ((vk == 0xbe || vk == 0xbc) && _services.allowsNumericSeparator(_raw))
            {
                _raw += text;
                return State();
            }
            return Complete(text);
        }
        if (vk >= 0x30 && vk <= 0x39) _raw += static_cast<char16_t>(vk);
        else if (vk >= 0x41 && vk <= 0x5a) _raw += static_cast<char16_t>(shift ? vk : vk + 32);
        else if (vk == 8) _raw.pop_back();
        else if (vk == 27) Clear();
        else return {}; // Unhandled, retaining the owned composition.
        return State();
    }
}
