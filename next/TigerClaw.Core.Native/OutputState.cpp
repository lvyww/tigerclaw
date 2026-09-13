#include "OutputState.h"
#include "TextElements.h"
namespace tiger::core
{
    bool IsOutputEndingWithDigit(std::u16string_view text)
    {
        if (text.empty()) return false;
        char16_t last = text.back();
        if (!((last >= u'0' && last <= u'9') || (last >= u'\uff10' && last <= u'\uff19'))) return false;
        auto starts = TextElementStarts(text);
        return !starts.empty() && starts.back() == text.size() - 1;
    }
    bool IsPassThroughOutputDigit(int vk, bool shift, bool ctrl, bool alt, bool win)
    {
        return !shift && !ctrl && !alt && !win && ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69));
    }
    namespace
    {
        bool Modifier(int vk)
        {
            return vk == 0x10 || vk == 0xa0 || vk == 0xa1 || vk == 0x11 || vk == 0xa2 || vk == 0xa3 ||
                vk == 0x12 || vk == 0xa4 || vk == 0xa5 || vk == 0x5b || vk == 0x5c || vk == 0x14;
        }
    }
    OutputResult OutputState::Normalize(std::u16string_view text, OutputContext providers) const
    {
        providers.dotAfterDigit = _dotAfterDigit;
        providers.repeatBuffer = _repeatBuffer;
        return NormalizeOutputAction(text, providers);
    }
    void OutputState::Observe(int vk, bool keyDown, bool handled, std::u16string_view output,
        bool shift, bool ctrl, bool alt, bool win)
    {
        if (handled && !output.empty()) _repeatBuffer = output;
        if (!keyDown) return;
        bool digit = !output.empty() ? IsOutputEndingWithDigit(output) :
            !handled && IsPassThroughOutputDigit(vk, shift, ctrl, alt, win);
        if (digit) _dotAfterDigit = true;
        else if (!Modifier(vk)) _dotAfterDigit = false;
    }
}
