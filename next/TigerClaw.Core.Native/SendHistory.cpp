#include "SendHistory.h"
#include "TextElements.h"
#include <algorithm>
namespace tiger::core
{
    std::optional<std::u16string> GuessPassThroughText(int vk, bool shift, bool capsLock)
    {
        if (vk == 0x20) return u" ";
        if (vk >= 0x41 && vk <= 0x5a) return std::u16string(1, static_cast<char16_t>((shift != capsLock ? u'A' : u'a') + vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39)
            return std::u16string(1, shift ? std::u16string_view(u")!@#$%^&*(")[vk - 0x30] : static_cast<char16_t>(vk));
        if (vk >= 0x60 && vk <= 0x69) return std::u16string(1, static_cast<char16_t>(u'0' + vk - 0x60));
        constexpr int keys[]{0xbb, 0xbc, 0xbd, 0xbe, 0xc0, 0xdb, 0xdc, 0xdd, 0xbf, 0xba, 0xde};
        constexpr std::u16string_view plain = u"=,-.`[\\]/;'", shifted = u"+<_>~{|}?:\"";
        for (std::size_t index = 0; index < std::size(keys); ++index)
            if (vk == keys[index]) return std::u16string(1, (shift ? shifted : plain)[index]);
        return std::nullopt;
    }
    void SendHistory::AppendPassThrough(int vk, bool shift, bool ctrl, bool alt, bool win, bool capsLock)
    {
        if (ctrl || alt || win) return;
        auto text = GuessPassThroughText(vk, shift, capsLock);
        if (text) Append(*text);
    }
    void SendHistory::Append(std::u16string_view text)
    {
        auto starts = TextElementStarts(text);
        for (std::size_t index = 0; index < starts.size(); ++index)
        {
            auto end = index + 1 < starts.size() ? starts[index + 1] : text.size();
            _elements.emplace_back(text.substr(starts[index], end - starts[index]));
        }
    }
    void SendHistory::Backspace()
    {
        if (_elements.empty()) return;
        auto last = std::move(_elements.back()); _elements.pop_back();
        if (last == u"\u201c" || last == u"\u201d") { _leftDouble = !_leftDouble; _deletedDouble = true; }
        else if (last == u"\u2018" || last == u"\u2019") { _leftSingle = !_leftSingle; _deletedSingle = true; }
        else { _deletedSingle = false; _deletedDouble = false; }
    }
    std::u16string SendHistory::EmitQuote(bool doubleQuote)
    {
        bool& left = doubleQuote ? _leftDouble : _leftSingle;
        bool& deleted = doubleQuote ? _deletedDouble : _deletedSingle;
        bool colon = !_elements.empty() && (_elements.back() == u":" || _elements.back() == u"\uff1a");
        bool emitLeft = colon && !deleted ? true : deleted ? !left : left;
        left = !emitLeft; deleted = false;
        return doubleQuote ? (emitLeft ? u"\u201c" : u"\u201d") : (emitLeft ? u"\u2018" : u"\u2019");
    }
    std::size_t SendHistory::VisibleCount() const { return std::min<std::size_t>(_elements.size(), 20); }
    std::u16string SendHistory::LastWord(int requested) const
    {
        if (requested <= 0) return {};
        auto count = std::min<std::size_t>(VisibleCount(), static_cast<std::size_t>(requested));
        std::u16string text;
        for (auto index = _elements.size() - count; index < _elements.size(); ++index) text += _elements[index];
        return text;
    }
}
