#include "RuntimeLexicons.h"
#include "RuntimePaths.h"
#include "PinyinLexicon.h"
#include "CodeCase.h"
#include "ConfigDefaults.h"
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

    void RuntimeLexicons::Reload(const std::filesystem::path& configFile, const AcceptSnapshot& accept, bool reloadBindings)
    {
        std::lock_guard guard(_writer);
        ReloadNoLock(configFile, accept, reloadBindings);
    }

    void RuntimeLexicons::ReloadTables(const AcceptSnapshot& accept)
    {
        std::lock_guard guard(_writer);
        auto previous = Read();
        if (!previous) throw std::logic_error("Runtime configuration is not loaded");
        LoadSnapshotNoLock(previous->config, accept, true, false);
    }

    void RuntimeLexicons::Initialize(const std::filesystem::path& configFile)
    {
        std::lock_guard guard(_writer);
        EnsureConfigFile(configFile);
        ReloadNoLock(configFile);
    }

    bool RuntimeLexicons::SetConfig(std::u16string_view key, std::u16string_view value,
        bool& changed, std::u16string& error, const AcceptSnapshot& accept)
    {
        changed = false; error.clear(); key = TrimText(key); value = TrimText(value);
        if (key.empty()) { error = u"key is empty"; return false; }
        std::u16string canonical;
        for (const auto& [known, unused] : ConfigDefaults)
            if (FoldOrdinalCode(known) == FoldOrdinalCode(key)) { canonical = known; break; }
        if (canonical.empty()) { error = u"unknown key"; return false; }
        auto normalized = canonical == RootKey ? NormalizeCodeRoot(value) : std::u16string(value);
        std::lock_guard guard(_writer);
        auto previous = Read();
        if (!previous) throw std::logic_error("Runtime configuration is not loaded");
        auto next = std::make_shared<RuntimeLexiconSnapshot>(*previous);
        auto& old = Value(next->config, canonical);
        if (old == normalized) return true;
        old = std::move(normalized);
        if (canonical == RootKey || canonical == CurrentKey)
            LoadSnapshotNoLock(std::move(next->config), accept, false);
        else
        {
            next->actionBindings = LoadActionBindings(next->config);
            next->generation++; next->configVersion++;
            if (accept) accept(next);
            _current.store(std::move(next));
        }
        changed = true;
        return true;
    }

    void RuntimeLexicons::ReloadNoLock(const std::filesystem::path& configFile, const AcceptSnapshot& accept, bool reloadBindings)
    {
        LoadSnapshotNoLock(ReadConfigFile(configFile), accept, reloadBindings);
    }

    void RuntimeLexicons::LoadSnapshotNoLock(ConfigValues config, const AcceptSnapshot& accept, bool reloadBindings, bool configChanged)
    {
        auto next = std::make_shared<RuntimeLexiconSnapshot>();
        next->config = std::move(config);
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
        // Config reload prepares engine bindings in the same transaction.
        // Low-level table loads may retain the already accepted bindings.
        if (reloadBindings)
        {
            next->selectionBindings = ReadSelectionBindingsFile(_executableDirectory / u"自定义选重键.txt");
            next->selectionKeys = SelectionKeys(next->selectionBindings);
        }
        else if (previous)
        {
            next->selectionBindings = previous->selectionBindings;
            next->selectionKeys = previous->selectionKeys;
        }
        next->generation = previous ? previous->generation + 1 : 1;
        next->configVersion = (previous ? previous->configVersion : 0) + (configChanged ? 1 : 0) + (selection.usedFallback ? 1 : 0);
        next->lexiconVersion = (previous ? previous->lexiconVersion : 0) + 1;
        // No partial config/main/pinyin publication when any disk work fails.
        if (accept) accept(next);
        _cachedName.clear();
        _cachedSchema.reset();
        _current.store(std::move(next));
    }

    bool RuntimeLexicons::SwitchSchema(std::u16string_view name, const AcceptSnapshot& accept)
    {
        std::lock_guard guard(_writer);
        return SwitchSchemaNoLock(name, accept);
    }

    bool RuntimeLexicons::SwitchRecentSchema(const AcceptSnapshot& accept)
    {
        std::lock_guard guard(_writer);
        auto current = Read();
        if (!current) return false;
        auto target = ChooseRecentSchema(GetSchemaNames(current->root), current->schemaName, current->recentSchemas);
        return !target.empty() && SwitchSchemaNoLock(target, accept);
    }

    bool RuntimeLexicons::SwitchSchemaNoLock(std::u16string_view name, const AcceptSnapshot& accept)
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
        next->configVersion = previous->configVersion + 1;
        next->lexiconVersion = previous->lexiconVersion + 1;
        // Capacity one: only the schema just left survives. Pinyin belongs to
        // the runtime snapshot and is shared across all schema switches.
        auto leavingName = previous->schemaName;
        // All allocation/loading precedes host acceptance; after acceptance only
        // non-throwing ownership transfers remain. Rejection preserves config,
        // recent-schema history, generation and the one-entry table cache.
        if (accept) accept(next);
        _cachedName.swap(leavingName);
        _cachedSchema = previous->schema;
        _current.store(std::move(next));
        return true;
    }
}
