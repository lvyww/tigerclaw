#pragma once
#include "PinyinComposition.h"
#include "UpperCaseComposition.h"
#include "MixedComposition.h"

namespace tiger::core
{
    enum class ChineseMode { Idle = 1, Ordinary = 2, UpperCase = 3, Pinyin = 4 };
    // Serialized Chinese key-down dispatcher. The outer engine must route
    // language/modifier shortcuts and sentence sessions before this entry.
    // Mixed mode requires the overload with runtime-bound decoder callbacks.
    // Output/history observation still occurs exactly once in the outer engine.
    class ChineseComposition
    {
    public:
        explicit ChineseComposition(UpperCaseServices services) : _upper(std::move(services)) {}
        ChineseComposition(UpperCaseServices services, FixedLengthMixedDecoder decoder)
            : _upper(std::move(services)), _mixed(std::move(decoder)) {}
        ChineseMode Mode() const;
        const std::u16string& Raw() const;
        const std::u16string& ActiveCode() const;
        std::u16string Surface() const;
        std::u16string ComposeChinese(std::u16string_view active) const
        {
            return _mixed && _mixed->Active() ? _mixed->Result().ComposeChinese(active) : std::u16string(active);
        }
        void Clear();
        // Non-sentence target only. Invoke after publishing the target runtime
        // snapshot so the injected decoder resolves that target's candidates.
        void RefreshAfterSchemaSwitch(const OrdinarySettings& settings);
        void ImportRaw(std::u16string_view raw, const OrdinarySettings& settings);
        void ResetPage() { _ordinary.ResetPage(); _pinyin.ResetPage(); if (_mixed) _mixed->ResetPage(); }
        CandidatePage Page(const SchemaLexicon& schema, const CompactLexicon& pinyin,
            const OrdinarySettings& settings);
        PostProcessResult KeyDown(int vk, bool shift, const SchemaLexicon& schema,
            const CompactLexicon& pinyin, const OrdinarySettings& settings,
            bool backQueryEnabled, const SelectionKeys& selection,
            OutputState& output, SendHistory& history, const OutputContext& context = {});
    private:
        OrdinaryComposition _ordinary;
        PinyinComposition _pinyin;
        UpperCaseComposition _upper;
        std::optional<MixedComposition> _mixed;
    };
}
