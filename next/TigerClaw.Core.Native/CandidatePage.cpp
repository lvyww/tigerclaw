#include "CandidatePage.h"
#include "CodeCase.h"
#include "LexiconText.h"
#include <algorithm>
namespace tiger::core
{
    int CandidatePageKeyDelta(std::u16string_view setting, int vk, bool shift)
    {
        setting = TrimText(setting);
        if (setting == u"Shift Tab/Tab") return vk == 0x09 ? (shift ? -1 : 1) : 0;
        if (shift) return 0;
        if (setting == u"[ ]") return vk == 0xdb ? -1 : vk == 0xdd ? 1 : 0;
        if (setting == u"PageUp/PageDown") return vk == 0x21 ? -1 : vk == 0x22 ? 1 : 0;
        return vk == 0xbd ? -1 : vk == 0xbb ? 1 : 0;
    }
    bool CandidatePageTracker::MoveKey(std::u16string_view rawCode, int mode, std::uint32_t total, int size,
        std::u16string_view setting, int vk, bool shift)
    {
        int delta = CandidatePageKeyDelta(setting, vk, shift);
        if (!delta) return false;
        Move(rawCode, mode, total, size, delta);
        return true;
    }
    void CandidatePageTracker::Reset()
    {
        _rawCode.clear(); _mode = 1; _index = 0;
    }
    std::uint32_t CandidatePageTracker::Resolve(std::u16string_view rawCode, int mode, std::uint32_t total, int size)
    {
        if (_mode != mode || _rawCode != rawCode)
        {
            _rawCode = rawCode; _mode = mode; _index = 0;
        }
        if (!total) _index = 0;
        else _index = std::min(_index, (total - 1) / static_cast<std::uint32_t>(std::clamp(size, 1, 10)));
        return _index;
    }
    void CandidatePageTracker::Move(std::u16string_view rawCode, int mode, std::uint32_t total, int size, int delta)
    {
        if (!delta) return; // Reference does not even reset identity on a zero move.
        Resolve(rawCode, mode, total, size);
        if (!total) return;
        auto maximum = (total - 1) / static_cast<std::uint32_t>(std::clamp(size, 1, 10));
        auto next = static_cast<std::int64_t>(_index) + delta;
        _index = static_cast<std::uint32_t>(std::clamp<std::int64_t>(next, 0, maximum));
    }
    CandidatePage ReadCandidatePage(const CompactLexicon& table, std::u16string_view code,
        std::int64_t requestedPage, int requestedSize)
    {
        CandidatePage result;
        auto normalized = NormalizeCode(code);
        if (normalized.empty()) return result;
        auto index = table.Find(normalized);
        if (!index) return result;
        result.found = true;
        result.total = table.CandidateCount(*index);
        if (!result.total) return result;
        auto size = static_cast<std::uint32_t>(std::clamp(requestedSize, 1, 10));
        auto maximumPage = (result.total - 1) / size;
        result.pageIndex = static_cast<std::uint32_t>(std::clamp<std::int64_t>(requestedPage, 0, maximumPage));
        auto start = result.pageIndex * size;
        auto count = std::min(size, result.total - start);
        result.entries.reserve(count);
        for (std::uint32_t offset = 0; offset < count; ++offset)
            result.entries.push_back(table.Candidate(*index, start + offset));
        return result;
    }
}
