#pragma once
#include "KeyPostProcessor.h"
#include <functional>

namespace tiger::core
{
    bool IsUpperCaseNumericPrefix(std::u16string_view code);
    enum class UpperCaseCommand { Literal, Timer, Currency };
    UpperCaseCommand ClassifyUpperCaseCommand(std::u16string_view code);
    // Entry point for regex-admitted uppercase S commands, not a general
    // localized currency parser.
    std::u16string ConvertUpperCaseCurrency(std::u16string_view code);
    struct UpperCaseServices
    {
        // The host must implement timer/currency commit. No literal fallback.
        std::function<std::u16string(std::u16string_view)> commit;
        std::function<bool(std::u16string_view)> allowsNumericSeparator = IsUpperCaseNumericPrefix;
    };
    UpperCaseServices MakeUpperCaseServices(
        std::function<std::u16string(std::u16string_view)> currency,
        std::function<void(std::u16string_view)> scheduleTimer);
    UpperCaseServices MakeUpperCaseServices(std::function<void(std::u16string_view)> scheduleTimer);
    class UpperCaseComposition
    {
    public:
        explicit UpperCaseComposition(UpperCaseServices services);
        PostProcessResult Start(char16_t uppercaseLetter);
        PostProcessResult KeyDown(int vk, bool shift, bool tabClear);
        void Clear() { _raw.clear(); }
        bool Active() const { return !_raw.empty(); }
        const std::u16string& Raw() const { return _raw; }
    private:
        PostProcessResult State(std::u16string output = {}) const;
        PostProcessResult Complete(std::u16string_view suffix = {});
        UpperCaseServices _services;
        std::u16string _raw;
    };
}
