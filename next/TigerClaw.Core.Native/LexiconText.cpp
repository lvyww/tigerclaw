#include "LexiconText.h"

namespace tiger::core
{
    bool IsDotNetWhiteSpace(char16_t c)
    {
        return (c >= 0x0009 && c <= 0x000d) || c == 0x0020 || c == 0x0085 || c == 0x00a0 ||
            c == 0x1680 || (c >= 0x2000 && c <= 0x200a) || c == 0x2028 || c == 0x2029 ||
            c == 0x202f || c == 0x205f || c == 0x3000;
    }
    std::u16string_view TrimText(std::u16string_view value)
    {
        while (!value.empty() && IsDotNetWhiteSpace(value.front())) value.remove_prefix(1);
        while (!value.empty() && IsDotNetWhiteSpace(value.back())) value.remove_suffix(1);
        return value;
    }
    std::u16string StripInlineComment(std::u16string_view line)
    {
        std::u16string result;
        result.reserve(line.size());
        bool oddSlashes = false;
        for (auto c : line)
        {
            if (c == u'#')
            {
                if (!oddSlashes) break;
                result.pop_back();
                result.push_back(c);
                oddSlashes = false;
                continue;
            }
            result.push_back(c);
            oddSlashes = c == u'\\' && !oddSlashes;
        }
        return std::u16string(TrimText(result));
    }
    std::u16string_view CandidateDisplayText(std::u16string_view entry)
    {
        auto separator = entry.find(char16_t(0x1e));
        if (separator == entry.npos) return entry;
        return separator == 0 ? entry.substr(1) : entry.substr(0, separator);
    }
    std::u16string_view CandidateCommitText(std::u16string_view entry)
    {
        auto separator = entry.find(u'\x001e');
        if (separator == entry.npos) return entry;
        auto commit = entry.substr(separator + 1);
        return commit.empty() ? entry.substr(0, separator) : commit;
    }
    namespace
    {
        std::u16string Replace(std::u16string_view value, std::u16string_view from, std::u16string_view to)
        {
            std::u16string result;
            std::size_t start = 0;
            for (;;)
            {
                auto found = value.find(from, start);
                if (found == value.npos) { result.append(value.substr(start)); return result; }
                result.append(value.substr(start, found - start));
                result.append(to);
                start = found + from.size();
            }
        }
    }
    std::u16string DecodeLexiconEscapes(std::u16string_view value)
    {
        // Deliberately preserve C# WrapDec's replacement order and legacy marker
        // collision behavior. A conventional escape parser is not equivalent.
        constexpr auto marker = u"Bime20231222BIME";
        auto result = Replace(value, u"\\\\", marker);
        result = Replace(result, u"\\t", u"\t");
        result = Replace(result, u"\\n", u"\r\n");
        result = Replace(result, u"\\s", u" ");
        return Replace(result, marker, u"\\");
    }
    std::u16string ParseLexiconEntryToken(std::u16string_view token)
    {
        auto raw = DecodeLexiconEscapes(TrimText(token));
        auto marker = raw.find(u"=>");
        if (marker == raw.npos || marker == 0 || marker + 2 >= raw.size()) return raw;
        auto display = raw.substr(0, marker);
        auto commit = raw.substr(marker + 2);
        if (display == commit) return commit;
        return display + u'\x001e' + commit;
    }
    namespace
    {
        std::vector<std::u16string_view> Split(std::u16string_view text, bool tabs)
        {
            std::vector<std::u16string_view> parts;
            std::size_t start = 0;
            for (std::size_t i = 0; i <= text.size(); ++i)
            {
                if (i != text.size() && text[i] != u' ' && (!tabs || text[i] != u'\t')) continue;
                if (i > start) parts.push_back(text.substr(start, i - start));
                start = i + 1;
            }
            return parts;
        }
        bool LikelyCode(std::u16string_view text)
        {
            text = TrimText(text);
            if (text.empty()) return false;
            for (auto c : text)
                if (!((c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z') ||
                    (c >= u'0' && c <= u'9') || std::u16string_view(u";/[]'-=").find(c) != std::u16string_view::npos))
                    return false;
            return true;
        }
    }
    bool LexiconLineParser::Integer(std::u16string_view token, std::int32_t& value) const
    {
        return ParseIntegerToken(token, value, positive_, negative_);
    }
    bool ParseIntegerToken(std::u16string_view token, std::int32_t& value, std::u16string_view positive_, std::u16string_view negative_)
    {
        // NumberStyles.Integer accepts ASCII whitespace, not Char.IsWhiteSpace.
        auto white = [](char16_t c) { return c == u' ' || (c >= 9 && c <= 13); };
        while (!token.empty() && white(token.front())) token.remove_prefix(1);
        bool negative = false;
        if (!positive_.empty() && token.starts_with(positive_)) token.remove_prefix(positive_.size());
        else if (!negative_.empty() && token.starts_with(negative_))
        { negative = true; token.remove_prefix(negative_.size()); }
        else if (negative_.size() == 1 &&
            std::u16string_view(u"\u2012\u207b\u208b\u2212\u2796\ufe63\uff0d").find(negative_[0]) != std::u16string_view::npos &&
            token.starts_with(u"-"))
        { negative = true; token.remove_prefix(1); } // .NET 10 AllowHyphenDuringParsing.
        std::uint64_t number = 0;
        std::size_t digits = 0;
        while (digits < token.size() && token[digits] >= u'0' && token[digits] <= u'9')
        {
            number = number * 10 + (token[digits++] - u'0');
            if (number > (negative ? 2147483648ULL : 2147483647ULL)) return false;
        }
        if (!digits) return false;
        token.remove_prefix(digits);
        while (!token.empty() && white(token.front())) token.remove_prefix(1);
        // .NET's integer parser accepts trailing NULs after trailing whitespace.
        for (auto c : token) if (c != 0) return false;
        auto signedNumber = static_cast<std::int64_t>(number);
        value = static_cast<std::int32_t>(negative ? -signedNumber : signedNumber);
        return true;
    }
    std::vector<LexiconRow> LexiconLineParser::Parse(std::u16string_view raw)
    {
        auto trimmed = TrimText(raw);
        if (trimmed.empty() || trimmed.front() == u'#') return {};
        if (!body_) { if (trimmed == u"...") body_ = true; return {}; }
        auto line = StripInlineComment(trimmed);
        if (line.empty()) return {};
        for (auto prefix : {u"{\u6dfb\u52a0}", u"{\u5220\u9664}", u"{\u7f6e\u9876}", u"{\u524d\u79fb}"})
            if (line.starts_with(prefix)) return {}; // User adjustment directives are loaded separately.
        if (line.find(u'\t') == line.npos)
        {
            auto parts = Split(line, false);
            if (parts.size() >= 2 && LikelyCode(parts[0]))
            {
                auto code = std::u16string(TrimText(parts[0]));
                for (auto& c : code) if (c >= u'A' && c <= u'Z') c = char16_t(c + 32);
                std::int32_t frequency = 0;
                auto end = parts.size();
                if (end >= 3 && Integer(parts.back(), frequency)) --end;
                std::vector<LexiconRow> rows;
                for (std::size_t i = 1; i < end; ++i)
                {
                    auto text = ParseLexiconEntryToken(parts[i]);
                    if (!text.empty()) rows.push_back({code, std::move(text), frequency});
                }
                if (!rows.empty()) return rows;
            }
        }
        auto parts = Split(line, true);
        if (parts.empty()) return {};
        auto text = ParseLexiconEntryToken(parts[0]);
        if (text.empty()) return {};
        std::u16string_view code;
        std::int32_t frequency = 0, parsed = 0;
        if (parts.size() >= 3 && Integer(parts[2], parsed)) { code = parts[1]; frequency = parsed; }
        else if (parts.size() >= 3 && Integer(parts[1], parsed)) { code = parts[2]; frequency = parsed; }
        else if (parts.size() >= 2)
        {
            if (Integer(parts[1], parsed)) frequency = parsed;
            else code = parts[1];
        }
        return {{std::u16string(TrimText(code)), std::move(text), frequency}};
    }
}
