#pragma once
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace tiger::core
{
    std::filesystem::path ResolveCodeRoot(const std::filesystem::path& executableDirectory,
        std::u16string_view configured);
    struct SchemaSelection
    {
        std::filesystem::path directory;
        std::u16string name;
        bool usedFallback = false;
    };
    // Selection only: the runtime owns config updates/versioning/persistence.
    SchemaSelection SelectSchemaDirectory(const std::filesystem::path& root,
        std::u16string_view configuredName);
    std::vector<std::u16string> GetSchemaNames(const std::filesystem::path& root);
}
