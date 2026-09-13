#pragma once
#include "MixedInputDecoder.h"
#include "OrdinaryComposition.h"
#include "LexiconText.h"
#include "CandidateSymbolTransition.h"
#include "Punctuation.h"
#include <algorithm>
#include <optional>

namespace tiger::core
{
    // Raw-edit owner. Host owns page navigation and passes the current page's
    // already-converted first output when appending across a segment boundary.
    class MixedComposition
    {
    public:
        explicit MixedComposition(FixedLengthMixedDecoder decoder) : _decoder(std::move(decoder)) {}
        const MixedDecodeResult& Result() const { return _result; }
        const std::u16string& Raw() const { return _raw; }
        bool Active() const { return !_raw.empty(); }
        void Clear() { _raw.clear(); _preferred.clear(); _result = {}; _page.Reset(); }
        void ResetPage() { _page.Reset(); }
        CandidatePage Page(const CompactLexicon& table, const OrdinarySettings& settings)
        {
            auto first = ReadCandidatePage(table, _result.active, 0, settings.pageSize);
            auto index = _page.Resolve(_result.active, Active() ? 2 : 1, first.total, settings.pageSize);
            return index == 0 ? std::move(first) : ReadCandidatePage(table, _result.active, index, settings.pageSize);
        }
        void EnsureCurrent(int maximum, int version)
        {
            if (_decodedMaximum != std::clamp(maximum, 1, 16) || _decodedVersion != version)
                Rebuild(maximum, version);
        }
        PostProcessResult AppendLetter(char16_t code, const CompactLexicon& table,
            const OrdinarySettings& settings, int version, const OutputContext& context = {})
        {
            auto page = Page(table, settings);
            std::optional<std::u16string> preferred;
            if (!page.entries.empty()) preferred = Output(page.entries.front(), context);
            Append(code, settings.maxCodeLength, version, preferred);
            return State();
        }
        // After shifted-punctuation handling, before ordinary symbols/quotes.
        // nullopt requires the outer dispatcher to continue handling the key.
        std::optional<PostProcessResult> EditKey(int vk, bool shift, const CompactLexicon& table,
            const OrdinarySettings& settings, int version, const SelectionKeys& selection,
            const OutputContext& context = {})
        {
            if (!Active()) return {};
            auto page = Page(table, settings);
            if (!shift)
            {
                auto chosen = selection.Select(vk, page.entries, context);
                if (chosen.recognized) return CompleteChinese(chosen.text);
            }
            if (_page.MoveKey(_result.active, 2, page.total, settings.pageSize, settings.pageKeys, vk, shift)) return State();
            if (vk == 8) { Backspace(settings.maxCodeLength, version); return State(); }
            if (vk == 27) { Clear(); return State(); }
            if (vk == 13)
            {
                auto output = settings.enterClear ? std::u16string{} : _raw;
                Clear(); return State(std::move(output));
            }
            if (vk == 9)
            {
                if (settings.tabClear) { Clear(); return State(); }
                return PostProcessResult{};
            }
            if (_result.active == u";" && (vk == 0xba || (vk == 32 && page.entries.empty())))
                return CompleteChinese(u"\uff1b");
            if (_result.active == u"/" && (vk == 0xbf || (vk == 32 && page.entries.empty())))
                return CompleteChinese(settings.slashIsDunhao ? u"\u3001" : u"/");
            if (_result.active == u"[" && (vk == 0xdb || (vk == 32 && page.entries.empty())))
                return CompleteChinese(u"\u3010");
            if (vk == 32) return CompleteChinese(page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context));
            return {};
        }
        PostProcessResult CompleteChinese(std::u16string_view active, std::u16string_view suffix = {})
        {
            auto output = _result.ComposeChinese(active, suffix);
            Clear(); return State(std::move(output));
        }
        // Active mixed mode only; the enclosing engine owns mode entry and
        // postprocessing. Symbols may activate ordinary/pinyin state owners.
        std::optional<PostProcessResult> KeyDown(int vk, bool shift, const SchemaLexicon& schema,
            const CompactLexicon& pinyinTable, const OrdinarySettings& settings, int version,
            const SelectionKeys& selection, OrdinaryComposition& ordinary, PinyinComposition& pinyin,
            bool backQueryEnabled, OutputState& outputState, SendHistory& history,
            const OutputContext& context = {})
        {
            if (!Active()) return {};
            auto page = Page(schema.table, settings);
            auto first = [&] { return page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context); };
            if (shift)
                if (auto symbol = ResolveShiftChineseSymbol(vk, settings.englishPunctuation))
                    return CompleteChinese(first(), *symbol);
            if (auto edit = EditKey(vk, shift, schema.table, settings, version, selection, context)) return edit;
            if (!shift && settings.secondSemicolon && vk == 0xba && page.entries.size() >= 2)
                return CompleteChinese(Output(page.entries[1], context));
            if (!shift && settings.thirdQuote && vk == 0xde && page.entries.size() >= 3)
                return CompleteChinese(Output(page.entries[2], context));
            if (!shift && vk >= 0x30 && vk <= 0x39)
                return CompleteChinese(first(), std::u16string(1, static_cast<char16_t>(vk)));
            if (ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, outputState))
            {
                auto prefix = _result.prefix;
                Clear();
                return CommitCandidateThenSymbol(vk, shift, page.entries.empty() ? std::u16string{} : page.entries.front(),
                    ordinary, pinyin, schema, pinyinTable, settings, backQueryEnabled, outputState, context, prefix, true);
            }
            if (vk == 0xde)
            {
                auto quote = history.EmitQuote(shift); // quote precedes candidate macro conversion
                return CompleteChinese(first(), quote);
            }
            if (vk >= 0x41 && vk <= 0x5a)
                return AppendLetter(static_cast<char16_t>(shift ? vk : vk + 32), schema.table, settings, version, context);
            return PostProcessResult{};
        }
        void Start(char16_t code, int maximum, int version)
        {
            Clear(); _raw += code; Rebuild(maximum, version);
        }
        void Import(std::u16string_view raw, int maximum, int version)
        {
            // Schema migration retains raw spelling but drops old preferences.
            auto owned = std::u16string(raw);
            _decoder.ClearCache(); // schema identity can change without a distinct caller version
            Clear(); _raw = std::move(owned); Rebuild(maximum, version);
        }
        void Append(char16_t code, int maximum, int version,
            const std::optional<std::u16string>& currentPageFirstOutput = {})
        {
            auto length = static_cast<std::size_t>(std::clamp(maximum, 1, 16));
            if (_result.active.size() == length && currentPageFirstOutput)
                _preferred[_raw.size() - _result.active.size()] = *currentPageFirstOutput;
            _raw += code;
            Rebuild(maximum, version);
        }
        void Backspace(int maximum, int version)
        {
            if (!_raw.empty()) _raw.pop_back();
            Rebuild(maximum, version);
        }
        void Rebuild(int maximum, int version)
        {
            maximum = std::clamp(maximum, 1, 16); // engine GetSafeMaxCodeLen
            auto completed = _raw.empty() ? 0 : ((_raw.size() - 1) / maximum) * maximum;
            std::erase_if(_preferred, [&](const auto& item) { return item.first >= completed; });
            _result = _decoder.Decode(_raw, maximum, version, _preferred);
            _decodedMaximum = maximum;
            _decodedVersion = version;
            _page.Reset();
            ++_revision; // host resets the candidate page after every rebuild
        }
        std::size_t Revision() const { return _revision; }
    private:
        static std::u16string Output(std::u16string_view packed, const OutputContext& context)
        {
            auto commit = CandidateCommitText(packed);
            auto converted = NormalizeOutputAction(commit, context);
            return converted.action == OutputAction::Text ? std::move(converted.text) : std::u16string(commit);
        }
        PostProcessResult State(std::u16string output = {}) const
        {
            return {true, std::move(output), _result.active, Active()};
        }
        CandidatePageTracker _page;
        FixedLengthMixedDecoder _decoder;
        std::u16string _raw;
        MixedDecodeResult _result;
        std::unordered_map<std::size_t, std::u16string> _preferred;
        std::size_t _revision = 0;
        int _decodedMaximum = -1, _decodedVersion = -1;
    };
}
