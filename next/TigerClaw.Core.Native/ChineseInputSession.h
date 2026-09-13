#pragma once
#include "ChineseComposition.h"
#include "CtrlSpaceState.h"
#include "ShiftToggleState.h"
#include "ActionShortcuts.h"
#include "LexiconText.h"
#include "CodeCase.h"
#include "LexiconAssembly.h"
#include "Punctuation.h"
#include <unordered_set>
#include <stdexcept>

namespace tiger::core
{
    struct InputKeyEvent
    {
        int vk = 0, scan = 0, repeat = 1;
        std::u16string action = u"down";
        bool shift = false, ctrl = false, alt = false, win = false;
        bool capsLock = false, numLock = false, extended = false;
    };
    // Optional composition backend hooks; the ordinary router still owns event
    // ordering and physical trackers. Returned hook results are postprocessed.
    struct InputDispatchExtensions
    {
        bool allowQuoteFallback = true;
        std::function<std::u16string()> toggleLanguage;
        std::function<std::optional<PostProcessResult>()> modifiedKey;
        std::function<std::optional<PostProcessResult>()> editingKey;
        std::function<void(PostProcessResult&)> actionComposition;
    };
    // Chinese key-down + output bookkeeping, before physical-key/shortcut host
    // integration. The caller must not postprocess the returned result again.
    class ChineseInputSession
    {
    public:
        explicit ChineseInputSession(UpperCaseServices services) : _composition(std::move(services)) {}
        ChineseInputSession(UpperCaseServices services, FixedLengthMixedDecoder decoder)
            : _composition(std::move(services), std::move(decoder)) {}
        // Unified serialized event entry for ordinary/pinyin/uppercase/English.
        // Caller supplies one immutable table/config view for this event.
        // Runtime mutation callbacks are required only when their actions fire.
        PostProcessResult ProcessKey(const InputKeyEvent& event, const SchemaLexicon& schema,
            const CompactLexicon& pinyin, const OrdinarySettings& settings,
            bool backQuery, bool ctrlSpaceEnabled, bool shiftToggleEnabled,
            const SelectionKeys& selection, const ActionBindings& bindings,
            const std::function<bool()>& switchRecent,
            const std::function<bool(CandidateAdjustment, std::u16string_view, std::u16string_view)>& adjust,
            CtrlSpaceState::Time now, OutputContext providers = {}, const InputDispatchExtensions& extensions = {})
        {
            auto action = FoldOrdinalCode(event.action);
            bool down = action == u"DOWN" || action == u"KEY_DOWN";
            bool up = action == u"UP" || action == u"KEY_UP";
            int vk = ResolveSelectionVirtualKey(event.vk, event.scan, event.extended);
            auto pass = [&]
            {
                PostProcessResult result;
                _post.Process(vk, down, event.shift, event.ctrl, event.alt, event.win, event.capsLock, result, providers);
                return result;
            };
            if (!down && !up) return pass();
            if (up) ActionModifierRelease(vk, event.shift, event.ctrl, event.alt, event.win);
            if (auto result = QuoteKey(vk, down, up, event.shift, event.ctrl, event.alt, event.win, event.capsLock,
                schema, pinyin, settings, providers, extensions.allowQuoteFallback)) return *result;
            if (auto result = CtrlSpaceKey(vk, down, up, event.repeat, event.shift, event.ctrl, event.alt, event.win,
                event.capsLock, ctrlSpaceEnabled, now, providers, extensions.toggleLanguage)) return *result;
            if (auto result = ModifierSelectionKey(vk, down, up, event.shift, event.ctrl, event.alt, event.win,
                event.capsLock, schema, pinyin, settings, selection, providers)) return *result;
            if (auto result = ShiftKey(vk, down, up, event.shift, event.ctrl, event.alt, event.win,
                event.capsLock, shiftToggleEnabled, providers, extensions.toggleLanguage)) return *result;
            if (up) ActionKeyRelease(vk);
            if (!down) return pass();
            if (auto result = ActionKeyDown(vk, event.shift, event.ctrl, event.alt, event.win, event.capsLock,
                bindings, switchRecent, providers, extensions.actionComposition)) return *result;
            if (extensions.modifiedKey)
                if (auto result = extensions.modifiedKey()) return *result;
            if (auto result = ModifiedKeyDown(vk, event.shift, event.ctrl, event.alt, event.win, event.capsLock,
                schema, pinyin, settings, adjust, providers)) return *result;
            if (extensions.editingKey)
                if (auto result = extensions.editingKey()) return *result;
            if (vk == 0x14) return CapsLockKey(event.shift, event.ctrl, event.alt, event.win, event.capsLock, schema, pinyin, providers);
            if (event.capsLock) return pass();
            // Reference idle right-Control handling is distinct from Ctrl held
            // in the modifier mask and does not commit or reset toggle trackers.
            if (_isChinese && Mode() == ChineseMode::Idle && vk == 0xa3) _isChinese = false;
            return KeyDown(vk, event.shift, schema, pinyin, settings, backQuery, selection, providers);
        }
        ChineseMode Mode() const { return _composition.Mode(); }
        bool IsChinese() const { return _isChinese; }
        const std::u16string& Raw() const { return _composition.Raw(); }
        const std::u16string& ActiveCode() const { return _composition.ActiveCode(); }
        std::u16string Surface() const { return _composition.Surface(); }
        const KeyPostProcessor& PostProcessor() const { return _post; }
        // After sentence selectors/letters have declined the key. Quote and
        // decimal state must be shared with the ordinary input path.
        std::optional<std::u16string> ResolveSentenceSuffix(int vk, bool shift, const OrdinarySettings& settings)
        {
            if (vk == 0xde) return _post.EmitQuote(shift);
            if (shift)
                if (auto symbol = ResolveShiftChineseSymbol(vk, settings.englishPunctuation)) return symbol;
            return ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, _post._output);
        }
        // A delegated composition backend returns an unprocessed result. Keep
        // the shared output/history owner here and observe that key exactly once.
        PostProcessResult ProcessExternalResult(const InputKeyEvent& event, int resolvedVk,
            PostProcessResult result, OutputContext providers = {})
        {
            _post.Process(resolvedVk, true, event.shift, event.ctrl, event.alt, event.win, event.capsLock, result, providers);
            return result;
        }
        CandidatePage Page(const SchemaLexicon& schema, const CompactLexicon& pinyin,
            const OrdinarySettings& settings) { return _composition.Page(schema, pinyin, settings); }
        void ClearComposition() { _composition.Clear(); }
        void ImportRaw(std::u16string_view raw, const OrdinarySettings& settings)
        { _composition.ImportRaw(raw, settings); }
        void RefreshAfterSchemaSwitch(const OrdinarySettings& settings)
        {
            _composition.RefreshAfterSchemaSwitch(settings);
        }
        // Physical CapsLock key-down, after modifier-selection and shortcut
        // interception. This key is passed through even when it commits text.
        PostProcessResult CapsLockKey(bool shift, bool ctrl, bool alt, bool win, bool capsLock,
            const SchemaLexicon& schema, const CompactLexicon& pinyin, OutputContext providers = {})
        {
            PostProcessResult result;
            if (!Raw().empty())
            {
                auto raw = ActiveCode();
                auto lookup = Mode() == ChineseMode::Pinyin ? std::u16string_view(raw).substr(1) : std::u16string_view(raw);
                const auto& table = Mode() == ChineseMode::Pinyin ? pinyin : schema.table;
                // ResolveCommitText uses the full table's first candidate,
                // not the selected page. Uppercase also looks in the main table.
                auto first = ReadCandidatePage(table, lookup, 0, 1);
                result.text = raw;
                if (!first.entries.empty() && !first.entries.front().empty())
                {
                    providers.repeatBuffer = _post._output.RepeatBuffer();
                    providers.dotAfterDigit = _post._output.DecimalArmed();
                    auto text = CandidateCommitText(first.entries.front());
                    auto converted = NormalizeOutputAction(text, providers);
                    result.text = converted.action == OutputAction::Text ? std::move(converted.text) : std::u16string(text);
                }
                result.text = _composition.ComposeChinese(result.text);
                _composition.Clear();
                _isChinese = true;
            }
            _post.Process(0x14, true, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        // External language change, like C# SetChinese: returns literal code,
        // records it in history here, but does not update repeat/decimal through
        // a synthetic key event. Physical toggle callers separately postprocess
        // their key response according to the original engine contract.
        std::u16string SetChinese(bool chinese)
        { return SetChineseWithExternalRaw(chinese, _composition.Raw()); }
        // Alternate composition owner supplies its authoritative live raw suffix.
        std::u16string SetChineseWithExternalRaw(bool chinese, std::u16string_view raw)
        {
            if (_isChinese == chinese) return {};
            std::u16string commit;
            if (!chinese && !raw.empty())
            {
                commit = raw;
                _post._history.Append(commit);
            }
            _composition.Clear();
            _isChinese = chinese;
            _post.ClearDecimalArm();
            return commit;
        }
        std::u16string ToggleChinese() { return SetChinese(!_isChinese); }
        // Earliest quote stage, after action validation and before Ctrl+Space.
        // Call for every resolved event to clear deleted-quote correction on
        // unrelated key-downs. Quote fallback does not run selection bindings.
        std::optional<PostProcessResult> QuoteKey(int vk, bool down, bool up,
            bool shift, bool ctrl, bool alt, bool win, bool capsLock,
            const SchemaLexicon& schema, const CompactLexicon& pinyin,
            const OrdinarySettings& settings, OutputContext providers = {}, bool allowFallback = true)
        {
            bool modifier = vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x14 ||
                vk == 0x5b || vk == 0x5c || (vk >= 0xa0 && vk <= 0xa5);
            if (down && vk != 0xde && vk != 8 && !modifier) _post._history.ClearDeletedQuoteArms();
            if (vk != 0xde) return {};
            if (down) { _quoteDownSeen = true; return {}; }
            if (!up) return {};
            bool hadDown = _quoteDownSeen;
            _quoteDownSeen = false;
            if (hadDown || !allowFallback || ctrl || alt || win || capsLock || !_isChinese || Mode() == ChineseMode::UpperCase) return {};
            PostProcessResult result{true, {}, {}, false};
            if (Mode() == ChineseMode::Idle) result.text = _post._history.EmitQuote(shift);
            else
            {
                auto page = Page(schema, pinyin, settings);
                if (!page.entries.empty())
                {
                    auto quote = _post._history.EmitQuote(shift);
                    providers.repeatBuffer = _post._output.RepeatBuffer();
                    providers.dotAfterDigit = _post._output.DecimalArmed();
                    auto text = CandidateCommitText(page.entries.front());
                    auto converted = NormalizeOutputAction(text, providers);
                    result.text = (converted.action == OutputAction::Text ? converted.text : std::u16string(text)) + quote;
                }
                _composition.Clear();
            }
            _post.Process(vk, false, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        // Early physical-key stage. nullopt means continue dispatch without
        // postprocessing yet; a returned response is already postprocessed.
        std::optional<PostProcessResult> CtrlSpaceKey(int vk, bool down, bool up,
            int repeat, bool shift, bool ctrl, bool alt, bool win, bool capsLock,
            bool enabled, CtrlSpaceState::Time now, OutputContext providers = {},
            const std::function<std::u16string()>& toggle = {})
        {
            if (!_ctrlSpace.Process(vk, down, up, repeat, shift, alt, win, enabled, now)) return {};
            PostProcessResult result{true, toggle ? toggle() : ToggleChinese(), {}, false};
            _post.Process(vk, down, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        // Call after modifier-selection handling. Like the C# branch, Shift
        // events return pass unless toggling produced literal text to commit.
        std::optional<PostProcessResult> ShiftKey(int vk, bool down, bool up,
            bool shift, bool ctrl, bool alt, bool win, bool capsLock, bool enabled,
            OutputContext providers = {}, const std::function<std::u16string()>& toggle = {})
        {
            auto decision = _shift.Process(vk, down, up, ctrl, alt, win, enabled);
            if (!decision.intercepted)
            {
                if (down) _shift.ObserveOtherKeyDown(vk);
                return {};
            }
            auto text = decision.toggle ? (toggle ? toggle() : ToggleChinese()) : std::u16string{};
            bool handled = !text.empty();
            PostProcessResult result{handled, std::move(text), {}, false};
            _post.Process(vk, down, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        void ShiftSelectionHandled() { _shift.SkipAfterSelection(); }
        void ShiftSelectionReleased(int vk) { _shift.SelectionReleased(vk); }
        // Priority: CtrlSpaceKey -> modifier selection -> ShiftKey. A selected
        // modifier's matching physical release is consumed even after commit.
        std::optional<PostProcessResult> ModifierSelectionKey(int vk, bool down, bool up,
            bool shift, bool ctrl, bool alt, bool win, bool capsLock,
            const SchemaLexicon& schema, const CompactLexicon& pinyin,
            const OrdinarySettings& settings, const SelectionKeys& selection,
            OutputContext providers = {})
        {
            if (down)
            {
                bool modifier = vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x14 ||
                    vk == 0x5b || vk == 0x5c || (vk >= 0xa0 && vk <= 0xa5);
                if (!modifier || !_isChinese || Raw().empty() ||
                    (Mode() != ChineseMode::Ordinary && Mode() != ChineseMode::Pinyin)) return {};
                providers.repeatBuffer = _post._output.RepeatBuffer();
                providers.dotAfterDigit = _post._output.DecimalArmed();
                auto page = Page(schema, pinyin, settings);
                auto selected = selection.Select(vk, page.entries, providers);
                if (!selected.recognized) return {};
                selected.text = _composition.ComposeChinese(selected.text);
                _composition.Clear();
                _selectedModifiers.insert(vk);
                if (ShiftToggleState::IsShift(vk)) _shift.SkipAfterSelection();
                PostProcessResult result{true, std::move(selected.text), {}, false};
                _post.Process(vk, true, shift, ctrl, alt, win, capsLock, result, providers);
                return result;
            }
            if (up && _selectedModifiers.erase(vk))
            {
                _shift.SelectionReleased(vk);
                PostProcessResult result{true, {}, {}, false};
                _post.Process(vk, false, shift, ctrl, alt, win, capsLock, result, providers);
                return result;
            }
            return {};
        }
        void ResetModifierSelectionState() { _selectedModifiers.clear(); }
        void ActionModifierRelease(int vk, bool shift, bool ctrl, bool alt, bool win)
        {
            _actions.ReleaseModifiers(vk, shift, ctrl, alt, win);
        }
        void ActionKeyRelease(int vk) { _actions.ReleaseKey(vk); }
        // After action shortcuts, before CapsLock. Callback receives packed
        // candidate data and raw code; it owns persistent runtime adjustment.
        std::optional<PostProcessResult> ModifiedKeyDown(int vk, bool shift, bool ctrl,
            bool alt, bool win, bool capsLock, const SchemaLexicon& schema,
            const CompactLexicon& pinyin, const OrdinarySettings& settings,
            const std::function<bool(CandidateAdjustment, std::u16string_view, std::u16string_view)>& adjust,
            OutputContext providers = {})
        {
            if (!alt && !ctrl && !win) return {};
            bool eligible = _isChinese && Mode() == ChineseMode::Ordinary && vk >= 0x31 && vk <= 0x39 &&
                ((alt && !ctrl && !win && !shift) || (!alt && ctrl && !win));
            if (eligible)
            {
                bool handled = _actions.IsHeldAction(vk);
                if (!handled)
                {
                    auto page = Page(schema, pinyin, settings);
                    auto index = static_cast<std::size_t>(vk - 0x31);
                    if (index < page.entries.size())
                    {
                        if (!adjust) throw std::logic_error("Candidate adjustment requires a runtime handler");
                        auto operation = alt ? CandidateAdjustment::Advance : shift ? CandidateAdjustment::Delete : CandidateAdjustment::Top;
                        bool changed = adjust(operation, ActiveCode(), page.entries[index]);
                        // C# Alt+digit consumes even a no-op advance; Ctrl+digit
                        // only consumes a successful top/delete.
                        handled = alt || changed;
                        if (handled) _actions.MarkHandledAction(vk);
                    }
                }
                if (handled)
                {
                    PostProcessResult result{true, {}, ActiveCode(), true};
                    _post.Process(vk, true, shift, ctrl, alt, win, capsLock, result, providers);
                    return result;
                }
            }
            bool bare = alt ? (vk == 0x12 || vk == 0xa4 || vk == 0xa5) : ctrl ?
                (vk == 0x11 || vk == 0xa2 || vk == 0xa3) : (vk == 0x5b || vk == 0x5c);
            PostProcessResult result;
            result.cancelCompositionBeforePass = !bare && _isChinese && !Raw().empty();
            if (result.cancelCompositionBeforePass) _composition.Clear();
            _post.Process(vk, true, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        std::optional<PostProcessResult> ActionKeyDown(int vk, bool shift, bool ctrl,
            bool alt, bool win, bool capsLock, const ActionBindings& bindings,
            const std::function<bool()>& switchRecent, OutputContext providers = {},
            const std::function<void(PostProcessResult&)>& composition = {})
        {
            auto outcome = _actions.KeyDown(vk, shift, ctrl, alt, win,
                bindings.addWord, bindings.recentSchema, switchRecent);
            if (outcome == ShortcutOutcome::None) return {};
            PostProcessResult result{true, {}, {}, false};
            if (outcome == ShortcutOutcome::AddWord) result.action = OutputAction::OpenAddWord;
            else if (outcome != ShortcutOutcome::SuppressedAddRepeat)
            {
                result.inputBuffer = ActiveCode();
                result.composing = _isChinese && !Raw().empty();
                if (composition) composition(result);
            }
            _post.Process(vk, true, shift, ctrl, alt, win, capsLock, result, providers);
            return result;
        }
        // No global/window side effects. Focus alone preserves raw composition;
        // external cancellation clears it. History and quote direction survive.
        void OnFocusChanged()
        {
            _quoteDownSeen = false;
            _actions.Reset();
            ResetLanguageToggleState();
            ResetModifierSelectionState();
            _composition.ResetPage();
            _post.ClearDecimalArm();
        }
        void OnExternalCompositionCanceled() { _composition.Clear(); OnFocusChanged(); }
        bool ResetCompositionForConfigChange()
        {
            bool hadComposition = !Raw().empty();
            _composition.Clear();
            _post.ClearDecimalArm();
            return hadComposition;
        }
        // Host focus/cancellation handlers also reset action and quote state;
        // this method resets only the language-toggle trackers owned here.
        void ResetLanguageToggleState() { _ctrlSpace.Reset(); _shift.Reset(); }
        PostProcessResult KeyDown(int vk, bool shift, const SchemaLexicon& schema,
            const CompactLexicon& pinyin, const OrdinarySettings& settings,
            bool backQueryEnabled, const SelectionKeys& selection, OutputContext providers = {})
        {
            // Both in-branch conversion and final normalization see the same
            // previous commit. Observation happens only after dispatch completes.
            providers.repeatBuffer = _post._output.RepeatBuffer();
            providers.dotAfterDigit = _post._output.DecimalArmed();
            auto result = _isChinese ? _composition.KeyDown(vk, shift, schema, pinyin, settings,
                backQueryEnabled, selection, _post._output, _post._history, providers) : PostProcessResult{};
            _post.Process(vk, true, shift, false, false, false, false, result, providers);
            return result;
        }
    private:
        ChineseComposition _composition;
        KeyPostProcessor _post;
        bool _isChinese = true;
        CtrlSpaceState _ctrlSpace;
        ShiftToggleState _shift;
        std::unordered_set<int> _selectedModifiers;
        ActionShortcuts _actions;
        bool _quoteDownSeen = false;
    };
}
