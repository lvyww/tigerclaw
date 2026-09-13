#pragma once
#include "OutputActions.h"
namespace tiger::core
{
    bool IsOutputEndingWithDigit(std::u16string_view text);
    bool IsPassThroughOutputDigit(int vk, bool shift, bool ctrl, bool alt, bool win);
    class OutputState
    {
    public:
        OutputResult Normalize(std::u16string_view text, OutputContext providers) const;
        // Called after macro/action normalization, on the final result text.
        void Observe(int vk, bool keyDown, bool handled, std::u16string_view output,
            bool shift = false, bool ctrl = false, bool alt = false, bool win = false);
        void ClearDecimalArm() { _dotAfterDigit = false; }
        bool DecimalArmed() const { return _dotAfterDigit; }
        const std::u16string& RepeatBuffer() const { return _repeatBuffer; }
    private:
        bool _dotAfterDigit = false;
        std::u16string _repeatBuffer = u"\u91cd\u590d\u4e0a\u5c4f";
    };
}
