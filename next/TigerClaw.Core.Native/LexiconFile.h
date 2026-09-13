#pragma once
#include "LexiconText.h"
#include <filesystem>
#include <span>
#include <functional>

namespace tiger::core
{
    enum class TextBomPolicy { LegacyLexicon, StreamReader };
    std::u16string DecodeLexiconBytes(std::span<const std::uint8_t> bytes, TextBomPolicy policy = TextBomPolicy::LegacyLexicon);
    void ReadLexiconLines(const std::filesystem::path& path,
        const std::function<void(std::u16string_view)>& consume, TextBomPolicy policy = TextBomPolicy::LegacyLexicon);
    void ReadLexiconFile(const std::filesystem::path& path,
        const std::function<void(LexiconRow)>& consume,
        std::u16string positive = u"+", std::u16string negative = u"-");
}
