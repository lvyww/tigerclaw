#pragma once
#include <filesystem>
#include <string>
#include <vector>
namespace tiger::core
{
    // Locale is explicit for differential tests; an omitted value uses the
    // Windows user locale. Uses the Windows system ICU, like default .NET 10.
    std::vector<std::filesystem::path> GetOrderedLexiconFiles(const std::filesystem::path& directory,
        const std::string* locale = nullptr);
}
