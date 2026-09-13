#include "RuntimePaths.h"
#include "ConfigParser.h"
#include "CodeCase.h"
#include "LexiconText.h"
#include <algorithm>
#include <unordered_set>
#include <stdexcept>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    namespace
    {
        bool FoldedScalarLess(std::u16string_view a, std::u16string_view b)
        {
            auto next = [](std::u16string_view& text) -> std::uint32_t
            {
                std::uint32_t value = text.front(); text.remove_prefix(1);
                if (value >= 0xd800 && value <= 0xdbff && !text.empty() && text.front() >= 0xdc00 && text.front() <= 0xdfff)
                {
                    value = 0x10000 + ((value - 0xd800) << 10) + (text.front() - 0xdc00);
                    text.remove_prefix(1);
                }
                return value;
            };
            while (!a.empty() && !b.empty())
            {
                auto left = next(a), right = next(b);
                if (left != right) return left < right;
            }
            return a.empty() && !b.empty();
        }
    }
    std::vector<std::u16string> GetSchemaNames(const std::filesystem::path& root)
    {
        try
        {
            if (root.empty() || !std::filesystem::is_directory(root)) return {};
            std::vector<std::u16string> names;
            std::unordered_set<std::u16string> seen;
            for (const auto& entry : std::filesystem::directory_iterator(root))
            {
                if (!entry.is_directory()) continue;
                auto name = entry.path().filename().u16string();
                if (TrimText(name).empty()) continue;
                auto folded = FoldOrdinalCode(name);
                if (seen.insert(std::move(folded)).second)
                    names.push_back(std::move(name));
            }
            std::stable_sort(names.begin(), names.end(), [](const auto& a, const auto& b) { return FoldedScalarLess(FoldOrdinalCode(a), FoldOrdinalCode(b)); });
            return names;
        }
        catch (...) { return {}; } // GetSchemaList is best-effort in the reference.
    }
    std::filesystem::path ResolveCodeRoot(const std::filesystem::path& executableDirectory,
        std::u16string_view configured)
    {
#ifdef _WIN32
        auto normalized = NormalizeCodeRoot(configured);
        if (normalized.find(char16_t(0)) != normalized.npos)
            throw std::invalid_argument("Embedded NUL in code root");
        // .NET IsPathRooted includes drive-relative C:foo and root-relative
        // paths. Those resolve against the process, not the executable folder.
        bool rooted = normalized.front() == u'/' || normalized.front() == u'\\' ||
            (normalized.size() >= 2 && normalized[1] == u':');
        std::filesystem::path path(normalized);
        if (!rooted) path = executableDirectory / path;
        auto raw = path.native();
        if (raw.find(wchar_t(0)) != raw.npos) throw std::invalid_argument("Embedded NUL in executable directory");
        // Extended device paths are already normalized from .NET's perspective.
        if (raw.starts_with(L"\\\\?\\") || raw.starts_with(L"\\??\\")) return path;
        DWORD size = GetFullPathNameW(raw.c_str(), 0, nullptr, nullptr);
        if (!size) throw std::runtime_error("Cannot resolve code root");
        for (;;)
        {
            std::vector<wchar_t> buffer(size);
            DWORD written = GetFullPathNameW(raw.c_str(), size, buffer.data(), nullptr);
            if (!written) throw std::runtime_error("Cannot resolve code root");
            if (written < size) return std::filesystem::path(std::wstring(buffer.data(), written));
            size = written + 1;
        }
#else
        (void)executableDirectory; (void)configured;
        throw std::runtime_error("Windows runtime path resolution requires Windows");
#endif
    }

    SchemaSelection SelectSchemaDirectory(const std::filesystem::path& root,
        std::u16string_view configuredName)
    {
        std::error_code error;
        if (root.empty() || !std::filesystem::is_directory(root, error)) return {};
        auto requested = FoldOrdinalCode(configuredName);
        SchemaSelection fallback;
        std::u16string fallbackKey;
        // C# Directory.GetDirectories completes enumeration before selecting.
        // Do not hide a later enumeration failure behind an early name match.
        std::vector<std::filesystem::path> directories;
        for (const auto& entry : std::filesystem::directory_iterator(root))
        {
            if (entry.is_directory()) directories.push_back(entry.path());
        }
        for (const auto& directory : directories)
        {
            auto name = directory.filename().u16string();
            auto folded = FoldOrdinalCode(name);
            if (!configuredName.empty() && folded == requested) return {directory, std::move(name), false};
            if (fallback.directory.empty() || FoldedScalarLess(folded, fallbackKey))
            {
                fallback = {directory, std::move(name), true};
                fallbackKey = std::move(folded);
            }
        }
        return fallback;
    }
}
