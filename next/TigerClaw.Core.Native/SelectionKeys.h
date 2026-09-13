#pragma once
#include <map>
#include <unordered_map>
#include <vector>
#include <string>
#include <span>
#include <optional>
#include <filesystem>
#include "OutputActions.h"
namespace tiger::core
{
    using SelectionBindings = std::map<int, std::vector<int>>;
    SelectionBindings DefaultSelectionBindings();
    int ResolveSelectionVirtualKey(int vk, int scan, bool extended);
    struct SelectionParseResult { bool success = true; SelectionBindings bindings; std::u16string error; };
    SelectionParseResult ParseSelectionBindings(std::span<const std::u16string> lines, const SelectionBindings& base = DefaultSelectionBindings());
    // Read-only stage: missing/unreadable/invalid files use the complete defaults.
    // Default-file creation is a separate, not yet implemented startup action.
    SelectionBindings ReadSelectionBindingsFile(const std::filesystem::path& path);
    std::u16string BuildSelectionBindingsText(const SelectionBindings& bindings);
    std::u16string BuildSelectionFileText(const SelectionBindings& bindings);
    void WriteSelectionBindingsFile(const std::filesystem::path& path, const SelectionBindings& bindings);
    void EnsureSelectionBindingsFile(const std::filesystem::path& path);
    struct SelectionResult { bool recognized = false; std::u16string text; };
    class SelectionKeys
    {
    public:
        explicit SelectionKeys(const SelectionBindings& bindings = DefaultSelectionBindings());
        std::optional<int> Number(int vk) const;
        SelectionResult Select(int vk, std::span<const std::u16string> page, const OutputContext& context = {}) const;
    private:
        std::unordered_map<int, int> _numbers;
    };
}
