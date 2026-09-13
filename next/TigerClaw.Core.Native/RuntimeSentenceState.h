#pragma once
#include "RuntimeLexicons.h"
#include "RuntimeSentenceInput.h"
#include "SentenceEligibility.h"
#include "ChineseInputSession.h"

namespace tiger::core
{
    // Serialized sentence-mode runtime owner. The optional service must outlive
    // this owner. Physical mode routing/output bookkeeping remain in the host.
    class RuntimeSentenceState
    {
        struct State
        {
            std::shared_ptr<const RuntimeLexiconSnapshot> snapshot;
            RuntimeSentenceDecoder decoder;
            RuntimeSentenceInput input; // destroyed before its borrowed decoder
            State(std::shared_ptr<const RuntimeLexiconSnapshot> source, const std::filesystem::path& model)
                : snapshot(std::move(source)), decoder(*snapshot->schema, model, LoadSentenceSettings(snapshot->config)),
                  input(decoder, LoadSentenceInputSettings(snapshot->config)) {}
        };
        std::unique_ptr<State> _state;
        std::filesystem::path _model;
        SentenceServiceLifecycle* _service;
        void Deactivate()
        {
            if (_service) _service->SetEnabled(false);
            _state.reset(); // join input/Beam work before releasing its decoder mapping
        }
        void Publish(std::unique_ptr<State> next)
        {
            _state.swap(next);
            next.reset();
            if (_service)
            {
                _service->SetEnabled(GetSentenceEligibility(_state->snapshot->config, _state->snapshot->schemaName).Resident());
                _state->input.AttachNeuralService(*_service);
            }
        }
        std::u16string ToggleLanguage(ChineseInputSession& outer)
        {
            if (!_state || _state->input.Session().Context().UncommittedRaw().empty()) return outer.ToggleChinese();
            auto raw = _state->input.ExportUncommittedRaw();
            auto output = outer.SetChineseWithExternalRaw(!outer.IsChinese(), raw);
            _state->input.Cancel(); // language switching is not a residency change
            return output;
        }
    public:
        RuntimeSentenceState(std::filesystem::path model, SentenceServiceLifecycle* service = nullptr)
            : _model(std::move(model)), _service(service) {}
        void Refresh(std::shared_ptr<const RuntimeLexiconSnapshot> snapshot)
        {
            if (!snapshot || !snapshot->schema || !GetSentenceEligibility(snapshot->config, snapshot->schemaName).input)
                throw std::invalid_argument("An eligible sentence snapshot is required");
            if (_state && _state->snapshot == snapshot) return;
            if (_state && _state->snapshot->schema == snapshot->schema &&
                _state->snapshot->root == snapshot->root && _state->snapshot->schemaName == snapshot->schemaName &&
                LoadSentenceInputSettings(_state->snapshot->config) == LoadSentenceInputSettings(snapshot->config))
            {
                auto before = LoadSentenceSettings(_state->snapshot->config);
                auto after = LoadSentenceSettings(snapshot->config);
                // These are all configurable decoder fields. Remaining Beam,
                // isolation and boundary settings are fixed by the loader.
                if (before.optimalCodeHighFrequencyLimit == after.optimalCodeHighFrequencyLimit &&
                    before.lattice.duplicateSingles == after.lattice.duplicateSingles &&
                    before.fullCodeWhitelist == after.fullCodeWhitelist)
                {
                    if (_service) _service->SetEnabled(GetSentenceEligibility(snapshot->config, snapshot->schemaName).Resident());
                    _state->snapshot = std::move(snapshot);
                    return; // preserve decoder identity, manual selection and committed context
                }
            }
            auto raw = _state ? _state->input.ExportUncommittedRaw() : std::u16string{};
            // Preparation does not attach/cancel the live service or mutate the
            // previous host; file/parse/allocation failure preserves that host.
            auto next = std::make_unique<State>(snapshot, _model);
            next->input.ImportRaw(raw, snapshot->generation);
            Publish(std::move(next));
        }
        // An entry transition, not a refresh of an already active sentence host.
        // Special compositions remain owned by the ordinary dispatcher.
        bool MigrateFromOrdinary(ChineseInputSession& source, std::shared_ptr<const RuntimeLexiconSnapshot> snapshot)
        {
            if (source.Mode() == ChineseMode::Pinyin || source.Mode() == ChineseMode::UpperCase) return false;
            if (_state) throw std::logic_error("Sentence entry requires an inactive target");
            if (!snapshot || !snapshot->schema || !GetSentenceEligibility(snapshot->config, snapshot->schemaName).input)
                throw std::invalid_argument("An eligible sentence snapshot is required");
            auto next = std::make_unique<State>(snapshot, _model);
            next->input.ImportRaw(source.Raw(), snapshot->generation);
            Publish(std::move(next));
            source.ClearComposition();
            return true;
        }
        RuntimeSentenceInput& Input()
        {
            if (!_state) throw std::logic_error("Sentence runtime is not initialized");
            return _state->input;
        }
        // Serialized event entry sharing the ordinary router's physical stages.
        // The caller retains this event's immutable tables and migrates sessions
        // inside switchRecent before returning from that callback.
        PostProcessResult ProcessKey(const InputKeyEvent& event, ChineseInputSession& outer,
            const SchemaLexicon& schema, const CompactLexicon& pinyin, const OrdinarySettings& settings,
            bool backQuery, bool ctrlSpaceEnabled, bool shiftToggleEnabled,
            const SelectionKeys& selection, const ActionBindings& bindings,
            const std::function<bool()>& switchRecent,
            const std::function<bool(CandidateAdjustment, std::u16string_view, std::u16string_view)>& adjust,
            CtrlSpaceState::Time now, OutputContext providers = {})
        {
            auto liveSentence = [&]
            {
                return _state && outer.IsChinese() && outer.Mode() != ChineseMode::Pinyin &&
                    outer.Mode() != ChineseMode::UpperCase && !_state->input.Session().Context().UncommittedRaw().empty();
            };
            InputDispatchExtensions extensions;
            extensions.allowQuoteFallback = !liveSentence();
            extensions.toggleLanguage = [&] { return ToggleLanguage(outer); };
            extensions.modifiedKey = [&] { return TryProcessModifiedKey(event, outer, providers); };
            extensions.editingKey = [&]() -> std::optional<PostProcessResult>
            {
                if (auto result = TryProcessEditingKey(event, outer, providers)) return result;
                // Unsupported keys in an active sentence must pass, not enter
                // ordinary idle processing (notably right-Control and F keys).
                if (liveSentence())
                    return outer.ProcessExternalResult(event,
                        ResolveSelectionVirtualKey(event.vk, event.scan, event.extended), {}, providers);
                return {};
            };
            extensions.actionComposition = [&](PostProcessResult& result)
            {
                if (liveSentence())
                {
                    result.inputBuffer = _state->input.Session().DisplayCode();
                    result.composing = true;
                }
            };
            return outer.ProcessKey(event, schema, pinyin, settings, backQuery, ctrlSpaceEnabled,
                shiftToggleEnabled, selection, bindings, switchRecent, adjust, now, providers, extensions);
        }
        // Keep these as separate stages so modifier selection can remain between
        // Ctrl+Space and Shift in the complete outer event dispatcher.
        std::optional<PostProcessResult> QuoteKey(const InputKeyEvent& event, ChineseInputSession& outer,
            const SchemaLexicon& schema, const CompactLexicon& pinyin, const OrdinarySettings& settings,
            OutputContext providers = {})
        {
            auto action = FoldOrdinalCode(event.action);
            bool down = action == u"DOWN" || action == u"KEY_DOWN";
            bool up = action == u"UP" || action == u"KEY_UP";
            bool sentenceComposing = _state && outer.IsChinese() &&
                outer.Mode() != ChineseMode::Pinyin && outer.Mode() != ChineseMode::UpperCase &&
                !_state->input.Session().Context().UncommittedRaw().empty();
            // Still observe the physical pair and clear deleted-quote arms.
            // C# has no key-up quote fallback for CnSentence; the outer
            // composition's Idle mode must not masquerade as sentence idle.
            return outer.QuoteKey(ResolveSelectionVirtualKey(event.vk, event.scan, event.extended), down, up,
                event.shift, event.ctrl, event.alt, event.win, event.capsLock, schema, pinyin, settings,
                std::move(providers), !sentenceComposing);
        }
        std::optional<PostProcessResult> CtrlSpaceKey(const InputKeyEvent& event, ChineseInputSession& outer,
            bool enabled, CtrlSpaceState::Time now, OutputContext providers = {})
        {
            auto action = FoldOrdinalCode(event.action);
            int vk = ResolveSelectionVirtualKey(event.vk, event.scan, event.extended);
            return outer.CtrlSpaceKey(vk, action == u"DOWN" || action == u"KEY_DOWN", action == u"UP" || action == u"KEY_UP",
                event.repeat, event.shift, event.ctrl, event.alt, event.win, event.capsLock, enabled, now,
                std::move(providers), [&] { return ToggleLanguage(outer); });
        }
        std::optional<PostProcessResult> ShiftKey(const InputKeyEvent& event, ChineseInputSession& outer,
            bool enabled, OutputContext providers = {})
        {
            auto action = FoldOrdinalCode(event.action);
            int vk = ResolveSelectionVirtualKey(event.vk, event.scan, event.extended);
            return outer.ShiftKey(vk, action == u"DOWN" || action == u"KEY_DOWN", action == u"UP" || action == u"KEY_UP",
                event.shift, event.ctrl, event.alt, event.win, event.capsLock, enabled, std::move(providers),
                [&] { return ToggleLanguage(outer); });
        }
        // After dedicated action bindings, before ordinary modifier handling.
        // Sentence candidates never participate in Ctrl/Alt digit adjustments.
        std::optional<PostProcessResult> TryProcessModifiedKey(const InputKeyEvent& event,
            ChineseInputSession& outer, OutputContext providers = {})
        {
            if (!_state || !outer.IsChinese() || outer.Mode() == ChineseMode::Pinyin ||
                outer.Mode() == ChineseMode::UpperCase || (!event.alt && !event.ctrl && !event.win)) return {};
            auto action = FoldOrdinalCode(event.action);
            if (action != u"DOWN" && action != u"KEY_DOWN") return {};
            int vk = ResolveSelectionVirtualKey(event.vk, event.scan, event.extended);
            // Match C#'s Alt, Ctrl, Win branch priority, including mixed masks.
            bool bare = event.alt ? (vk == 0x12 || vk == 0xa4 || vk == 0xa5) : event.ctrl ?
                (vk == 0x11 || vk == 0xa2 || vk == 0xa3) : (vk == 0x5b || vk == 0x5c);
            PostProcessResult result;
            result.cancelCompositionBeforePass = !bare && !_state->input.Session().Context().UncommittedRaw().empty();
            if (result.cancelCompositionBeforePass) _state->input.Cancel();
            return outer.ProcessExternalResult(event, vk, std::move(result), std::move(providers));
        }
        // Invoke only after outer quote/modifier/toggle/shortcut handling. A
        // null result leaves the key untouched for pinyin/punctuation/other
        // routing. Accepted results already include common output processing.
        std::optional<PostProcessResult> TryProcessEditingKey(const InputKeyEvent& event,
            ChineseInputSession& outer, OutputContext providers = {})
        {
            if (!_state || !outer.IsChinese() || event.ctrl || event.alt || event.win ||
                outer.Mode() == ChineseMode::Pinyin || outer.Mode() == ChineseMode::UpperCase) return {};
            auto action = FoldOrdinalCode(event.action);
            if (action != u"DOWN" && action != u"KEY_DOWN") return {};
            int vk = ResolveSelectionVirtualKey(event.vk, event.scan, event.extended);
            if (vk == 0x14 && !_state->input.Session().Context().UncommittedRaw().empty())
            {
                PostProcessResult result; // CapsLock itself passes through, even when committing raw.
                result.text = _state->input.FinishLiteral();
                return outer.ProcessExternalResult(event, vk, std::move(result), std::move(providers));
            }
            if (event.capsLock) return {};
            if (_state->input.Session().Context().UncommittedRaw().empty() && (event.shift || vk < 0x41 || vk > 0x5a)) return {};
            auto control = _state->input.KeyDown(vk, event.shift);
            if (!control.handled)
            {
                auto suffix = outer.ResolveSentenceSuffix(vk, event.shift, _state->snapshot->InputSettings(0));
                if (!suffix) return {};
                control = {true, false, _state->input.FinishWithPunctuation(*suffix)};
            }
            PostProcessResult result;
            result.handled = true;
            if (control.commit) result.text = std::move(*control.commit);
            result.inputBuffer = _state->input.Session().DisplayCode();
            result.composing = !_state->input.Session().Context().UncommittedRaw().empty();
            return outer.ProcessExternalResult(event, vk, std::move(result), std::move(providers));
        }
        std::u16string Leave()
        {
            auto raw = _state ? _state->input.ExportUncommittedRaw() : std::u16string{};
            Deactivate();
            return raw;
        }
        void FocusChanged(ChineseInputSession& outer)
        {
            // C# resets physical chord/page/decimal state, not sentence raw,
            // selected candidate, manual freeze or early-commit context.
            outer.OnFocusChanged();
        }
        void CancelComposition(ChineseInputSession& outer)
        {
            if (_state) _state->input.Cancel();
            outer.OnExternalCompositionCanceled();
            // Neither focus nor external composition cancellation changes
            // schema eligibility or unloads the decoder/neural service.
        }
        // The target's decoder callbacks must already resolve its target table.
        // Prepare target composition first; a failed import leaves both live
        // sessions unchanged and does not release the sentence service.
        void MigrateToOrdinary(ChineseInputSession& target, const OrdinarySettings& settings)
        {
            auto raw = _state ? _state->input.ExportUncommittedRaw() : std::u16string{};
            auto prepared = target;
            prepared.ImportRaw(raw, settings);
            Deactivate();
            target = std::move(prepared);
        }
        std::shared_ptr<const RuntimeLexiconSnapshot> Snapshot() const { return _state ? _state->snapshot : nullptr; }
    };
}
