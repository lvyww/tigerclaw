#include "SelectionKeys.h"
#include "LexiconText.h"
#include "CodeCase.h"
#include "LexiconFile.h"
#include "ConfigParser.h"
#include <algorithm>
#include <bit>
namespace tiger::core
{
    void WriteSelectionBindingsFile(const std::filesystem::path& path, const SelectionBindings& bindings)
    {
        WriteUtf8TextFile(path, BuildSelectionFileText(bindings), true);
    }
    void EnsureSelectionBindingsFile(const std::filesystem::path& path)
    {
        if (path.empty() || path.native().find(std::filesystem::path::value_type(0)) != path.native().npos)
            throw std::invalid_argument("Invalid selection configuration path");
        // Reference engine startup does not create parent directories here.
        if (!std::filesystem::is_regular_file(path))
            WriteSelectionBindingsFile(path, DefaultSelectionBindings());
    }
    SelectionBindings ReadSelectionBindingsFile(const std::filesystem::path& path)
    {
        try
        {
            std::vector<std::u16string> lines;
            ReadLexiconLines(path, [&](std::u16string_view line) { lines.emplace_back(line); }, TextBomPolicy::StreamReader);
            auto parsed = ParseSelectionBindings(lines);
            if (parsed.success) return std::move(parsed.bindings);
        }
        catch (const std::exception&)
        {
            // Like the reference loader, a failed read must not retain an old
            // binding or publish a valid prefix of a malformed configuration.
        }
        return DefaultSelectionBindings();
    }
    namespace
    {
        std::u16string Decimal(int value)
        {
            auto text = std::to_string(value);
            return {text.begin(), text.end()};
        }
        std::u16string Hex(int value)
        {
            auto bits = static_cast<std::uint32_t>(value);
            std::u16string text;
            do
            {
                text.push_back(u"0123456789ABCDEF"[bits & 15]);
                bits >>= 4;
            } while (bits);
            if (text.size() < 2) text.push_back(u'0');
            std::reverse(text.begin(), text.end());
            return u"0x" + text;
        }
        const std::vector<std::pair<std::u16string, int>>& KeyNames()
        {
            static const auto names = []
            {
                std::vector<std::pair<std::u16string, int>> result{
                    {u"VK_SHIFT",0x10},{u"VK_LSHIFT",0xa0},{u"VK_RSHIFT",0xa1},{u"VK_CONTROL",0x11},{u"VK_LCONTROL",0xa2},{u"VK_RCONTROL",0xa3},
                    {u"VK_MENU",0x12},{u"VK_LMENU",0xa4},{u"VK_RMENU",0xa5},{u"VK_LWIN",0x5b},{u"VK_RWIN",0x5c},{u"VK_CAPITAL",0x14},
                    {u"VK_SPACE",0x20},{u"VK_BACK",8},{u"VK_RETURN",13},{u"VK_TAB",9},{u"VK_ESCAPE",27},
                    {u"VK_OEM_1",0xba},{u"VK_OEM_2",0xbf},{u"VK_OEM_4",0xdb},{u"VK_OEM_7",0xde},{u"VK_OEM_COMMA",0xbc},{u"VK_OEM_PERIOD",0xbe}};
                for (char16_t c : std::u16string_view(u"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"))
                    result.emplace_back(std::u16string(u"VK_") + c, c);
                for (int i = 1; i <= 24; ++i) result.emplace_back(u"VK_F" + Decimal(i), 0x6f + i);
                std::sort(result.begin(), result.end(), [](const auto& left, const auto& right)
                {
                    return left.second != right.second ? left.second < right.second : left.first < right.first;
                });
                return result;
            }();
            return names;
        }
        std::optional<int> Key(std::u16string_view token)
        {
            token = TrimText(token);
            if (token.size() >= 2 && token[0] == u'0' && (token[1] == u'x' || token[1] == u'X'))
            {
                token.remove_prefix(2);
                auto white = [](char16_t c) { return c == u' ' || (c >= 9 && c <= 13); };
                while (!token.empty() && white(token.front())) token.remove_prefix(1);
                std::uint64_t number = 0; std::size_t digits = 0;
                for (; digits < token.size(); ++digits)
                {
                    char16_t c = token[digits];
                    int value = c >= u'0' && c <= u'9' ? c-u'0' : c >= u'a' && c <= u'f' ? c-u'a'+10 : c >= u'A' && c <= u'F' ? c-u'A'+10 : -1;
                    if (value < 0) break;
                    number = number * 16 + value;
                    if (number > 0xffffffffULL) return {};
                }
                if (!digits) return {};
                token.remove_prefix(digits);
                while (!token.empty() && white(token.front())) token.remove_prefix(1);
                for (char16_t c : token) if (c) return {};
                return std::bit_cast<std::int32_t>(static_cast<std::uint32_t>(number));
            }
            std::int32_t number;
            if (ParseIntegerToken(token, number)) return number;
            auto name = FoldOrdinalCode(token);
            for (const auto& [key, vk] : KeyNames()) if (name == key) return vk;
            return {};
        }
    }
    std::u16string BuildSelectionBindingsText(const SelectionBindings& bindings)
    {
        std::u16string text;
        for (int rank = 1; rank <= 10; ++rank)
        {
            if (rank != 1) text += u"\r\n";
            text += Decimal(rank) + u"\u9009";
            auto found = bindings.find(rank);
            if (found == bindings.end()) continue;
            std::vector<int> seen;
            for (int key : found->second)
            {
                if (std::find(seen.begin(), seen.end(), key) != seen.end()) continue;
                seen.push_back(key);
                text += u" " + Hex(key);
            }
        }
        return text;
    }
    std::u16string BuildSelectionFileText(const SelectionBindings& bindings)
    {
        std::u16string text =
            u"# TigerClaw \u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u914d\u7f6e\r\n"
            u"# \u683c\u5f0f\uff1a<n\u9009> <\u952e1> <\u952e2> ...\r\n"
            u"# \u952e\u53ef\u4ee5\u5199\u6210\uff1a\u5341\u8fdb\u5236\uff0849\uff09\u3001\u5341\u516d\u8fdb\u5236\uff080x31\uff09\u3001\u6216\u952e\u540d\uff08VK_1\uff09\r\n"
            u"# \u4e0b\u9762\u662f\u53ef\u7528\u6309\u952e\u540d\u79f0\u4e0e\u952e\u503c\uff1a";
        for (const auto& [name, key] : KeyNames())
            text += u"\r\n# " + name + u" " + Hex(key) + u" " + Decimal(key);
        return text + u"\r\n\r\n" + BuildSelectionBindingsText(bindings);
    }
    SelectionParseResult ParseSelectionBindings(std::span<const std::u16string> lines, const SelectionBindings& base)
    {
        SelectionParseResult result{true, base, {}};
        for (const auto& raw : lines)
        {
            auto line = TrimText(raw);
            if (line.empty() || line.front() == u'#') continue;
            std::vector<std::u16string_view> parts;
            while (!line.empty())
            {
                auto end = line.find_first_of(u" \t");
                if (end != 0) parts.push_back(line.substr(0, end));
                if (end == line.npos) break;
                line.remove_prefix(end + 1);
            }
            auto label = parts.front(); std::int32_t rank = 0;
            if (!label.ends_with(u"\u9009") || !ParseIntegerToken(label.substr(0, label.size()-1), rank) || rank < 1 || rank > 10)
                return {false, std::move(result.bindings), u"\u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u5b58\u5728\u65e0\u6548\u6807\u7b7e\uff1a" + std::u16string(label)};
            std::vector<int> keys;
            for (std::size_t index = 1; index < parts.size(); ++index)
            {
                auto key = Key(parts[index]);
                if (!key) return {false, std::move(result.bindings), u"\u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u5b58\u5728\u65e0\u6548\u6309\u952e\uff1a" + std::u16string(parts[index])};
                if (std::find(keys.begin(), keys.end(), *key) == keys.end()) keys.push_back(*key);
            }
            result.bindings[rank] = std::move(keys);
        }
        return result;
    }
}
