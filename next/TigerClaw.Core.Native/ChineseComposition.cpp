#include "ChineseComposition.h"
#include "CandidateSymbolTransition.h"
#include "Punctuation.h"
#include <stdexcept>

namespace tiger::core
{
    ChineseMode ChineseComposition::Mode() const
    {
        if (_pinyin.Active()) return ChineseMode::Pinyin;
        if (_upper.Active()) return ChineseMode::UpperCase;
        return _ordinary.Active() || (_mixed && _mixed->Active()) ? ChineseMode::Ordinary : ChineseMode::Idle;
    }
    const std::u16string& ChineseComposition::Raw() const
    {
        if (_pinyin.Active()) return _pinyin.Raw();
        if (_upper.Active()) return _upper.Raw();
        if (_mixed && _mixed->Active()) return _mixed->Raw();
        return _ordinary.Raw();
    }
    const std::u16string& ChineseComposition::ActiveCode() const
    {
        return _mixed && _mixed->Active() ? _mixed->Result().active : Raw();
    }
    std::u16string ChineseComposition::Surface() const
    {
        return _mixed && _mixed->Active() ? _mixed->Result().surface : Raw();
    }
    void ChineseComposition::Clear() { _ordinary.Clear(); _pinyin.Clear(); _upper.Clear(); if (_mixed) _mixed->Clear(); }
    void ChineseComposition::RefreshAfterSchemaSwitch(const OrdinarySettings& settings)
    {
        if (Mode() != ChineseMode::Ordinary) { ResetPage(); return; }
        ImportRaw(Raw(), settings);
    }
    void ChineseComposition::ImportRaw(std::u16string_view raw, const OrdinarySettings& settings)
    {
        bool mixed = settings.mixedInput || raw.size() > static_cast<std::size_t>(std::clamp(settings.maxCodeLength, 1, 16));
        if (!raw.empty() && mixed && !_mixed) throw std::logic_error("Schema migration requires a runtime-bound mixed decoder");
        // Prepare separately: raw may alias our storage and decoding can throw.
        auto next = *this;
        next.Clear();
        if (!raw.empty())
        {
            if (mixed) next._mixed->Import(std::u16string(raw), settings.maxCodeLength, settings.lexiconVersion);
            else next._ordinary.Import(std::u16string(raw));
        }
        next.ResetPage();
        *this = std::move(next);
    }
    CandidatePage ChineseComposition::Page(const SchemaLexicon& schema,
        const CompactLexicon& pinyin, const OrdinarySettings& settings)
    {
        if (_pinyin.Active()) return _pinyin.Page(pinyin, settings);
        if (_mixed && _mixed->Active())
        {
            _mixed->EnsureCurrent(settings.maxCodeLength, settings.lexiconVersion);
            return _mixed->Page(schema.table, settings);
        }
        if (_ordinary.Active()) return _ordinary.Page(schema, settings);
        return {};
    }
    PostProcessResult ChineseComposition::KeyDown(int vk, bool shift,
        const SchemaLexicon& schema, const CompactLexicon& pinyin,
        const OrdinarySettings& settings, bool backQueryEnabled,
        const SelectionKeys& selection, OutputState& output, SendHistory& history,
        const OutputContext& context)
    {
        auto transition = [&](int key, bool shifted, std::u16string candidate)
        {
            return CommitCandidateThenSymbol(key, shifted, candidate, _ordinary,
                _pinyin, schema, pinyin, settings, backQueryEnabled, output, context);
        };
        if (_pinyin.Active())
            return _pinyin.KeyDown(vk, shift, backQueryEnabled, pinyin, settings,
                selection, output, history, transition, context);
        if (_upper.Active()) return _upper.KeyDown(vk, shift, settings.tabClear);
        if (_mixed && _mixed->Active())
        {
            _mixed->EnsureCurrent(settings.maxCodeLength, settings.lexiconVersion);
            return *_mixed->KeyDown(vk, shift, schema, pinyin, settings, settings.lexiconVersion,
                selection, _ordinary, _pinyin, backQueryEnabled, output, history, context);
        }
        if (_ordinary.Active())
        {
            // Let existing selection/paging/Shift priority run before backquery.
            if (auto result = _ordinary.KeyDown(vk, shift, schema, settings, selection, output, history, context))
                return std::move(*result);
            if (vk == 0xc0)
            {
                auto page = _ordinary.Page(schema, settings);
                ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, output);
                return transition(vk, shift, page.entries.empty() ? std::u16string{} : page.entries.front());
            }
            return {};
        }
        if (vk == 32) return {};
        if (auto started = _ordinary.StartShortSymbol(vk, shift, schema, settings, context)) return std::move(*started);
        if (shift)
            if (auto symbol = ResolveShiftChineseSymbol(vk, settings.englishPunctuation))
                return {true, std::move(*symbol), {}, false};
        if (!shift && vk == 0xc0 && backQueryEnabled && pinyin.Count() > 0) return _pinyin.Start();
        if (auto symbol = ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, output))
            return {true, std::move(*symbol), {}, false};
        if (vk == 0xde) return {true, history.EmitQuote(shift), {}, false};
        if (vk >= 0x41 && vk <= 0x5a)
        {
            if (shift) return _upper.Start(static_cast<char16_t>(vk));
            if (settings.mixedInput)
            {
                if (!_mixed) throw std::logic_error("Mixed input requires a decoder bound to the runtime lexicon and output services");
                _mixed->Start(static_cast<char16_t>(vk + 32), settings.maxCodeLength, settings.lexiconVersion);
                return {true, {}, _mixed->Result().active, true};
            }
            return _ordinary.AppendLetter(static_cast<char16_t>(vk + 32), schema, settings, context);
        }
        // The newline is pass-through bookkeeping, not an injected commit.
        if (vk == 13) return {false, u"\n", {}, false};
        return {};
    }
}
