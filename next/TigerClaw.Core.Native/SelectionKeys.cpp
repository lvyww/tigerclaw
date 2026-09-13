#include "SelectionKeys.h"
#include "LexiconText.h"
namespace tiger::core
{
    SelectionBindings DefaultSelectionBindings()
    {
        SelectionBindings result;
        for (int number = 1; number <= 10; ++number) result[number] = {0x30 + number % 10};
        return result;
    }
    int ResolveSelectionVirtualKey(int vk, int scan, bool extended)
    {
        if (vk == 0x10) { if (scan == 0x2a) return 0xa0; if (scan == 0x36) return 0xa1; }
        else if (vk == 0x11) return extended ? 0xa3 : 0xa2;
        else if (vk == 0x12) return extended ? 0xa5 : 0xa4;
        return vk;
    }
    SelectionKeys::SelectionKeys(const SelectionBindings& bindings)
    {
        for (int number = 1; number <= 10; ++number)
        {
            auto found = bindings.find(number);
            if (found == bindings.end()) continue;
            for (int vk : found->second) _numbers.try_emplace(vk, number);
        }
    }
    std::optional<int> SelectionKeys::Number(int vk) const
    {
        auto found = _numbers.find(vk);
        if (found != _numbers.end()) return found->second;
        int generic;
        if (vk == 0xa0 || vk == 0xa1) generic = 0x10;
        else if (vk == 0xa2 || vk == 0xa3) generic = 0x11;
        else if (vk == 0xa4 || vk == 0xa5) generic = 0x12;
        else if (vk == 0x5c) generic = 0x5b;
        else return std::nullopt;
        found = _numbers.find(generic);
        return found == _numbers.end() ? std::nullopt : std::optional<int>(found->second);
    }
    SelectionResult SelectionKeys::Select(int vk, std::span<const std::u16string> page, const OutputContext& context) const
    {
        auto convert = [&](std::u16string_view entry)
        {
            auto commit = CandidateCommitText(entry);
            auto result = NormalizeOutputAction(commit, context);
            // ConvertOutputText expands text macros here, but add/hide actions
            // are interpreted only after CompleteCnComposition/postprocessing.
            return result.action == OutputAction::Text ? std::move(result.text) : std::u16string(commit);
        };
        auto number = Number(vk);
        if (!number) return {};
        if (page.empty()) return {true, {}};
        if (static_cast<std::size_t>(*number) <= page.size()) return {true, convert(page[*number - 1])};
        bool digit = (vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69);
        if (!digit) return {true, {}};
        auto output = convert(page.front());
        output += static_cast<char16_t>(u'0' + *number % 10);
        return {true, std::move(output)};
    }
}
