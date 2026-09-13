#include "LexiconFile.h"
#include <fstream>
#include <stdexcept>
#include <iterator>

namespace tiger::core
{
    std::u16string DecodeLexiconBytes(std::span<const std::uint8_t> b, TextBomPolicy policy)
    {
        enum class Encoding { Utf8, Le16, Be16, Be32, Le32 } encoding = Encoding::Utf8;
        // Match DetectTextEncoding followed by StreamReader preamble handling.
        // FF FE is selected as UTF-16 before a possible UTF-32 LE BOM.
        if (policy == TextBomPolicy::StreamReader && b.size() >= 4 && b[0] == 0xff && b[1] == 0xfe && b[2] == 0 && b[3] == 0)
        { encoding = Encoding::Le32; b = b.subspan(4); }
        else if (b.size() >= 2 && b[0] == 0xff && b[1] == 0xfe) { encoding = Encoding::Le16; b = b.subspan(2); }
        else if (b.size() >= 2 && b[0] == 0xfe && b[1] == 0xff) { encoding = Encoding::Be16; b = b.subspan(2); }
        else if (b.size() >= 3 && b[0] == 0xef && b[1] == 0xbb && b[2] == 0xbf) b = b.subspan(3);
        else if (b.size() >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xfe && b[3] == 0xff)
        { encoding = Encoding::Be32; b = b.subspan(4); }
        std::u16string text;
        auto emit = [&](std::uint32_t cp)
        {
            if (cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) cp = 0xfffd;
            if (cp < 0x10000) text.push_back(static_cast<char16_t>(cp));
            else { cp -= 0x10000; text.push_back(char16_t(0xd800 + (cp >> 10))); text.push_back(char16_t(0xdc00 + (cp & 1023))); }
        };
        if (encoding == Encoding::Be32 || encoding == Encoding::Le32)
        {
            std::size_t i = 0;
            for (; i + 4 <= b.size(); i += 4)
                emit(encoding == Encoding::Be32
                    ? (std::uint32_t(b[i]) << 24) | (std::uint32_t(b[i+1]) << 16) | (std::uint32_t(b[i+2]) << 8) | b[i+3]
                    : b[i] | (std::uint32_t(b[i+1]) << 8) | (std::uint32_t(b[i+2]) << 16) | (std::uint32_t(b[i+3]) << 24));
            if (i != b.size()) emit(0xfffd);
        }
        else if (encoding != Encoding::Utf8)
        {
            auto unit = [&](std::size_t i) { return encoding == Encoding::Le16 ? b[i] | (b[i+1] << 8) : (b[i] << 8) | b[i+1]; };
            std::size_t i = 0;
            for (; i + 2 <= b.size(); i += 2)
            {
                auto c = unit(i);
                if (c >= 0xd800 && c <= 0xdbff && i + 4 <= b.size())
                {
                    auto next = unit(i + 2);
                    if (next >= 0xdc00 && next <= 0xdfff)
                    { text.push_back(char16_t(c)); text.push_back(char16_t(next)); i += 2; continue; }
                }
                emit(c);
            }
            if (i != b.size()) emit(0xfffd);
        }
        else
        {
            for (std::size_t i = 0; i < b.size();)
            {
                auto first = b[i++];
                if (first < 0x80) { emit(first); continue; }
                int extra = first >= 0xc2 && first <= 0xdf ? 1 : first >= 0xe0 && first <= 0xef ? 2 : first >= 0xf0 && first <= 0xf4 ? 3 : 0;
                if (!extra) { emit(0xfffd); continue; }
                std::uint32_t cp = first & (extra == 1 ? 31 : extra == 2 ? 15 : 7);
                bool valid = true;
                for (int j = 0; j < extra; ++j)
                {
                    if (i == b.size()) { valid = false; break; }
                    auto c = b[i];
                    if (c < 0x80 || c > 0xbf || (j == 0 &&
                        ((first == 0xe0 && c < 0xa0) || (first == 0xed && c > 0x9f) ||
                         (first == 0xf0 && c < 0x90) || (first == 0xf4 && c > 0x8f))))
                    { valid = false; break; }
                    ++i; cp = (cp << 6) | (c & 63);
                }
                emit(valid ? cp : 0xfffd);
            }
        }
        return text;
    }
    void ReadLexiconLines(const std::filesystem::path& path, const std::function<void(std::u16string_view)>& consume, TextBomPolicy policy)
    {
        std::ifstream file(path, std::ios::binary);
        if (!file) throw std::runtime_error("Cannot open lexicon file");
        std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(file), {}};
        if (file.bad()) throw std::runtime_error("Cannot read lexicon file");
        auto text = DecodeLexiconBytes(bytes, policy);
        std::size_t start = 0;
        for (std::size_t i = 0; i <= text.size(); ++i)
        {
            if (i != text.size() && text[i] != u'\r' && text[i] != u'\n') continue;
            if (i != text.size() || i > start)
                consume(std::u16string_view(text).substr(start, i - start));
            if (i < text.size() && text[i] == u'\r' && i + 1 < text.size() && text[i+1] == u'\n') ++i;
            start = i + 1;
        }
    }
    void ReadLexiconFile(const std::filesystem::path& path, const std::function<void(LexiconRow)>& consume,
        std::u16string positive, std::u16string negative)
    {
        auto name = path.filename().u16string();
        for (auto& c : name) if (c >= u'A' && c <= u'Z') c = char16_t(c + 32);
        LexiconLineParser parser(name.ends_with(u".dict.yaml"), std::move(positive), std::move(negative));
        ReadLexiconLines(path, [&](std::u16string_view line)
        { for (auto& row : parser.Parse(line)) consume(std::move(row)); });
    }
}
