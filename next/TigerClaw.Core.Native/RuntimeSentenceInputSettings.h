#pragma once
#include "OrdinaryComposition.h"
#include "LexiconText.h"

namespace tiger::core
{
    struct RuntimeSentenceInputSettings
    {
        bool autoCommit = false, enterClear = false, tabClear = true;
        bool semicolonRank = true, quoteRank = true;
        int pageSize = 5;
        std::size_t minimumRetained = 0;
        bool operator==(const RuntimeSentenceInputSettings&) const = default;
    };
    inline RuntimeSentenceInputSettings LoadSentenceInputSettings(const ConfigValues& config,
        std::u16string_view positiveSign = u"+", std::u16string_view negativeSign = u"-")
    {
        auto common = LoadOrdinarySettings(config, 0, positiveSign, negativeSign);
        RuntimeSentenceInputSettings result;
        result.enterClear = common.enterClear; result.tabClear = common.tabClear;
        result.semicolonRank = common.secondSemicolon; result.quoteRank = common.thirdQuote;
        result.pageSize = common.pageSize;
        for (const auto& [key, value] : config)
        {
            if (key == u"\u6574\u53e5\u81ea\u52a8\u63d0\u524d\u4e0a\u5c4f") // 整句自动提前上屏
                result.autoCommit = ParseConfigBool(value, false);
            else if (key == u"\u4fdd\u7559\u6700\u5c11\u7f16\u7801\u6570\u91cf") // 保留最少编码数量
            {
                std::int32_t number;
                result.minimumRetained = ParseIntegerToken(value, number, positiveSign, negativeSign)
                    ? static_cast<std::size_t>(std::clamp(number, 0, 32)) : 0;
            }
        }
        return result;
    }
}
