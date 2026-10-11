#pragma once
#include <string_view>

namespace tiger::core
{
    inline constexpr std::u16string_view CandidateLayoutKey = u"\u5019\u9009\u5e03\u5c40";
    inline bool ValidCandidateLayout(std::u16string_view value)
    { return value.size() == 1 && value[0] >= u'1' && value[0] <= u'5'; }
    inline int ParseCandidateLayout(std::u16string_view value)
    { return ValidCandidateLayout(value) ? value[0] - u'0' : 4; }
}
