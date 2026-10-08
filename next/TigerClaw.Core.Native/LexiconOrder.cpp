#include "LexiconOrder.h"
#include "CodeCase.h"
#include <algorithm>
#include "WindowsLocale.h"
#include <system_error>
#include <stdexcept>
#include <climits>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    std::vector<std::filesystem::path> GetOrderedLexiconFiles(const std::filesystem::path& directory, const std::string* locale)
    {
#ifdef _WIN32
        const auto localeName = WindowsLocaleName(locale);
        struct File { std::filesystem::path path; std::vector<unsigned char> sortKey; bool preferred; };
        std::vector<File> textFiles, yamlFiles;
        auto absoluteDirectory = std::filesystem::absolute(directory).lexically_normal();
        auto schema = absoluteDirectory.filename().u16string();
        if (schema.empty()) schema = absoluteDirectory.parent_path().filename().u16string();
        auto preferredText = FoldOrdinalCode(schema + u".txt");
        auto preferredYaml = FoldOrdinalCode(schema + u".dict.yaml");
        for (const auto& item : std::filesystem::directory_iterator(directory))
        {
            if (item.is_directory()) continue;
            auto name = item.path().filename().u16string();
            auto folded = FoldOrdinalCode(name);
            bool yaml = folded.ends_with(u".DICT.YAML");
            if (!yaml && !folded.ends_with(u".TXT")) continue;
            const auto wide = item.path().filename().native();
            if (wide.size() > INT_MAX) throw std::runtime_error("Filename too long");
            constexpr DWORD flags = LCMAP_SORTKEY | NORM_LINGUISTIC_CASING;
            const int length = LCMapStringEx(localeName.c_str(), flags, wide.data(),
                static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr, 0);
            if (!length) throw std::system_error(static_cast<int>(GetLastError()),
                std::system_category(), "Size lexicon sort key");
            // Sort-key output is bytes (not wchar_t), even for the W API.
            std::vector<unsigned char> key(static_cast<std::size_t>(length));
            if (LCMapStringEx(localeName.c_str(), flags, wide.data(), static_cast<int>(wide.size()),
                reinterpret_cast<LPWSTR>(key.data()), length, nullptr, nullptr, 0) != length)
                throw std::system_error(static_cast<int>(GetLastError()),
                    std::system_category(), "Read lexicon sort key");
            File file{item.path(), std::move(key), folded == preferredText || folded == preferredYaml};
            (yaml ? yamlFiles : textFiles).push_back(std::move(file));
        }
        textFiles.insert(textFiles.end(), std::make_move_iterator(yamlFiles.begin()), std::make_move_iterator(yamlFiles.end()));
        std::stable_sort(textFiles.begin(), textFiles.end(), [&](const File& a, const File& b)
        {
            if (a.preferred != b.preferred) return a.preferred;
            // Byte keys give a strict order even for NLS contextual kana cases
            // where CompareStringEx(a,b) and CompareStringEx(b,a) both return >.
            return a.sortKey < b.sortKey;
        });
        std::vector<std::filesystem::path> result;
        for (auto& item : textFiles) result.push_back(std::move(item.path));
        return result;
#else
        (void)directory; (void)locale;
        throw std::runtime_error("CurrentCulture directory ordering requires Windows NLS in this build");
#endif
    }
}
