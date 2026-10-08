#pragma once
#include "RuntimeLexicons.h"
#include "ChineseInputSession.h"
#include "SentenceEligibility.h"
#include "RuntimeSentenceState.h"
#include "RuntimeInputSnapshot.h"
#include <limits>

namespace tiger::core
{
    // Serialized, offline event host. The caller owns RuntimeLexicons and must
    // serialize its mutations with this host. No pipe, UI or process startup.
    class RuntimeInput
    {
    public:
        RuntimeInput(RuntimeLexicons& runtime, UpperCaseServices upper,
            std::filesystem::path sentenceModel = {}, SentenceServiceLifecycle* service = nullptr, bool allowNoModel = false)
            : _runtime(runtime), _allowNoModel(allowNoModel), _modelPath(std::move(sentenceModel)), _service(service), _session(std::move(upper), FixedLengthMixedDecoder(
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
            if (!_allowNoModel && !_modelPath.empty()) _sentence = std::make_unique<RuntimeSentenceState>(_modelPath, service);
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
            auto switchRecent = [this] { return SwitchRecentSchema(); };
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
        // Frontends issue a token only for the exact output returned by Process,
        // and acknowledge it after their document edit. Replay must reuse the
        // original response/token. Failure and duplicate receipts cannot learn.
        std::string IssueLearningReceipt(std::u16string client, const PostProcessResult& result)
        {
            if (!_sentence || !UsesSentenceComposition()) return {};
            auto& input = _sentence->Input();
            return _learningReceipts.Issue(std::move(client), input.LearningStore(), input.TakeLearning(result.text));
        }
        bool AcknowledgeLearningReceipt(std::u16string_view client, const std::string& token, bool success)
        {
            Synchronize();
            if (!Boolean(u"整句Tab自学习", true)) { _learningReceipts.Cancel(); return false; }
            return _learningReceipts.Acknowledge(client, token, success);
        }
        // Test/controlled shutdown only; never called by key dispatch.
        bool WaitLearningIdle(std::chrono::milliseconds timeout)
        {
            if (!_sentence || !_sentence->Snapshot()) return true;
            auto store = _sentence->Input().LearningStore();
            return !store || store->WaitIdle(timeout);
        }
        CandidatePage Page()
        {
            RefreshOutputContext(); Synchronize();
            return ReadPage();
        }
        RuntimeInputSnapshot CaptureSnapshot()
        {
            RefreshOutputContext(); Synchronize();
            RuntimeInputSnapshot result;
            result.page = ReadPage(); // the only Pump; copy all remaining fields afterwards
            result.lexiconGeneration = _snapshot->generation;
            result.schemaName = _snapshot->schemaName;
            result.pageSize = std::clamp(_settings.pageSize, 1, 10);
            result.isChinese = _session.IsChinese();
            result.raw = Raw();
            result.composing = !result.raw.empty();
            result.selectedCandidateIndex = SelectedCandidateIndex();
            if (UsesSentenceComposition())
            {
                const auto& input = _sentence->Input();
                const auto& session = input.Session();
                result.mode = RuntimeInputMode::Sentence;
                result.sentenceGeneration = session.Request().generation;
                result.activeCode = result.raw;
                result.displayCode = session.DisplayCode();
                result.candidatesCurrent = !result.composing || session.DecodeCurrent();
                result.decodeFailed = input.LastDecodeError() != nullptr;
                result.neuralFailed = input.LastNeuralError() != nullptr;
                result.decodePending = !result.candidatesCurrent && !result.decodeFailed;
            }
            else
            {
                result.activeCode = _session.ActiveCode();
                result.displayCode = _session.Surface();
                if (!result.isChinese) result.mode = RuntimeInputMode::English;
                else switch (_session.Mode())
                {
                case ChineseMode::Idle: result.mode = RuntimeInputMode::Idle; break;
                case ChineseMode::Ordinary: result.mode = RuntimeInputMode::Ordinary; break;
                case ChineseMode::Pinyin: result.mode = RuntimeInputMode::Pinyin; break;
                case ChineseMode::UpperCase: result.mode = RuntimeInputMode::UpperCase; break;
                }
            }
            return result;
        }
        // Host callers use these entry points instead of publishing a table
        // switch first: model/migration failures leave the runtime unchanged.
        void Reload(const std::filesystem::path& configFile)
        { _runtime.Reload(configFile, [this](auto next) { AcceptSnapshot(std::move(next)); }, true); }
        void ReloadTables()
        { _runtime.ReloadTables([this](auto next) { AcceptSnapshot(std::move(next)); }); }
        bool SetConfig(std::u16string_view key, std::u16string_view value, bool& changed, std::u16string& error)
        { return _runtime.SetConfig(key, value, changed, error, [this](auto next) { AcceptSnapshot(std::move(next)); }); }
        std::u16string ToggleLanguage()
        { Synchronize(); return _sentence ? _sentence->ToggleLanguage(_session) : _session.ToggleChinese(); }
        void ApplyDefaultLanguage() { _session.SetChinese(Boolean(u"默认中文", true)); }
        void CancelLearningReceipts() { _learningReceipts.Cancel(); }
        bool ExpectKeyUp(const InputKeyEvent& event) const
        { return _session.ExpectKeyUp(event, Boolean(u"Ctrl+空格切换中英文", true)); }
        bool SwitchSchema(std::u16string_view name)
        {
            RefreshOutputContext(); Synchronize();
            return _runtime.SwitchSchema(name, [this](auto next) { AcceptSnapshot(std::move(next)); });
        }
        bool SwitchRecentSchema()
        {
            RefreshOutputContext(); Synchronize();
            return _runtime.SwitchRecentSchema([this](auto next) { AcceptSnapshot(std::move(next)); });
        }
    private:
        CandidatePage ReadPage()
        {
            if (UsesSentenceComposition())
            {
                _sentence->Input().Pump();
                CandidatePage page;
                const auto candidates = _sentence->Input().Session().PublishedCandidates();
                auto count = std::min(candidates.size(), static_cast<std::size_t>(std::clamp(_settings.pageSize, 1, 10)));
                for (std::size_t i = 0; i < count; ++i) page.entries.push_back(candidates[i]);
                page.total = static_cast<std::uint32_t>(candidates.size());
                page.found = page.total != 0;
                return page;
            }
            return _session.Page(*_snapshot->schema, *_snapshot->pinyin, _settings);
        }
    public:
        std::u16string Raw() const
        {
            return UsesSentenceComposition() ? _sentence->Input().ExportUncommittedRaw() : _session.Raw();
        }
        std::size_t SelectedCandidateIndex() const
        {
            return UsesSentenceComposition() ? _sentence->Input().Session().SelectedIndex() : 0;
        }
        const ChineseInputSession& Session() const { return _session; }
        std::u16string CandidateDisplay(std::u16string_view candidate)
        {
            if (CandidateCommitText(candidate)!=candidate) return std::u16string(CandidateDisplayText(candidate));
            RefreshOutputContext();
            auto value=NormalizeOutputAction(candidate,_context);
            return value.action==OutputAction::Text ? std::move(value.text) : std::u16string(candidate);
        }
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
            AcceptSnapshot(_runtime.Read());
        }
        void AcceptSnapshot(std::shared_ptr<const RuntimeLexiconSnapshot> next)
        {
            if (!next || !next->schema || !next->pinyin) throw std::logic_error("Runtime tables must be initialized first");
            if (next == _snapshot) return;
            bool adoptedModel = false;
            if (_allowNoModel && !_sentence && !_modelPath.empty())
            {
                // Retry when a new runtime snapshot arrives, never on every
                // key. A repaired/added model can reactivate sentence input.
                try
                {
                    SentenceFivegramModel checked(_modelPath);
                    _sentence=std::make_unique<RuntimeSentenceState>(_modelPath,_service);
                    adoptedModel=true;
                }
                catch (const std::exception&) {} // ordinary fallback until resources become available
            }
            bool sentenceEnabled = GetSentenceEligibility(next->config, next->schemaName).input;
            if (sentenceEnabled && !_sentence && !_allowNoModel)
                throw std::logic_error("Sentence schemas require an explicit n-gram model path");
            sentenceEnabled = sentenceEnabled && static_cast<bool>(_sentence);
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
                if (adoptedModel) _sentence.reset();
                throw;
            }
            _learningReceipts.Cancel(); // successful configuration/schema publication invalidates old capabilities
        }
        SentenceLearningReceipts _learningReceipts;
        RuntimeLexicons& _runtime;
        bool _allowNoModel;
        std::filesystem::path _modelPath;
        SentenceServiceLifecycle* _service;
        std::shared_ptr<const RuntimeLexiconSnapshot> _snapshot;
        OrdinarySettings _settings;
        int _version = 0;
        OutputContext _context;
        std::u16string _repeat; // output postprocessing may reallocate the session's repeat buffer
        ChineseInputSession _session;
        std::unique_ptr<RuntimeSentenceState> _sentence; // drains workers before session/table destruction
    };
}
