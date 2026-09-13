#pragma once
#include "RuntimeLexicons.h"
#include "ChineseInputSession.h"
#include "SentenceEligibility.h"
#include "RuntimeSentenceState.h"
#include <limits>

namespace tiger::core
{
    // Serialized, offline event host. The caller owns RuntimeLexicons and must
    // serialize its mutations with this host. No pipe, UI or process startup.
    class RuntimeInput
    {
    public:
        RuntimeInput(RuntimeLexicons& runtime, UpperCaseServices upper,
            std::filesystem::path sentenceModel = {}, SentenceServiceLifecycle* service = nullptr)
            : _runtime(runtime), _session(std::move(upper), FixedLengthMixedDecoder(
                [this](std::u16string_view code)
                {
                    auto index = _snapshot->schema->table.Find(code);
                    return index && _snapshot->schema->table.CandidateCount(*index)
                        ? _snapshot->schema->table.Candidate(*index, 0) : std::u16string{};
                }, [this](std::u16string_view packed)
                {
                    auto text = CandidateCommitText(packed);
                    auto result = NormalizeOutputAction(text, _context);
                    return result.action == OutputAction::Text ? std::move(result.text) : std::u16string(text);
                }))
        {
            if (!sentenceModel.empty()) _sentence = std::make_unique<RuntimeSentenceState>(std::move(sentenceModel), service);
            Synchronize();
            _session.SetChinese(Boolean(u"\u9ed8\u8ba4\u4e2d\u6587", true));
        }
        RuntimeInput(const RuntimeInput&) = delete;
        RuntimeInput& operator=(const RuntimeInput&) = delete;
        PostProcessResult Process(const InputKeyEvent& event, CtrlSpaceState::Time now, OutputContext context = {})
        {
            _context = std::move(context);
            RefreshOutputContext();
            Synchronize();
            // A callback can publish another snapshot during this event. Keep
            // the event's original tables/bindings alive until dispatch returns.
            auto eventSnapshot = _snapshot;
            auto eventSettings = _settings;
            auto switchRecent = [this] { if (!_runtime.SwitchRecentSchema()) return false; Synchronize(); return true; };
            auto adjust = [this](CandidateAdjustment op, std::u16string_view code, std::u16string_view text)
                { return _runtime.AdjustCandidate(op, code, text); };
            auto result = _sentence ? _sentence->ProcessKey(event, _session, *eventSnapshot->schema, *eventSnapshot->pinyin, eventSettings,
                Boolean(u"`\u952e\u62fc\u97f3\u53cd\u67e5", true),
                Boolean(u"Ctrl+\u7a7a\u683c\u5207\u6362\u4e2d\u82f1\u6587", true),
                Boolean(u"shift\u5207\u6362\u4e2d\u82f1\u6587", true),
                eventSnapshot->selectionKeys, eventSnapshot->actionBindings, switchRecent, adjust, now, _context)
                : _session.ProcessKey(event, *eventSnapshot->schema, *eventSnapshot->pinyin, eventSettings,
                Boolean(u"`\u952e\u62fc\u97f3\u53cd\u67e5", true),
                Boolean(u"Ctrl+\u7a7a\u683c\u5207\u6362\u4e2d\u82f1\u6587", true),
                Boolean(u"shift\u5207\u6362\u4e2d\u82f1\u6587", true),
                eventSnapshot->selectionKeys, eventSnapshot->actionBindings,
                switchRecent, adjust, now, _context);
            RefreshOutputContext();
            Synchronize();
            return result;
        }
        CandidatePage Page()
        {
            RefreshOutputContext(); Synchronize();
            if (UsesSentenceComposition())
            {
                _sentence->Input().Pump();
                CandidatePage page;
                const auto& candidates = _sentence->Input().Session().Candidates();
                auto count = std::min(candidates.size(), static_cast<std::size_t>(std::clamp(_settings.pageSize, 1, 10)));
                for (std::size_t i = 0; i < count; ++i) page.entries.push_back(candidates[i].text);
                page.total = static_cast<std::uint32_t>(candidates.size());
                page.found = page.total != 0;
                return page;
            }
            return _session.Page(*_snapshot->schema, *_snapshot->pinyin, _settings);
        }
        std::u16string Raw() const
        {
            return UsesSentenceComposition() ? _sentence->Input().ExportUncommittedRaw() : _session.Raw();
        }
        std::size_t SelectedCandidateIndex() const
        {
            return UsesSentenceComposition() ? _sentence->Input().Session().SelectedIndex() : 0;
        }
        const ChineseInputSession& Session() const { return _session; }
        void FocusChanged() { if (_sentence) _sentence->FocusChanged(_session); else _session.OnFocusChanged(); }
        void Cancel() { if (_sentence) _sentence->CancelComposition(_session); else _session.OnExternalCompositionCanceled(); }
        void ImportRaw(std::u16string_view raw)
        {
            // Synchronize may replace the session, so retain aliased input first.
            std::u16string owned(raw);
            RefreshOutputContext(); Synchronize();
            if (UsesSentenceComposition()) _sentence->Input().ImportRaw(owned, _snapshot->generation);
            else _session.ImportRaw(owned, _settings);
        }
    private:
        bool UsesSentenceComposition() const
        {
            return _sentence && _sentence->Snapshot() && _session.IsChinese() &&
                _session.Mode() != ChineseMode::Pinyin && _session.Mode() != ChineseMode::UpperCase;
        }
        void RefreshOutputContext()
        {
            _repeat = _session.PostProcessor().Output().RepeatBuffer();
            _context.repeatBuffer = _repeat;
            _context.dotAfterDigit = _session.PostProcessor().Output().DecimalArmed();
        }
        bool Boolean(std::u16string_view key, bool fallback) const
        {
            for (const auto& [name, value] : _snapshot->config)
                if (name == key) return ParseConfigBool(value, fallback);
            return fallback;
        }
        void Synchronize()
        {
            auto next = _runtime.Read();
            if (!next || !next->schema || !next->pinyin) throw std::logic_error("Runtime tables must be initialized first");
            if (next == _snapshot) return;
            bool sentenceEnabled = GetSentenceEligibility(next->config, next->schemaName).input;
            if (sentenceEnabled && !_sentence)
                throw std::logic_error("Sentence schemas require an explicit n-gram model path");
            bool migrated = _snapshot && (next->root != _snapshot->root || next->schemaName != _snapshot->schemaName);
            auto previousSnapshot = _snapshot;
            auto previousSettings = _settings;
            auto previousVersion = _version;
            auto previousSession = _session;
            try
            {
                _snapshot = std::move(next);
                _version = _version == std::numeric_limits<int>::max() ? 0 : _version + 1;
                _settings = _snapshot->InputSettings(_version);
                if (sentenceEnabled)
                {
                    if (_sentence->Snapshot()) _sentence->Refresh(_snapshot);
                    else if (!_sentence->MigrateFromOrdinary(_session, _snapshot)) _sentence->Refresh(_snapshot);
                }
                else if (_sentence && _sentence->Snapshot())
                {
                    if (_session.Mode() == ChineseMode::Pinyin || _session.Mode() == ChineseMode::UpperCase)
                        _sentence->Leave(); // special composition stays in the common session
                    else _sentence->MigrateToOrdinary(_session, _settings);
                }
                else
                {
                    if (migrated) _session.RefreshAfterSchemaSwitch(_settings);
                    _session.Page(*_snapshot->schema, *_snapshot->pinyin, _settings);
                }
            }
            catch (...)
            {
                _snapshot = std::move(previousSnapshot);
                _settings = std::move(previousSettings);
                _version = previousVersion;
                _session = std::move(previousSession);
                throw;
            }
        }
        RuntimeLexicons& _runtime;
        std::shared_ptr<const RuntimeLexiconSnapshot> _snapshot;
        OrdinarySettings _settings;
        int _version = 0;
        OutputContext _context;
        std::u16string _repeat; // output postprocessing may reallocate the session's repeat buffer
        ChineseInputSession _session;
        std::unique_ptr<RuntimeSentenceState> _sentence; // drains workers before session/table destruction
    };
}
