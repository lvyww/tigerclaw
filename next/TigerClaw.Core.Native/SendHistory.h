#pragma once
#include <string>
#include <string_view>
#include <vector>
#include <optional>
namespace tiger::core
{
    std::optional<std::u16string> GuessPassThroughText(int vk, bool shift, bool capsLock);
    class SendHistory
    {
    public:
        void Append(std::u16string_view text);
        void AppendPassThrough(int vk, bool shift, bool ctrl, bool alt, bool win, bool capsLock);
        // Only invoke for a passed-through Backspace keydown.
        void Backspace();
        void ClearDeletedQuoteArms() { _deletedSingle = _deletedDouble = false; }
        std::u16string EmitQuote(bool doubleQuote);
        std::u16string LastWord(int requested) const;
        std::size_t VisibleCount() const;
    private:
        std::vector<std::u16string> _elements;
        bool _leftSingle = true, _leftDouble = true;
        bool _deletedSingle = false, _deletedDouble = false;
    };
}
