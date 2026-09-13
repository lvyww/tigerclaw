#include "PinyinComposition.h"
#include "Punctuation.h"
#include "LexiconText.h"
#include <stdexcept>

namespace tiger::core
{
    PostProcessResult PinyinComposition::State(std::u16string output) const
    {
        return {true, std::move(output), _raw, Active()};
    }
    void PinyinComposition::Clear() { _raw.clear(); _page.Reset(); }
    PostProcessResult PinyinComposition::Start() { Clear(); _raw = u"\u00b7"; return State(); }
    PostProcessResult PinyinComposition::Complete(std::u16string output) { Clear(); return State(std::move(output)); }
    CandidatePage PinyinComposition::Page(const CompactLexicon& pinyin, const OrdinarySettings& settings)
    {
        auto code = Active() ? std::u16string_view(_raw).substr(1) : std::u16string_view{};
        auto page = ReadCandidatePage(pinyin, code, 0, settings.pageSize);
        auto index = _page.Resolve(_raw, Active() ? 4 : 1, page.total, settings.pageSize);
        return index == 0 ? std::move(page) : ReadCandidatePage(pinyin, code, index, settings.pageSize);
    }
    PostProcessResult PinyinComposition::KeyDown(int vk, bool shift, bool backQueryEnabled,
        const CompactLexicon& pinyin, const OrdinarySettings& settings,
        const SelectionKeys& selection, OutputState& outputState, SendHistory& history,
        const SymbolCommitter& commitSymbol, const OutputContext& context)
    {
        if (!Active()) throw std::logic_error("Pinyin composition is not active");
        auto page = Page(pinyin, settings);
        auto output = [&](std::u16string_view entry)
        {
            auto text = CandidateCommitText(entry);
            auto converted = NormalizeOutputAction(text, context);
            return converted.action == OutputAction::Text ? std::move(converted.text) : std::u16string(text);
        };
        if (shift)
            if (auto symbol = ResolveShiftChineseSymbol(vk, settings.englishPunctuation))
                return Complete(page.entries.empty() ? std::u16string{} : output(page.entries.front()) + *symbol);
        if (_page.MoveKey(_raw, 4, page.total, settings.pageSize, settings.pageKeys, vk, shift)) return State();
        if (backQueryEnabled && pinyin.Count() > 0 && _raw == u"\u00b7" && vk == 0xc0) return Complete(u"\u00b7");
        if (vk == 32) return Complete(page.entries.empty() ? std::u16string{} : output(page.entries.front()));
        if (!shift && settings.secondSemicolon && vk == 0xba && page.entries.size() >= 2)
            return Complete(output(page.entries[1]));
        if (!shift && settings.thirdQuote && vk == 0xde && page.entries.size() >= 3)
            return Complete(output(page.entries[2]));
        if (!shift)
        {
            auto chosen = selection.Select(vk, page.entries, context);
            if (chosen.recognized) return Complete(std::move(chosen.text));
        }
        // Probe symbol membership without consuming live state when the host
        // forgot to provide its transition handler.
        auto probeState = outputState;
        if (ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, probeState))
        {
            if (!commitSymbol) throw std::logic_error("Pinyin symbol transition handler is required");
            outputState = std::move(probeState);
            auto candidate = page.entries.empty() ? std::u16string{} : page.entries.front();
            Clear();
            return commitSymbol(vk, shift, std::move(candidate));
        }
        if (vk == 0xde)
        {
            if (page.entries.empty()) return Complete();
            auto quote = history.EmitQuote(shift);
            return Complete(output(page.entries.front()) + quote);
        }
        if (vk >= 0x41 && vk <= 0x5a)
        {
            _raw += static_cast<char16_t>(vk + 32); // Shift does not uppercase pinyin.
            return State();
        }
        if (!shift && vk >= 0x30 && vk <= 0x39)
            return Complete((page.entries.empty() ? std::u16string{} : output(page.entries.front())) + static_cast<char16_t>(vk));
        if (vk == 8)
        {
            _raw.pop_back();
            if (_raw.size() <= 1) return Complete();
            return State();
        }
        if (vk == 13) return Complete(settings.enterClear ? std::u16string{} : _raw);
        if (vk == 9)
        {
            if (settings.tabClear) return Complete();
            return {};
        }
        if (vk == 27) return Complete();
        return {};
    }
}
