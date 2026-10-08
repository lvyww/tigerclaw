#pragma once
#include <filesystem>
#include <string>
#include <vector>
namespace tiger::core
{
    // Locale is explicit for differential tests; an omitted value uses the
    // Windows user locale. Uses stable Windows NLS sort keys (.NET UseNls=true GetSortKey).
    std::vector<std::filesystem::path> GetOrderedLexiconFiles(const std::filesystem::path& directory,
        const std::string* locale = nullptr);
}
