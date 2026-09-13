#pragma once
#include <cstdint>
#include <filesystem>
#include <optional>
#include <span>
#include <string>
#include <vector>
#include <utility>

namespace tiger::core
{
    // Owns a validated TCLX v1 image; no managed runtime, Dictionary or text
    // expansion. Code enumeration follows source insertion order, not lookup order.
    class CompactLexicon final
    {
        std::vector<std::uint8_t> image_;
        std::uint32_t count_ = 0, texts_ = 0;
        std::size_t codeIds_ = 0, sorted_ = 0, starts_ = 0, candidateIds_ = 0, offsets_ = 0, data_ = 0;
        std::uint32_t Read(std::size_t offset) const;
        void ValidateOffsets(std::size_t offset, std::uint32_t count, std::uint32_t end) const;
        void ValidateText(std::uint32_t id) const;
        std::u16string Text(std::uint32_t id) const;
        static int Compare(std::u16string_view left, std::u16string_view right);
    public:
        using Entry = std::pair<std::u16string, std::vector<std::u16string>>;
        explicit CompactLexicon(std::vector<std::uint8_t> image);
        static CompactLexicon Build(std::span<const Entry> entries);
        std::span<const std::uint8_t> Image() const noexcept { return image_; }
        static CompactLexicon Load(const std::filesystem::path& path);
        std::uint32_t Count() const noexcept { return count_; }
        std::size_t BinarySize() const noexcept { return image_.size(); }
        std::u16string Code(std::uint32_t index) const;
        std::optional<std::uint32_t> Find(std::u16string_view code) const;
        std::uint32_t CandidateCount(std::uint32_t index) const;
        std::u16string Candidate(std::uint32_t index, std::uint32_t rank) const;
    };
}
