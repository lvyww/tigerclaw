#pragma once
#include "ConfigParser.h"
#include "SchemaLexicon.h"
#include "RecentSchemas.h"
#include "CandidatePage.h"
#include "SelectionKeys.h"
#include "ActionShortcuts.h"
#include "LexiconAssembly.h"
#include "OrdinaryComposition.h"
#include <atomic>
#include <memory>
#include <mutex>
#include <functional>

namespace tiger::core
{
    struct RuntimeLexiconSnapshot
    {
        ConfigValues config;
        ActionBindings actionBindings = LoadActionBindings({});
        std::filesystem::path root;
        std::u16string schemaName;
        SchemaHistory recentSchemas;
        std::shared_ptr<const SchemaLexicon> schema;
        std::shared_ptr<const CompactLexicon> pinyin;
        SelectionBindings selectionBindings = DefaultSelectionBindings();
        SelectionKeys selectionKeys;
        std::uint64_t generation = 0;
        std::uint64_t configVersion = 0, lexiconVersion = 0;
        OrdinarySettings InputSettings(int decoderVersion) const
        {
            return LoadOrdinarySettings(config, decoderVersion);
        }
    };

    // Immutable data ownership with explicit SaveConfig. No live IME state,
    // automatic config writeback or host startup integration,
    // shortcut dispatch, model processes or production IPC yet.
    class RuntimeLexicons
    {
    public:
        // Runs under the writer lock before publication. It must not mutate this
        // runtime or reenter its writer methods. Throwing rejects the switch.
        using AcceptSnapshot = std::function<void(std::shared_ptr<const RuntimeLexiconSnapshot>)>;
        explicit RuntimeLexicons(std::filesystem::path executableDirectory);
        std::shared_ptr<const RuntimeLexiconSnapshot> Read() const;
        void Reload(const std::filesystem::path& configFile, const AcceptSnapshot& accept = {}, bool reloadBindings = false);
        void ReloadTables(const AcceptSnapshot& accept = {});
        bool SetConfig(std::u16string_view key, std::u16string_view value, bool& changed,
            std::u16string& error, const AcceptSnapshot& accept = {});
        void Initialize(const std::filesystem::path& configFile);
        bool SwitchSchema(std::u16string_view name, const AcceptSnapshot& accept = {});
        bool SwitchRecentSchema(const AcceptSnapshot& accept = {});
        void SaveConfig(const std::filesystem::path& configFile);
        std::filesystem::path ExportLexicon() const;
        void ReloadSelectionBindings();
        // Publish main-table edits; adjustment-log failure does not undo a
        // successful in-memory change, matching C# best-effort persistence.
        bool AdjustCandidate(CandidateAdjustment operation, std::u16string_view code,
            std::u16string_view candidate);
        // Invalid text returns the parser error; filesystem failures throw.
        // Neither failure publishes a new snapshot.
        SelectionParseResult SaveSelectionBindings(std::span<const std::u16string> lines);
        CandidatePage GetCandidatePage(std::u16string_view code, std::int64_t page, int size, bool pinyin = false) const;
    private:
        void ReloadNoLock(const std::filesystem::path& configFile, const AcceptSnapshot& accept = {}, bool reloadBindings = false);
        void LoadSnapshotNoLock(ConfigValues config, const AcceptSnapshot& accept, bool reloadBindings, bool configChanged = true);
        bool SwitchSchemaNoLock(std::u16string_view name, const AcceptSnapshot& accept);
        std::filesystem::path _executableDirectory;
        std::atomic<std::shared_ptr<const RuntimeLexiconSnapshot>> _current;
        std::mutex _writer;
        std::u16string _cachedName;
        std::shared_ptr<const SchemaLexicon> _cachedSchema;
    };
}
