#pragma once
#include "SchemaLexicon.h"
#include "CandidatePage.h"
#include "SelectionKeys.h"
#include "KeyPostProcessor.h"
#include "ConfigParser.h"

namespace tiger::core
{
    struct OrdinarySettings
    {
        int maxCodeLength = 4;
        int pageSize = 5;
        bool maxCodeAutoCommit = true;
        bool clearOnNoCode = true;
        bool enterClear = false;
        bool tabClear = true;
        bool englishPunctuation = false;
        bool slashIsDunhao = true;
        bool secondSemicolon = true;
        bool thirdQuote = true;
        std::u16string pageKeys;
        bool mixedInput = false;
        int lexiconVersion = 0;
    };
    OrdinarySettings LoadOrdinarySettings(const ConfigValues& config, int lexiconVersion = 0,
        std::u16string_view positiveSign = u"+", std::u16string_view negativeSign = u"-");

    // Serialized ordinary-mode composition owner. The outer engine must route
    // shortcuts, modifiers, pinyin,
    // mixed input and sentence modes before calling these operations.
    class OrdinaryComposition
    {
    public:
        const std::u16string& Raw() const { return _raw; }
        bool Active() const { return !_raw.empty(); }
        void Clear();
        void Import(std::u16string_view raw)
        {
            auto owned = std::u16string(raw);
            Clear();
            _raw = std::move(owned);
        }
        void ResetPage() { _page.Reset(); }
        CandidatePage Page(const SchemaLexicon& schema, const OrdinarySettings& settings);
        std::optional<PostProcessResult> StartShortSymbol(int vk, bool shift, const SchemaLexicon& schema,
            const OrdinarySettings& settings, const OutputContext& context = {});
        PostProcessResult AppendLetter(char16_t letter, const SchemaLexicon& schema,
            const OrdinarySettings& settings, const OutputContext& context = {});
        // nullopt means this operation does not implement that key. It is not
        // permission for a host to pass through an unimplemented input branch.
        std::optional<PostProcessResult> EditKey(int vk, bool shift, const SchemaLexicon& schema,
            const OrdinarySettings& settings, const SelectionKeys& selection,
            const OutputContext& context = {});
        std::optional<PostProcessResult> KeyDown(int vk, bool shift, const SchemaLexicon& schema,
            const OrdinarySettings& settings, const SelectionKeys& selection,
            OutputState& outputState, SendHistory& history, const OutputContext& context = {});
    private:
        PostProcessResult State(std::u16string output = {}) const;
        PostProcessResult Complete(std::u16string output = {});
        std::u16string _raw;
        CandidatePageTracker _page;
    };
}
