#include "LexiconOrder.h"
#include "CodeCase.h"
#include <algorithm>
#include <memory>
#include <stdexcept>
#include <climits>
#ifdef _WIN32
#include <windows.h>
#include <icu.h>
#endif

namespace tiger::core
{
    std::vector<std::filesystem::path> GetOrderedLexiconFiles(const std::filesystem::path& directory, const std::string* locale)
    {
#ifdef _WIN32
        std::string localeName;
        if (locale) localeName = *locale;
        else
        {
            wchar_t name[LOCALE_NAME_MAX_LENGTH];
            if (!GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH)) throw std::runtime_error("Cannot read Windows locale");
            // Windows locale names contain ASCII language/script/region tags.
            for (auto p = name; *p; ++p)
            {
                if (*p > 127) throw std::runtime_error("Non-ASCII locale name");
                localeName.push_back(static_cast<char>(*p));
            }
        }
        UErrorCode status = U_ZERO_ERROR;
        std::unique_ptr<UCollator, decltype(&ucol_close)> collator(ucol_open(localeName.c_str(), &status), &ucol_close);
        if (U_FAILURE(status) || !collator) throw std::runtime_error("Cannot open ICU collator");
        struct File { std::filesystem::path path; std::u16string name; bool preferred; };
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
            File file{item.path(), std::move(name), folded == preferredText || folded == preferredYaml};
            (yaml ? yamlFiles : textFiles).push_back(std::move(file));
        }
        textFiles.insert(textFiles.end(), std::make_move_iterator(yamlFiles.begin()), std::make_move_iterator(yamlFiles.end()));
        std::stable_sort(textFiles.begin(), textFiles.end(), [&](const File& a, const File& b)
        {
            if (a.preferred != b.preferred) return a.preferred;
            if (a.name.size() > INT_MAX || b.name.size() > INT_MAX) throw std::runtime_error("Filename too long");
            // SDK UChar is wchar_t on Windows; copy to avoid aliasing char16_t.
            std::vector<UChar> left(a.name.begin(), a.name.end()), right(b.name.begin(), b.name.end());
            return ucol_strcoll(collator.get(), left.data(), static_cast<int32_t>(left.size()),
                right.data(), static_cast<int32_t>(right.size())) == UCOL_LESS;
        });
        std::vector<std::filesystem::path> result;
        for (auto& item : textFiles) result.push_back(std::move(item.path));
        return result;
#else
        (void)directory; (void)locale;
        throw std::runtime_error("CurrentCulture directory ordering requires Windows system ICU in this build");
#endif
    }
}
