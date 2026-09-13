#include "TextElements.h"
#include "DotNetGraphemeTable.h"
#include <algorithm>
#include <iterator>

namespace tiger::core
{
    std::vector<std::size_t> TextElementStarts(std::u16string_view text)
    {
        using G = GraphemeType;
        auto property = [](std::uint32_t scalar)
        {
            auto item = std::lower_bound(std::begin(GraphemeRanges), std::end(GraphemeRanges), scalar,
                [](const GraphemeRange& range, std::uint32_t value) { return range.last < value; });
            return item != std::end(GraphemeRanges) && scalar >= item->first ? item->type : G::Other;
        };
        auto control = [](G type) { return type == G::Control || type == G::CR || type == G::LF; };
        std::vector<std::size_t> starts;
        G previous = G::Other;
        std::size_t regionalCount = 0;
        bool pictographExtend = false, zwjAfterPictograph = false;
        for (std::size_t i = 0; i < text.size();)
        {
            auto offset = i;
            std::uint32_t scalar = text[i++];
            if (scalar >= 0xd800 && scalar <= 0xdbff && i < text.size() && text[i] >= 0xdc00 && text[i] <= 0xdfff)
                scalar = 0x10000 + ((scalar - 0xd800) << 10) + (text[i++] - 0xdc00);
            auto current = property(scalar);
            bool join = false;
            if (offset != 0)
            {
                if (previous == G::CR && current == G::LF) join = true;
                else if (control(previous) || control(current)) join = false;
                else if (previous == G::L && (current == G::L || current == G::V || current == G::LV || current == G::LVT)) join = true;
                else if ((previous == G::LV || previous == G::V) && (current == G::V || current == G::T)) join = true;
                else if ((previous == G::LVT || previous == G::T) && current == G::T) join = true;
                else if (current == G::Extend || current == G::ZWJ || current == G::SpacingMark || previous == G::Prepend) join = true;
                else if (current == G::Extended_Pictograph && previous == G::ZWJ && zwjAfterPictograph) join = true;
                else if (previous == G::Regional_Indicator && current == G::Regional_Indicator && regionalCount % 2 == 1) join = true;
            }
            if (!join) starts.push_back(offset);
            bool nextZwj = current == G::ZWJ && pictographExtend;
            if (current != G::Extend) pictographExtend = current == G::Extended_Pictograph;
            zwjAfterPictograph = nextZwj;
            regionalCount = current == G::Regional_Indicator ? regionalCount + 1 : 0;
            previous = current;
        }
        return starts;
    }
}
