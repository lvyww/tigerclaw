#pragma once
#include "CompactLexicon.h"
namespace tiger::core
{
    int CandidatePageKeyDelta(std::u16string_view setting, int vk, bool shift);
    struct CandidatePage
    {
        bool found = false;
        std::uint32_t total = 0;
        std::uint32_t pageIndex = 0;
        std::vector<std::u16string> entries;
    };
    CandidatePage ReadCandidatePage(const CompactLexicon& table, std::u16string_view code,
        std::int64_t requestedPage, int requestedSize);
    // Per-composition state, owned by the future serialized input engine.
    // Raw spelling (not normalized lookup spelling) and input mode identify it.
    class CandidatePageTracker
    {
    public:
        void Reset();
        std::uint32_t Resolve(std::u16string_view rawCode, int mode, std::uint32_t total, int size);
        void Move(std::u16string_view rawCode, int mode, std::uint32_t total, int size, int delta);
        bool MoveKey(std::u16string_view rawCode, int mode, std::uint32_t total, int size,
            std::u16string_view setting, int vk, bool shift);
        std::uint32_t Index() const { return _index; }
    private:
        std::u16string _rawCode;
        int _mode = 1; // CnIdle in the reference input state enum.
        std::uint32_t _index = 0;
    };
}
