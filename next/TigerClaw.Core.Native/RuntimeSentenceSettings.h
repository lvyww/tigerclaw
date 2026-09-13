#pragma once
#include "SentenceLattice.h"
#include "SentenceIsolation.h"
#include "ConfigParser.h"
#include "ConfigDefaults.h"
#include "LexiconText.h"

namespace tiger::core
{
    struct RuntimeSentenceSettings
    {
        SentenceLatticeSettings lattice;
        SentenceIsolationSettings isolation;
        int optimalCodeHighFrequencyLimit = 1500;
        SentenceLexicon::CharacterSet fullCodeWhitelist;
        bool scoreSentenceBoundaries = true;
    };
    inline RuntimeSentenceSettings LoadSentenceSettings(const ConfigValues& config)
    {
        constexpr auto optimal = u"\u9ad8\u9891\u5b57\u4ec5\u4f7f\u7528\u6700\u4f18\u7801\u7ec4\u53e5";
        constexpr auto whitelist = u"\u6574\u53e5\u5141\u8bb8\u5168\u7801\u7ec4\u53e5\u767d\u540d\u5355";
        constexpr auto duplicate = u"\u5141\u8bb8\u5355\u5b57\u91cd\u7801\u7ec4\u53e5";
        RuntimeSentenceSettings result;
        std::u16string_view whiteText;
        for (const auto& [key, value] : ConfigDefaults) if (key == whitelist) whiteText = value;
        for (const auto& [key, value] : config)
        {
            if (key == optimal)
            {
                std::int32_t number;
                // C# explicitly uses invariant signs here, unlike retained raw.
                result.optimalCodeHighFrequencyLimit = ParseIntegerToken(TrimText(value), number) && number >= 0 ? number : 0;
            }
            else if (key == whitelist) whiteText = value;
            else if (key == duplicate) result.lattice.duplicateSingles = ParseConfigBool(value, true);
        }
        whiteText = TrimText(whiteText);
        auto starts = TextElementStarts(whiteText);
        for (std::size_t i = 0; i < starts.size(); ++i)
        {
            auto end = i + 1 < starts.size() ? starts[i + 1] : whiteText.size();
            auto element = whiteText.substr(starts[i], end - starts[i]);
            if (!std::all_of(element.begin(), element.end(), IsDotNetWhiteSpace))
                result.fullCodeWhitelist.emplace(element);
        }
        return result;
    }
}
