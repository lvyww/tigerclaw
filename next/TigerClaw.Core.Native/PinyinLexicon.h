#pragma once
#include "CompactLexicon.h"
namespace tiger::core
{
    CompactLexicon LoadPinyinLexicon(const std::filesystem::path& executableDirectory);
}
