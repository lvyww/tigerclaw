#pragma once
#include <string>
#include <string_view>
#include <vector>
#include <span>
#include <utility>
#include <filesystem>
#include <cstdint>
namespace tiger::core
{
    using ConfigValues = std::vector<std::pair<std::u16string, std::u16string>>;
    ConfigValues ParseConfigLines(std::span<const std::u16string> lines);
    // Read-only preparation stage. Missing/unreadable files throw; creation,
    // publication and persistence belong to the runtime transaction.
    ConfigValues ReadConfigFile(const std::filesystem::path& path);
    std::vector<std::uint8_t> SerializeConfig(const ConfigValues& config);
    std::vector<std::uint8_t> EncodeUtf8Text(std::u16string_view text, bool bom = false);
    void WriteUtf8TextFile(const std::filesystem::path& path, std::u16string_view text, bool bom = false);
    // Explicit write operation; caller chooses path and handles errors. Does
    // not create directories or perform runtime/registry side effects.
    void WriteConfigFile(const std::filesystem::path& path, const ConfigValues& config);
    void EnsureConfigFile(const std::filesystem::path& path);
    std::u16string NormalizeCodeRoot(std::u16string_view value);
    bool ParseConfigBool(std::u16string_view value, bool fallback);
}
