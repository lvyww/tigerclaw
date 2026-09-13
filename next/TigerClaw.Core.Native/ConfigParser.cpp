#include "ConfigParser.h"
#include "ConfigDefaults.h"
#include "CodeCase.h"
#include "LexiconText.h"
#include "LexiconFile.h"
#include <unordered_map>
#include <algorithm>
#include <fstream>
#include <stdexcept>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    std::vector<std::uint8_t> SerializeConfig(const ConfigValues& config)
    {
        std::unordered_map<std::u16string, std::u16string_view> values;
        for (const auto& [key, value] : config) values[FoldOrdinalCode(key)] = value;
        std::u16string text = u"# TigerClaw.Core config\r\n";
        for (const auto& [key, ignored] : ConfigDefaults)
        {
            (void)ignored;
            auto found = values.find(FoldOrdinalCode(key));
            if (found == values.end()) continue;
            text += key; text += u'\t'; text += found->second; text += u"\r\n";
        }
        return EncodeUtf8Text(text);
    }
    std::vector<std::uint8_t> EncodeUtf8Text(std::u16string_view text, bool bom)
    {
        std::vector<std::uint8_t> bytes;
        if (bom) bytes = {0xef, 0xbb, 0xbf};
        for (std::size_t index = 0; index < text.size(); ++index)
        {
            std::uint32_t scalar = text[index];
            if (scalar >= 0xd800 && scalar <= 0xdbff && index + 1 < text.size() && text[index+1] >= 0xdc00 && text[index+1] <= 0xdfff)
                scalar = 0x10000 + ((scalar - 0xd800) << 10) + (text[++index] - 0xdc00);
            else if (scalar >= 0xd800 && scalar <= 0xdfff) scalar = 0xfffd;
            auto append = [&](std::uint32_t byte) { bytes.push_back(static_cast<std::uint8_t>(byte)); };
            if (scalar < 0x80) append(scalar);
            else if (scalar < 0x800) { append(0xc0 | (scalar >> 6)); append(0x80 | (scalar & 63)); }
            else if (scalar < 0x10000) { append(0xe0 | (scalar >> 12)); append(0x80 | ((scalar >> 6) & 63)); append(0x80 | (scalar & 63)); }
            else { append(0xf0 | (scalar >> 18)); append(0x80 | ((scalar >> 12) & 63)); append(0x80 | ((scalar >> 6) & 63)); append(0x80 | (scalar & 63)); }
        }
        return bytes;
    }
    static void WriteBytes(const std::filesystem::path& path, std::span<const std::uint8_t> bytes)
    {
        if (path.native().find(std::filesystem::path::value_type(0)) != path.native().npos)
            throw std::invalid_argument("Embedded NUL in configuration path");
#ifdef _WIN32
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot open configuration for writing");
        struct Close { HANDLE file; ~Close() { CloseHandle(file); } } close{file};
        std::size_t offset = 0;
        while (offset < bytes.size())
        {
            DWORD written = 0;
            DWORD count = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - offset, 1024 * 1024));
            if (!WriteFile(file, bytes.data() + offset, count, &written, nullptr) || written == 0)
                throw std::runtime_error("Cannot write configuration");
            offset += written;
        }
#else
        std::ofstream file(path, std::ios::binary | std::ios::trunc);
        file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        file.close();
        if (!file) throw std::runtime_error("Cannot write configuration");
#endif
    }
    void WriteUtf8TextFile(const std::filesystem::path& path, std::u16string_view text, bool bom)
    {
        WriteBytes(path, EncodeUtf8Text(text, bom));
    }
    void WriteConfigFile(const std::filesystem::path& path, const ConfigValues& config)
    {
        WriteBytes(path, SerializeConfig(config));
    }
    ConfigValues ReadConfigFile(const std::filesystem::path& path)
    {
        std::vector<std::u16string> lines;
        ReadLexiconLines(path, [&](std::u16string_view line) { lines.emplace_back(line); });
        return ParseConfigLines(lines);
    }
    void EnsureConfigFile(const std::filesystem::path& path)
    {
        if (path.empty() || path.native().find(std::filesystem::path::value_type(0)) != path.native().npos)
            throw std::invalid_argument("Invalid configuration path");
        auto parent = path.parent_path();
        if (!parent.empty()) std::filesystem::create_directories(parent);
        if (std::filesystem::is_regular_file(path)) return;
        // Like the reference startup helper, defaults are created only when
        // no existing file is present. Startup callers serialize initialization.
        ConfigValues defaults;
        for (const auto& [key, value] : ConfigDefaults) defaults.emplace_back(key, value);
        WriteConfigFile(path, defaults);
    }
    std::u16string NormalizeCodeRoot(std::u16string_view value)
    {
        value = TrimText(value);
        if (value.empty()) return u"\u7801\u8868";
        while (value.size() > 1 && (value.back() == u'\\' || value.back() == u'/'))
        {
            if (value.size() == 3 && value[1] == u':') break;
            value.remove_suffix(1);
        }
        return std::u16string(value);
    }
    bool ParseConfigBool(std::u16string_view value, bool fallback)
    {
        auto key = FoldOrdinalCode(TrimText(value));
        if (key == u"\u662f" || key == u"TRUE" || key == u"ON" || key == u"1") return true;
        if (key == u"\u5426" || key == u"FALSE" || key == u"OFF" || key == u"0") return false;
        return fallback;
    }
    ConfigValues ParseConfigLines(std::span<const std::u16string> lines)
    {
        ConfigValues result;
        std::unordered_map<std::u16string, std::size_t> known;
        for (const auto& [key, value] : ConfigDefaults)
        { known.emplace(FoldOrdinalCode(key), result.size()); result.emplace_back(key, value); }
        for (const auto& raw : lines)
        {
            std::u16string_view line(raw);
            while (!line.empty() && IsDotNetWhiteSpace(line.front())) line.remove_prefix(1);
            while (!line.empty() && (line.back() == u'\r' || line.back() == u'\n')) line.remove_suffix(1);
            if (line.empty() || line.front() == u'#') continue;
            auto separator = line.find_first_of(u"\t ,");
            if (separator == line.npos || separator == 0) continue;
            auto found = known.find(FoldOrdinalCode(TrimText(line.substr(0, separator))));
            if (found == known.end()) continue;
            result[found->second].second = TrimText(line.substr(separator + 1));
        }
        auto root = known.at(FoldOrdinalCode(u"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e"));
        result[root].second = NormalizeCodeRoot(result[root].second);
        return result;
    }
}
