#include "SentenceSupplementFile.h"
#include "LexiconFile.h"

namespace tiger::core
{
    namespace
    {
        bool PositiveWeight(std::u16string_view token, std::int64_t& weight)
        {
            auto white = [](char16_t c) { return c == u' ' || (c >= 9 && c <= 13); };
            while (!token.empty() && white(token.front())) token.remove_prefix(1);
            if (token.starts_with(u"+")) token.remove_prefix(1);
            std::uint64_t number = 0;
            std::size_t digits = 0;
            constexpr auto maximum = static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max());
            while (digits < token.size() && token[digits] >= u'0' && token[digits] <= u'9')
            {
                auto digit = static_cast<unsigned>(token[digits++] - u'0');
                if (number > (maximum - digit) / 10) return false;
                number = number * 10 + digit;
            }
            if (!digits || !number) return false;
            token.remove_prefix(digits);
            while (!token.empty() && white(token.front())) token.remove_prefix(1);
            for (auto unit : token) if (unit != 0) return false;
            weight = static_cast<std::int64_t>(number);
            return true;
        }
    }
    void SentenceSupplementParser::Consume(std::u16string_view raw)
    {
        auto uncommented = StripInlineComment(raw);
        auto line = TrimText(uncommented);
        std::vector<std::u16string_view> parts;
        for (std::size_t start = 0; start < line.size();)
        {
            if (line[start] == u' ' || line[start] == u'\t') { ++start; continue; }
            auto end = line.find_first_of(u" \t", start);
            if (end == line.npos) end = line.size();
            parts.push_back(line.substr(start, end - start));
            if (parts.size() > 2) return;
            start = end;
        }
        if (parts.empty()) return;
        auto text = TrimText(parts[0]);
        if (text.empty()) return;
        std::int64_t weight = 1000;
        if (parts.size() == 2 && !PositiveWeight(parts[1], weight)) return;
        auto entry = SentenceSupplementEntry::Create(std::u16string(text), weight);
        auto [found, inserted] = _indices.emplace(entry.text, _entries.size());
        if (inserted) _entries.push_back(std::move(entry));
        else _entries[found->second] = std::move(entry); // last valid value, original order
    }
    std::vector<SentenceSupplementEntry> LoadSentenceSupplements(const std::filesystem::path& directory)
    {
        if (directory.empty() || !std::filesystem::is_directory(directory)) return {};
        auto path = directory / u"\u8865\u5145\u8bed\u6599.txt"; // 补充语料.txt
        if (!std::filesystem::is_regular_file(path)) return {};
        SentenceSupplementParser parser;
        ReadLexiconLines(path, [&](auto line) { parser.Consume(line); });
        return parser.Entries();
    }
}
