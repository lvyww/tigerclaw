#include "CompactLexicon.h"
#include <algorithm>
#include <climits>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <unordered_map>
#include <numeric>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    namespace
    {
        constexpr std::size_t HeaderSize = 20;
        constexpr std::size_t MaxFileBytes = 256 * 1024 * 1024;
        void Require(bool value, const char* reason)
        {
            if (!value) throw std::runtime_error(reason);
        }
    }
    std::uint32_t CompactLexicon::Read(std::size_t offset) const
    {
        Require(offset <= image_.size() && image_.size() - offset >= 4, "truncated TCLX integer");
        return std::uint32_t(image_[offset]) | (std::uint32_t(image_[offset + 1]) << 8) |
            (std::uint32_t(image_[offset + 2]) << 16) | (std::uint32_t(image_[offset + 3]) << 24);
    }
    void CompactLexicon::ValidateText(std::uint32_t id) const
    {
        Require(id < texts_, "invalid TCLX text id");
    }
    void CompactLexicon::ValidateOffsets(std::size_t offset, std::uint32_t count, std::uint32_t end) const
    {
        Require(Read(offset) == 0 && Read(offset + std::size_t(count) * 4) == end, "invalid TCLX offset endpoints");
        std::uint32_t previous = 0;
        for (std::uint32_t i = 1; i <= count; ++i)
        {
            auto current = Read(offset + std::size_t(i) * 4);
            Require(current >= previous && current <= end, "invalid TCLX offset order");
            previous = current;
        }
    }
    int CompactLexicon::Compare(std::u16string_view left, std::u16string_view right)
    {
        // The initial portable harness only accepts ASCII codes. Windows uses
        // its ordinal comparator, with non-ASCII parity explicitly still a gate.
        auto ascii = [](std::u16string_view text)
        { return std::all_of(text.begin(), text.end(), [](char16_t c) { return c < 128; }); };
        if (ascii(left) && ascii(right))
        {
            auto upper = [](char16_t c) { return c >= u'a' && c <= u'z' ? char16_t(c - 32) : c; };
            for (std::size_t i = 0; i < std::min(left.size(), right.size()); ++i)
            {
                auto a = upper(left[i]), b = upper(right[i]);
                if (a != b) return a < b ? -1 : 1;
            }
            return left.size() == right.size() ? 0 : left.size() < right.size() ? -1 : 1;
        }
#ifdef _WIN32
        Require(left.size() <= INT_MAX && right.size() <= INT_MAX, "code too long");
        auto a = std::wstring(left.begin(), left.end()), b = std::wstring(right.begin(), right.end());
        int result = CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE);
        Require(result != 0, "ordinal comparison failed");
        return result - CSTR_EQUAL;
#else
        throw std::runtime_error("non-ASCII code comparison requires Windows in this experimental build");
#endif
    }
    CompactLexicon::CompactLexicon(std::vector<std::uint8_t> image) : image_(std::move(image))
    {
        Require(image_.size() >= HeaderSize && image_.size() <= MaxFileBytes, "invalid TCLX image size");
        Require(Read(0) == 0x584C4354 && Read(4) == 1, "unsupported TCLX header/version");
        count_ = Read(8);
        auto candidates = Read(12);
        texts_ = Read(16);
        std::uint64_t data = HeaderSize + 4ull * (3ull * count_ + 1 + candidates + std::uint64_t(texts_) + 1);
        Require(data <= image_.size() && (image_.size() - data) % 2 == 0, "truncated TCLX sections");
        codeIds_ = HeaderSize;
        sorted_ = codeIds_ + std::size_t(count_) * 4;
        starts_ = sorted_ + std::size_t(count_) * 4;
        candidateIds_ = starts_ + (std::size_t(count_) + 1) * 4;
        offsets_ = candidateIds_ + std::size_t(candidates) * 4;
        data_ = static_cast<std::size_t>(data);
        ValidateOffsets(starts_, count_, candidates);
        ValidateOffsets(offsets_, texts_, static_cast<std::uint32_t>((image_.size() - data_) / 2));
        for (std::uint32_t i = 0; i < count_; ++i) ValidateText(Read(codeIds_ + std::size_t(i) * 4));
        for (std::uint32_t i = 0; i < candidates; ++i) ValidateText(Read(candidateIds_ + std::size_t(i) * 4));
        std::u16string previous;
        for (std::uint32_t i = 0; i < count_; ++i)
        {
            auto index = Read(sorted_ + std::size_t(i) * 4);
            Require(index < count_, "invalid TCLX sorted index");
            auto current = Code(index);
            Compare(current, current); // Validate supported code domain even for one code.
            Require(i == 0 || Compare(previous, current) < 0, "duplicate/unsorted TCLX code");
            previous = std::move(current);
        }
    }
    CompactLexicon CompactLexicon::Load(const std::filesystem::path& path)
    {
        std::ifstream input(path, std::ios::binary | std::ios::ate);
        Require(bool(input), "cannot open TCLX file");
        auto length = input.tellg();
        Require(length >= 0 && std::uint64_t(length) <= MaxFileBytes, "TCLX file too large");
        std::vector<std::uint8_t> bytes(static_cast<std::size_t>(length));
        input.seekg(0);
        Require(bool(input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()))), "cannot read TCLX file");
        return CompactLexicon(std::move(bytes));
    }
    CompactLexicon CompactLexicon::Build(std::span<const Entry> entries)
    {
        Require(entries.size() <= MaxFileBytes / 12, "too many TCLX codes");
        // All construction indexes are temporary. Only the validated byte image
        // survives publication; candidate duplicates and empty entries are legal.
        std::unordered_map<std::u16string_view, std::uint32_t> ids;
        std::vector<std::u16string_view> texts;
        std::vector<std::uint32_t> codeIds, candidates, starts, sorted(entries.size());
        std::uint64_t characters = 0;
        auto id = [&](std::u16string_view value)
        {
            auto found = ids.find(value);
            if (found != ids.end()) return found->second;
            Require(texts.size() < MaxFileBytes / 4 && value.size() <= MaxFileBytes / 2 - characters, "TCLX text pool too large");
            auto next = static_cast<std::uint32_t>(texts.size());
            texts.push_back(value); ids.emplace(value, next); characters += value.size();
            return next;
        };
        for (const auto& [code, values] : entries)
        {
            codeIds.push_back(id(code));
            starts.push_back(static_cast<std::uint32_t>(candidates.size()));
            Require(values.size() <= MaxFileBytes / 4 - candidates.size(), "too many TCLX candidates");
            for (const auto& text : values) candidates.push_back(id(text));
        }
        starts.push_back(static_cast<std::uint32_t>(candidates.size()));
        std::iota(sorted.begin(), sorted.end(), 0u);
        std::sort(sorted.begin(), sorted.end(), [&](auto a, auto b) { return Compare(entries[a].first, entries[b].first) < 0; });
        auto size = HeaderSize + 4ull * (3ull * entries.size() + 1 + candidates.size() + texts.size() + 1) + characters * 2;
        Require(size <= MaxFileBytes, "TCLX image too large");
        std::vector<std::uint8_t> bytes;
        bytes.reserve(static_cast<std::size_t>(size));
        auto write = [&](std::uint32_t value)
        { for (int shift = 0; shift < 32; shift += 8) bytes.push_back(static_cast<std::uint8_t>(value >> shift)); };
        write(0x584c4354); write(1); write(static_cast<std::uint32_t>(entries.size()));
        write(static_cast<std::uint32_t>(candidates.size())); write(static_cast<std::uint32_t>(texts.size()));
        for (auto value : codeIds) write(value);
        for (auto value : sorted) write(value);
        for (auto value : starts) write(value);
        for (auto value : candidates) write(value);
        std::uint32_t offset = 0;
        for (auto text : texts) { write(offset); offset += static_cast<std::uint32_t>(text.size()); }
        write(offset);
        for (auto text : texts) for (auto c : text)
        { bytes.push_back(static_cast<std::uint8_t>(c)); bytes.push_back(static_cast<std::uint8_t>(c >> 8)); }
        return CompactLexicon(std::move(bytes));
    }
    std::u16string CompactLexicon::Text(std::uint32_t id) const
    {
        ValidateText(id);
        auto start = Read(offsets_ + std::size_t(id) * 4), end = Read(offsets_ + (std::size_t(id) + 1) * 4);
        std::u16string result;
        result.reserve(end - start);
        for (auto i = start; i < end; ++i)
        {
            auto at = data_ + std::size_t(i) * 2;
            result.push_back(char16_t(std::uint16_t(image_[at]) | (std::uint16_t(image_[at + 1]) << 8)));
        }
        return result;
    }
    std::u16string CompactLexicon::Code(std::uint32_t index) const
    {
        if (index >= count_) throw std::out_of_range("code index");
        return Text(Read(codeIds_ + std::size_t(index) * 4));
    }
    std::optional<std::uint32_t> CompactLexicon::Find(std::u16string_view code) const
    {
        std::uint32_t low = 0, high = count_;
        while (low < high)
        {
            auto middle = low + (high - low) / 2;
            auto index = Read(sorted_ + std::size_t(middle) * 4);
            int comparison = Compare(Code(index), code);
            if (comparison == 0) return index;
            if (comparison < 0) low = middle + 1; else high = middle;
        }
        return std::nullopt;
    }
    std::uint32_t CompactLexicon::CandidateCount(std::uint32_t index) const
    {
        if (index >= count_) throw std::out_of_range("code index");
        return Read(starts_ + (std::size_t(index) + 1) * 4) - Read(starts_ + std::size_t(index) * 4);
    }
    std::u16string CompactLexicon::Candidate(std::uint32_t index, std::uint32_t rank) const
    {
        if (rank >= CandidateCount(index)) throw std::out_of_range("candidate rank");
        auto at = Read(starts_ + std::size_t(index) * 4) + rank;
        return Text(Read(candidateIds_ + std::size_t(at) * 4));
    }
}
