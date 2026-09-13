#include "CandidateSymbolTransition.h"
#include "LexiconText.h"
#include "Punctuation.h"
#include <stdexcept>

namespace tiger::core
{
    PostProcessResult CommitCandidateThenSymbol(int vk, bool shift,
        std::u16string_view candidate, OrdinaryComposition& ordinary,
        PinyinComposition& pinyin, const SchemaLexicon& schema,
        const CompactLexicon& pinyinTable, const OrdinarySettings& settings,
        bool backQueryEnabled, OutputState& outputState, const OutputContext& context,
        std::u16string_view resolvedPrefix, bool mixedSession)
    {
        auto probe = outputState;
        if (!ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, probe))
            throw std::invalid_argument("Candidate transition requires a plain-symbol key");
        // Own the entry before clearing either composition (callers may pass views).
        auto packed = std::u16string(candidate);
        auto prefix = std::u16string(resolvedPrefix);
        ordinary.Clear();
        pinyin.Clear();
        if (!mixedSession && packed.empty() && prefix.empty()) return {true, {}, {}, false};
        auto convert = [&](std::u16string_view text)
        {
            auto result = NormalizeOutputAction(text, context);
            return result.action == OutputAction::Text ? std::move(result.text) : std::u16string(text);
        };
        auto first = prefix + (packed.empty() ? std::u16string{} : convert(CandidateCommitText(packed)));
        PostProcessResult idle;
        if (auto shortSymbol = ordinary.StartShortSymbol(vk, shift, schema, settings, context))
            idle = std::move(*shortSymbol);
        else if (shift && ResolveShiftChineseSymbol(vk, settings.englishPunctuation))
            idle = {true, *ResolveShiftChineseSymbol(vk, settings.englishPunctuation), {}, false};
        else if (!shift && vk == 0xc0 && backQueryEnabled && pinyinTable.Count() > 0)
            idle = pinyin.Start();
        else
            idle = {true, *ResolveChineseSymbol(vk, settings.englishPunctuation, settings.slashIsDunhao, outputState), {}, false};
        auto suffix = idle.text;
        if (!suffix.empty())
            suffix = suffix == u"\u3002" && IsOutputEndingWithDigit(first) ? u"." : convert(suffix);
        idle.text = first + suffix;
        if (!first.empty() && idle.composing && !idle.inputBuffer.empty())
        {
            idle.inputBuffer.clear();
            idle.composing = false;
        }
        return idle;
    }
}
