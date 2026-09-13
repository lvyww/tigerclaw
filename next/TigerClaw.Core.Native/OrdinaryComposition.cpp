#include "OrdinaryComposition.h"
#include "LexiconText.h"
#include "Punctuation.h"
#include <algorithm>
#include <stdexcept>

namespace tiger::core
{
    namespace
    {
        std::u16string Output(std::u16string_view entry, const OutputContext& context)
        {
            auto commit = CandidateCommitText(entry);
            auto converted = NormalizeOutputAction(commit, context);
            return converted.action == OutputAction::Text ? std::move(converted.text) : std::u16string(commit);
        }
    }
    void OrdinaryComposition::Clear()
    {
        _raw.clear();
        _page.Reset();
    }
    PostProcessResult OrdinaryComposition::State(std::u16string output) const
    {
        return {true, std::move(output), _raw, Active()};
    }
    PostProcessResult OrdinaryComposition::Complete(std::u16string output)
    {
        Clear();
        return State(std::move(output));
    }
    CandidatePage OrdinaryComposition::Page(const SchemaLexicon& schema, const OrdinarySettings& settings)
    {
        auto first = ReadCandidatePage(schema.table, _raw, 0, settings.pageSize);
        auto index = _page.Resolve(_raw, Active() ? 2 : 1, first.total, settings.pageSize);
        return index == 0 ? std::move(first) : ReadCandidatePage(schema.table, _raw, index, settings.pageSize);
    }
    PostProcessResult OrdinaryComposition::AppendLetter(char16_t letter, const SchemaLexicon& schema,
        const OrdinarySettings& settings, const OutputContext& context)
    {
        if (letter < u'a' || letter > u'z') throw std::invalid_argument("Ordinary letter must be lowercase ASCII");
        bool active = Active();
        auto page = Page(schema, settings);
        auto extended = _raw + letter;
        int maximum = std::clamp(settings.maxCodeLength, 1, 16);
        const auto& metadata = schema.metadata;
        auto extendedOutput = [&]
        {
            auto candidates = ReadCandidatePage(schema.table, extended, 0, settings.pageSize);
            return candidates.entries.empty() ? std::u16string{} : Output(candidates.entries.front(), context);
        };
        if (active && (metadata.shortSemicolon || metadata.shortSlash || metadata.shortBracket || metadata.shortZ)
            && metadata.autoShort.Contains(extended)) return Complete(extendedOutput());
        if (settings.maxCodeAutoCommit && ((!active && maximum == 1) ||
            (active && _raw.size() >= static_cast<std::size_t>(maximum - 1))) && metadata.unique.Contains(extended))
            return Complete(extendedOutput());
        auto extendedIndex = schema.table.Find(extended);
        bool hasExtended = extendedIndex && schema.table.CandidateCount(*extendedIndex) > 0;
        if (!active || _raw.size() < static_cast<std::size_t>(maximum) ||
            hasExtended || metadata.nonTerminal.Contains(extended))
        {
            _raw = std::move(extended);
            return State();
        }
        if (settings.clearOnNoCode || !page.entries.empty())
        {
            auto output = page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context);
            _raw.assign(1, letter);
            return State(std::move(output));
        }
        _raw = std::move(extended);
        return State();
    }
    std::optional<PostProcessResult> OrdinaryComposition::StartShortSymbol(int vk, bool shift, const SchemaLexicon& schema,
        const OrdinarySettings& settings, const OutputContext& context)
    {
        if (Active() || shift) return {};
        const auto& meta = schema.metadata;
        char16_t code = vk == 0xba && meta.shortSemicolon ? u';' : vk == 0xbf && meta.shortSlash ? u'/' :
            vk == 0xdb && meta.shortBracket ? u'[' : vk == 0x5a && meta.shortZ ? u'z' : 0;
        if (!code) return {};
        _raw.assign(1, code);
        if (meta.autoShort.Contains(_raw))
        {
            auto page = Page(schema, settings);
            return Complete(page.entries.empty() ? _raw : Output(page.entries.front(), context));
        }
        return State();
    }
    std::optional<PostProcessResult> OrdinaryComposition::EditKey(int vk, bool shift, const SchemaLexicon& schema,
        const OrdinarySettings& settings, const SelectionKeys& selection, const OutputContext& context)
    {
        if (!Active()) return {};
        auto page = Page(schema, settings);
        if (!shift)
        {
            auto chosen = selection.Select(vk, page.entries, context);
            if (chosen.recognized) return Complete(std::move(chosen.text));
        }
        if (schema.metadata.shortBracket && _raw == u"[" && (vk == 0xdb || (vk == 32 && page.total == 0)))
            return Complete(u"\u3010");
        if (_page.MoveKey(_raw, 2, page.total, settings.pageSize, settings.pageKeys, vk, shift)) return State();
        if (vk == 8)
        {
            _raw.pop_back();
            if (!Active()) _page.Reset();
            return State();
        }
        if (vk == 27) return Complete();
        if (vk == 13) return Complete(settings.enterClear ? std::u16string{} : _raw);
        if (vk == 9)
        {
            if (settings.tabClear) return Complete();
            return PostProcessResult{}; // pass, retaining the owned composition
        }
        if ((_raw == u";" && (vk == 0xba || (vk == 32 && page.entries.empty())))) return Complete(u"\uff1b");
        if ((_raw == u"/" && (vk == 0xbf || (vk == 32 && page.entries.empty())))) return Complete(settings.slashIsDunhao ? u"\u3001" : u"/");
        if ((_raw == u"[" && (vk == 0xdb || (vk == 32 && page.entries.empty())))) return Complete(u"\u3010");
        if (vk == 32) return Complete(page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context));
        return {};
    }
    std::optional<PostProcessResult> OrdinaryComposition::KeyDown(int vk, bool shift, const SchemaLexicon& schema,
        const OrdinarySettings& settings, const SelectionKeys& selection,
        OutputState& outputState, SendHistory& history, const OutputContext& context)
    {
        if (!Active())
        {
            if (auto started = StartShortSymbol(vk, shift, schema, settings, context)) return started;
            if (!shift && vk >= 0x41 && vk <= 0x5a)
                return AppendLetter(static_cast<char16_t>(vk + 32), schema, settings, context);
            return {};
        }
        auto page = Page(schema, settings);
        if (shift)
        {
            if (auto symbol = ResolveShiftChineseSymbol(vk, settings.englishPunctuation))
                return Complete(page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context) + *symbol);
        }
        if (auto edited = EditKey(vk, shift, schema, settings, selection, context)) return edited;
        if (!shift && settings.secondSemicolon && vk == 0xba && page.entries.size() >= 2)
            return Complete(Output(page.entries[1], context));
        if (!shift && settings.thirdQuote && vk == 0xde && page.entries.size() >= 3)
            return Complete(Output(page.entries[2], context));
        if (!shift && vk >= 0x30 && vk <= 0x39)
            return Complete((page.entries.empty() ? std::u16string{} : Output(page.entries.front(), context))
                + static_cast<char16_t>(vk));
        // Pinyin re-entry still needs the outer mode dispatcher.
        if (vk == 0xc0) return {};
        if (ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, outputState))
        {
            if (page.entries.empty() || page.entries.front().empty()) return Complete();
            auto first = Output(page.entries.front(), context);
            Clear();
            if (auto started = StartShortSymbol(vk, shift, schema, settings, context))
            {
                started->text = first + Output(started->text, context);
                if (!first.empty() && started->composing && !started->inputBuffer.empty())
                {
                    // Suppress this response's echo, not the retained raw code.
                    started->inputBuffer.clear();
                    started->composing = false;
                }
                return started;
            }
            // The reference first probes the symbol (consuming the decimal
            // arm), then resolves it again through the idle branch after commit.
            auto suffix = *ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, outputState);
            if (suffix == u"\u3002" && IsOutputEndingWithDigit(first)) suffix = u".";
            else suffix = Output(suffix, context);
            return Complete(first + suffix);
        }
        if (vk == 0xde)
        {
            if (page.entries.empty()) return Complete();
            auto quote = history.EmitQuote(shift);
            return Complete(Output(page.entries.front(), context) + quote);
        }
        if (vk >= 0x41 && vk <= 0x5a)
            return AppendLetter(static_cast<char16_t>(vk + 32), schema, settings, context);
        return {};
    }
}
