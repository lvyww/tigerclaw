#pragma once
#include "ConfigParser.h"

namespace tiger::core
{
    struct SentenceEligibility
    {
        bool input = false;
        bool neural = true;
        bool Resident() const { return input && neural; }
    };
    inline SentenceEligibility GetSentenceEligibility(const ConfigValues& config, std::u16string_view schema)
    {
        bool automatic = true, neural = true;
        for (const auto& [key, value] : config)
        {
            if (key == u"\u81ea\u52a8\u542f\u7528\u6574\u53e5\u6a21\u5f0f") // 自动启用整句模式
                automatic = ParseConfigBool(value, true);
            else if (key == u"\u6574\u53e5\u795e\u7ecf\u91cd\u6392") // 整句神经重排
                neural = ParseConfigBool(value, true);
        }
        return {automatic && schema.find(u"\u6574\u53e5") != std::u16string_view::npos, neural}; // 整句
    }
}
