#include "RuntimeLexicons.h"
#include "RuntimePaths.h"
#include "PinyinLexicon.h"
#include "CodeCase.h"
#include <stdexcept>

namespace tiger::core
{
    namespace
    {
        constexpr auto RootKey = u"\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e";
        constexpr auto CurrentKey = u"\u5f53\u524d\u7801\u8868";
        constexpr auto RecentKey = u"\u6700\u8fd1\u7801\u8868\u5bf9";
        std::u16string& Value(ConfigValues& config, std::u16string_view key)
        {
            for (auto& [name, value] : config) if (name == key) return value;
            throw std::logic_error("Missing default configuration key");
        }
    }
    RuntimeLexicons::RuntimeLexicons(std::filesystem::path executableDirectory)
        : _executableDirectory(std::move(executableDirectory)), _current(nullptr) {}

    std::shared_ptr<const RuntimeLexiconSnapshot> RuntimeLexicons::Read() const
    {
        return _current.load();
    }

    void RuntimeLexicons::SaveConfig(const std::filesystem::path& configFile)
    {
        std::lock_guard guard(_writer);
        auto current = Read();
        if (!current) throw std::logic_error("Runtime configuration is not loaded");
        WriteConfigFile(configFile, current->config);
    }

    void RuntimeLexicons::ReloadSelectionBindings()
    {
        std::lock_guard guard(_writer);
        auto previous = Read();
        if (!previous) throw std::logic_error("Runtime configuration is not loaded");
        auto next = std::make_shared<RuntimeLexiconSnapshot>(*previous);
        next->selectionBindings = ReadSelectionBindingsFile(
            _executableDirectory / u"\u81ea\u5b9a\u4e49\u9009\u91cd\u952e.txt"); // Custom selection keys
        next->selectionKeys = SelectionKeys(next->selectionBindings);
        next->generation = previous->generation + 1;
        _current.store(std::move(next));
    }

    SelectionParseResult RuntimeLexicons::SaveSelectionBindings(std::span<const std::u16string> lines)
    {
        std::lock_guard guard(_writer);
        auto previous = Read();
        if (!previous) throw std::logic_error("Runtime configuration is not loaded");
        auto parsed = ParseSelectionBindings(lines);
        if (!parsed.success) return parsed;
        auto next = std::make_shared<RuntimeLexiconSnapshot>(*previous);
        auto path = _executableDirectory / u"\u81ea\u5b9a\u4e49\u9009\u91cd\u952e.txt";
        if (!path.parent_path().empty()) std::filesystem::create_directories(path.parent_path());
        WriteSelectionBindingsFile(path, parsed.bindings);
        // Match the reference's post-save reload, including fallback if another
        // process makes the file invalid or unreadable between write and read.
        next->selectionBindings = ReadSelectionBindingsFile(path);
        next->selectionKeys = SelectionKeys(next->selectionBindings);
        next->generation = previous->generation + 1;
        _current.store(std::move(next));
        return parsed;
    }

    CandidatePage RuntimeLexicons::GetCandidatePage(std::u16string_view code, std::int64_t page, int size, bool pinyin) const
    {
        auto current = Read();
        if (!current) return {};
        return ReadCandidatePage(pinyin ? *current->pinyin : current->schema->table, code, page, size);
    }

    void RuntimeLexicons::Reload(const std::filesystem::path& configFile)
    {
        std::lock_guard guard(_writer);
        ReloadNoLock(configFile);
    }

    void RuntimeLexicons::Initialize(const std::filesystem::path& configFile)
    {
        std::lock_guard guard(_writer);
        EnsureConfigFile(configFile);
        ReloadNoLock(configFile);
    }

    void RuntimeLexicons::ReloadNoLock(const std::filesystem::path& configFile)
    {
        auto next = std::make_shared<RuntimeLexiconSnapshot>();
        next->config = ReadConfigFile(configFile);
        next->actionBindings = LoadActionBindings(next->config);
        next->root = ResolveCodeRoot(_executableDirectory, Value(next->config, RootKey));
        auto selection = SelectSchemaDirectory(next->root, Value(next->config, CurrentKey));
        next->schemaName = selection.name;
        next->recentSchemas = SeedRecentSchemas(Value(next->config, RecentKey));
        if (!selection.name.empty())
        {
            RecordRecentSchema(next->recentSchemas, selection.name);
            Value(next->config, RecentKey) = SerializeRecentSchemas(next->recentSchemas);
        }
        if (selection.usedFallback) Value(next->config, CurrentKey) = selection.name;
        next->schema = std::make_shared<const SchemaLexicon>(LoadSchemaLexicon(selection.directory));
        next->pinyin = std::make_shared<const CompactLexicon>(LoadPinyinLexicon(_executableDirectory));
        auto previous = Read();
        // Full table reload does not itself reload the engine-owned bindings.
        // The future host invokes ReloadSelectionBindings at engine startup and
        // explicit selection-setting changes, matching the C# ownership boundary.
        if (previous)
        {
            next->selectionBindings = previous->selectionBindings;
            next->selectionKeys = previous->selectionKeys;
        }
        next->generation = previous ? previous->generation + 1 : 1;
        // No partial config/main/pinyin publication when any disk work fails.
        _cachedName.clear();
        _cachedSchema.reset();
        _current.store(std::move(next));
    }

    bool RuntimeLexicons::SwitchSchema(std::u16string_view name)
    {
        std::lock_guard guard(_writer);
        return SwitchSchemaNoLock(name);
    }

    bool RuntimeLexicons::SwitchRecentSchema()
    {
        std::lock_guard guard(_writer);
        auto current = Read();
        if (!current) return false;
        auto target = ChooseRecentSchema(GetSchemaNames(current->root), current->schemaName, current->recentSchemas);
        return !target.empty() && SwitchSchemaNoLock(target);
    }

    bool RuntimeLexicons::SwitchSchemaNoLock(std::u16string_view name)
    {
        auto previous = Read();
        if (!previous || name.empty() || FoldOrdinalCode(name) == FoldOrdinalCode(previous->schemaName)) return false;
        auto selection = SelectSchemaDirectory(previous->root, name);
        // An explicit switch must not silently select an unrelated fallback.
        if (selection.directory.empty() || selection.usedFallback) return false;
        auto next = std::make_shared<RuntimeLexiconSnapshot>(*previous);
        next->schemaName = selection.name;
        Value(next->config, CurrentKey) = selection.name;
        RecordRecentSchema(next->recentSchemas, selection.name);
        Value(next->config, RecentKey) = SerializeRecentSchemas(next->recentSchemas);
        next->schema = _cachedSchema && FoldOrdinalCode(_cachedName) == FoldOrdinalCode(selection.name)
            ? _cachedSchema : std::make_shared<const SchemaLexicon>(LoadSchemaLexicon(selection.directory));
        next->generation = previous->generation + 1;
        // Capacity one: only the schema just left survives. Pinyin belongs to
        // the runtime snapshot and is shared across all schema switches.
        auto leavingName = previous->schemaName;
        _cachedName.swap(leavingName);
        _cachedSchema = previous->schema;
        _current.store(std::move(next));
        return true;
    }
}
