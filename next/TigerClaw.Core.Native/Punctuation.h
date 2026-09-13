#pragma once
#include "OutputState.h"
#include <optional>
namespace tiger::core
{
    std::optional<std::u16string> ResolveChineseSymbol(int vk, bool englishPunctuation, bool slashIsDunhao, OutputState& state);
    std::optional<std::u16string> ResolveShiftChineseSymbol(int vk, bool englishPunctuation);
}
