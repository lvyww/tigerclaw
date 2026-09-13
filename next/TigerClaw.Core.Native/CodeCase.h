#pragma once
#include <string>
#include <string_view>
namespace tiger::core
{
    std::u16string NormalizeCode(std::u16string_view code);
    std::u16string FoldOrdinalCode(std::u16string_view code);
}
