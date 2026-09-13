#include "OrdinaryComposition.h"
#include "LexiconText.h"
#include <algorithm>

namespace tiger::core
{
    OrdinarySettings LoadOrdinarySettings(const ConfigValues& config, int version,
        std::u16string_view positiveSign, std::u16string_view negativeSign)
    {
        auto get = [&](std::u16string_view key) -> std::u16string_view
        {
            for (auto it = config.rbegin(); it != config.rend(); ++it)
                if (it->first == key) return it->second;
            return {};
        };
        auto boolean = [&](std::u16string_view key, bool fallback) { return ParseConfigBool(get(key), fallback); };
        auto integer = [&](std::u16string_view key, int fallback, int maximum)
        {
            std::int32_t value;
            return ParseIntegerToken(get(key), value, positiveSign, negativeSign) ? std::clamp(static_cast<int>(value), 1, maximum) : fallback;
        };
        OrdinarySettings result;
        result.lexiconVersion = version;
        result.maxCodeLength = integer(u"\u6700\u5927\u7801\u957f", 4, 16); // 最大码长
        result.pageSize = integer(u"\u6bcf\u9875\u5019\u9009\u4e2a\u6570", 5, 10); // 每页候选个数
        result.maxCodeAutoCommit = boolean(u"\u6700\u5927\u7801\u957f\u65e0\u91cd\u81ea\u52a8\u4e0a\u5c4f", true);
        result.clearOnNoCode = boolean(u"\u7a7a\u7801\u81ea\u52a8\u6e05\u5c4f", true);
        result.enterClear = boolean(u"\u56de\u8f66\u6e05\u5c4f", false);
        result.tabClear = boolean(u"TAB\u6e05\u5c4f", true);
        result.englishPunctuation = boolean(u"\u4e2d\u6587\u72b6\u6001\u4e0b\u4f7f\u7528\u82f1\u6587\u6807\u70b9", false);
        result.slashIsDunhao = boolean(u"/\u8f93\u51fa\u987f\u53f7", true);
        result.secondSemicolon = boolean(u"\u5206\u53f7\u6b21\u9009", true);
        result.thirdQuote = boolean(u"\u5f15\u53f7\u4e09\u9009", true);
        result.mixedInput = boolean(u"\u4e2d\u82f1\u6587\u4e0d\u9650\u957f\u6df7\u5408\u8f93\u5165", false);
        auto pageKeys = TrimText(get(u"\u7ffb\u9875\u952e"));
        result.pageKeys = pageKeys.empty() ? u"- =" : std::u16string(pageKeys);
        return result;
    }
}
