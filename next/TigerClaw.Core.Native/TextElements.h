#pragma once
#include <string_view>
#include <vector>
namespace tiger::core
{
    std::vector<std::size_t> TextElementStarts(std::u16string_view text);
}
