#pragma once
#include "OrdinaryComposition.h"

namespace tiger::core
{
    class PinyinComposition
    {
    public:
        using SymbolCommitter = std::function<PostProcessResult(int, bool, std::u16string)>;
        PostProcessResult Start();
        void Clear();
        void ResetPage() { _page.Reset(); }
        bool Active() const { return !_raw.empty(); }
        const std::u16string& Raw() const { return _raw; }
        CandidatePage Page(const CompactLexicon& pinyin, const OrdinarySettings& settings);
        // Host callback applies the shared candidate-then-idle-symbol path,
        // including transitions into ordinary short-symbol or pinyin modes.
        PostProcessResult KeyDown(int vk, bool shift, bool backQueryEnabled,
            const CompactLexicon& pinyin, const OrdinarySettings& settings,
            const SelectionKeys& selection, OutputState& outputState, SendHistory& history,
            const SymbolCommitter& commitSymbol, const OutputContext& context = {});
    private:
        PostProcessResult State(std::u16string output = {}) const;
        PostProcessResult Complete(std::u16string output = {});
        std::u16string _raw;
        CandidatePageTracker _page;
    };
}
