#pragma once
#include "PinyinComposition.h"

namespace tiger::core
{
    // Caller has already probed plain-symbol membership (and consumed the
    // decimal arm). Mixed callers supply the resolved prefix and session flag.
    PostProcessResult CommitCandidateThenSymbol(int vk, bool shift,
        std::u16string_view candidate, OrdinaryComposition& ordinary,
        PinyinComposition& pinyin, const SchemaLexicon& schema,
        const CompactLexicon& pinyinTable, const OrdinarySettings& settings,
        bool backQueryEnabled, OutputState& outputState,
        const OutputContext& context = {}, std::u16string_view resolvedPrefix = {},
        bool mixedSession = false);
}
