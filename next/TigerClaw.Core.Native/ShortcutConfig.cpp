#include "ActionShortcuts.h"
#include "LexiconText.h"
#include "CodeCase.h"
#include <vector>

namespace tiger::core
{
    namespace
    {
        const std::vector<std::pair<std::u16string, int>>& Names()
        {
            static const auto names = []
            {
                std::vector<std::pair<std::u16string, int>> result{
                    {u"VK_BACK",8},{u"VK_TAB",9},{u"VK_RETURN",13},{u"VK_ESCAPE",27},{u"VK_SPACE",32},
                    {u"VK_PRIOR",33},{u"VK_NEXT",34},{u"VK_END",35},{u"VK_HOME",36},{u"VK_LEFT",37},
                    {u"VK_UP",38},{u"VK_RIGHT",39},{u"VK_DOWN",40},{u"VK_INSERT",45},{u"VK_DELETE",46},
                    {u"VK_OEM_1",186},{u"VK_OEM_PLUS",187},{u"VK_OEM_COMMA",188},{u"VK_OEM_MINUS",189},
                    {u"VK_OEM_PERIOD",190},{u"VK_OEM_2",191},{u"VK_OEM_3",192},{u"VK_OEM_4",219},
                    {u"VK_OEM_5",220},{u"VK_OEM_6",221},{u"VK_OEM_7",222}};
                for (int key = 48; key <= 90; ++key)
                    if (key <= 57 || key >= 65) result.emplace_back(u"VK_" + std::u16string(1, static_cast<char16_t>(key)), key);
                for (int f = 1; f <= 24; ++f)
                {
                    auto decimal = std::to_string(f);
                    result.emplace_back(u"VK_F" + std::u16string(decimal.begin(), decimal.end()), 111 + f);
                }
                return result;
            }();
            return names;
        }
        std::optional<int> Key(std::u16string_view token)
        {
            auto folded = FoldOrdinalCode(token);
            if (!folded.starts_with(u"0X"))
            {
                for (const auto& [name, value] : Names()) if (folded == name) return value;
                return {};
            }
            // Valid gestures ultimately require 1..255. Parse that subset of
            // .NET HexNumber, including leading zeros and terminal NULs.
            auto hex = token.substr(2);
            auto asciiWhite = [](char16_t c) { return c == 32 || (c >= 9 && c <= 13); };
            while (!hex.empty() && asciiWhite(hex.front())) hex.remove_prefix(1);
            int value = 0;
            std::size_t i = 0;
            for (; i < hex.size(); ++i)
            {
                auto c = hex[i];
                int digit = c >= u'0' && c <= u'9' ? c - u'0' : c >= u'a' && c <= u'f' ? c - u'a' + 10 : c >= u'A' && c <= u'F' ? c - u'A' + 10 : -1;
                if (digit < 0) break;
                if (value > 15) return {};
                value = value * 16 + digit;
            }
            if (i == 0) return {};
            while (i < hex.size() && asciiWhite(hex[i])) ++i;
            while (i < hex.size() && hex[i] == 0) ++i;
            return i == hex.size() ? std::optional<int>(value) : std::nullopt;
        }
    }
    std::optional<ShortcutGesture> ShortcutGesture::Parse(std::u16string_view value)
    {
        bool ctrl = false, alt = false, shift = false, win = false;
        int key = 0;
        for (;;)
        {
            auto end = value.find(u'+');
            auto part = TrimText(value.substr(0, end));
            if (part.empty()) return {};
            auto folded = FoldOrdinalCode(part);
            bool* modifier = folded == u"CTRL" || folded == u"CONTROL" ? &ctrl : folded == u"ALT" ? &alt :
                folded == u"SHIFT" ? &shift : folded == u"WIN" || folded == u"WINDOWS" ? &win : nullptr;
            if (modifier) { if (*modifier) return {}; *modifier = true; }
            else
            {
                if (key != 0) return {};
                auto parsed = Key(part);
                if (!parsed) return {};
                key = *parsed;
            }
            if (end == std::u16string_view::npos) break;
            value.remove_prefix(end + 1);
        }
        return Create(key, ctrl, alt, shift, win);
    }
    std::u16string ShortcutGesture::ConfigString() const
    {
        std::u16string result;
        if (control) result += u"Ctrl+";
        if (alt) result += u"Alt+";
        if (shift) result += u"Shift+";
        for (const auto& [name, value] : Names()) if (value == key) return result + name;
        constexpr std::u16string_view digits = u"0123456789ABCDEF";
        return result + u"0x" + digits[(key >> 4) & 15] + digits[key & 15];
    }
    ActionBindings LoadActionBindings(const ConfigValues& config)
    {
        auto value = [&](std::u16string_view key) -> std::optional<std::u16string_view>
        {
            for (const auto& [name, text] : config) if (name == key) return text;
            return {};
        };
        auto boolean = [&](std::u16string_view key, bool fallback)
        {
            auto text = value(key);
            return text ? ParseConfigBool(*text, fallback) : fallback;
        };
        auto gesture = [&](std::u16string_view key, std::u16string_view fallback)
        {
            auto text = value(key);
            auto parsed = text ? ShortcutGesture::Parse(*text) : std::nullopt;
            return parsed ? parsed : ShortcutGesture::Parse(fallback);
        };
        ActionBindings bindings{
            gesture(u"\u624b\u52a8\u52a0\u8bcd\u5feb\u6377\u952e", u"Ctrl+VK_OEM_PLUS"),
            gesture(u"\u5207\u6362\u6700\u8fd1\u7801\u8868\u5feb\u6377\u952e", u"Ctrl+VK_M")};
        FilterActionBindings(bindings.addWord, bindings.recentSchema,
            boolean(u"Ctrl+\u7b49\u53f7\u624b\u52a8\u52a0\u8bcd", true),
            boolean(u"Ctrl+m\u5207\u6362\u6700\u8fd1\u7801\u8868", false),
            boolean(u"Ctrl+\u7a7a\u683c\u5207\u6362\u4e2d\u82f1\u6587", true),
            boolean(u"Alt+\\\u542f\u7528\u6216\u7981\u7528\u5916\u6302\u7248", true));
        return bindings;
    }
}
