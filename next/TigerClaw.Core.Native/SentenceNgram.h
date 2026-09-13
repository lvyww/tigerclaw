#pragma once
#include <span>
#include <cstdint>
#include <bit>
#include <cmath>
#include <algorithm>
#include <string_view>
#include <stdexcept>

namespace tiger::core
{
    // Read-only view of TCSKNM01 (Windows V2 model). The backing image must
    // remain immutable and alive; a mapped-file owner is supplied by the host.
    class SentenceNgram
    {
        struct Section { std::uint64_t offset, count; unsigned keyBytes; };
    public:
        explicit SentenceNgram(std::span<const std::uint8_t> image) : _image(image)
        {
            if (image.empty() || image.size() > (std::uint64_t{4} << 30)) Invalid();
            std::uint64_t position = 0;
            for (char c : std::string_view("TCSKNM01")) if (Read(position++, 1) != static_cast<unsigned char>(c)) Invalid();
            if (Read(position, 4) != 1) Invalid();
            position += 4;
            auto section = [&](unsigned countBytes, unsigned keyBytes)
            {
                auto count = Read(position, countBytes);
                if (count >> (countBytes * 8 - 1)) Invalid(); // signed count in C# format
                position += countBytes;
                if (count > (image.size() - position) / (keyBytes + 4)) Invalid();
                Section result{position, count, keyBytes};
                position += count * (keyBytes + 4);
                return result;
            };
            _unigrams = section(4, 4);
            _bigrams = section(8, 8);
            _bigramContexts = section(4, 4);
            _trigrams = section(8, 8);
            _trigramContexts = section(8, 8);
            if (position != image.size() || !_unigrams.count) Invalid();
            _unknown = Lookup(_unigrams, 0, 0);
            if (!std::isfinite(_unknown) || _unknown <= 0 || _unknown > 1) Invalid();
        }
        double LogProbability(std::u16string_view previous2, std::u16string_view previous1,
            std::u16string_view target, bool includeUnigram = true) const
        {
            auto first = Scalar(previous2), second = Scalar(previous1), third = Scalar(target);
            double unigram = includeUnigram ? Lookup(_unigrams, third, _unknown) : 0;
            double bigram = Lookup(_bigrams, Pair(second, third), 0);
            double bigramLambda = Lookup(_bigramContexts, second, 1);
            bigram += bigramLambda * unigram;
            double trigram = Lookup(_trigrams, Triple(first, second, third), 0);
            double trigramLambda = Lookup(_trigramContexts, Pair(first, second), 1);
            trigram += trigramLambda * bigram;
            // std::max with probability first preserves NaN like Math.Max.
            return std::log(std::max(trigram, 1e-300));
        }
        bool HasObservedBigram(std::u16string_view previous, std::u16string_view target) const
        {
            auto key = Pair(Scalar(previous), Scalar(target));
            auto index = Find(_bigrams, key);
            return index < _bigrams.count && Read(_bigrams.offset + index * 12, 8) == key;
        }
        static std::uint32_t Scalar(std::u16string_view token)
        {
            if (token.size() == 1) return token.front();
            if (token.size() == 2 && token[0] >= 0xd800 && token[0] <= 0xdbff && token[1] >= 0xdc00 && token[1] <= 0xdfff)
                return 0x10000 + ((token[0] - 0xd800) << 10) + token[1] - 0xdc00;
            return 0;
        }
        static std::uint64_t Pair(std::uint32_t a, std::uint32_t b)
        { return (std::uint64_t{a} << 21) | (b & 0x1fffff); }
        static std::uint64_t Triple(std::uint32_t a, std::uint32_t b, std::uint32_t c)
        { return (std::uint64_t{a} << 42) | (std::uint64_t{b} << 21) | (c & 0x1fffff); }
    private:
        [[noreturn]] static void Invalid() { throw std::invalid_argument("Invalid sentence n-gram V2 image"); }
        std::uint64_t Read(std::uint64_t offset, unsigned size) const
        {
            if (offset > _image.size() || size > _image.size() - offset) Invalid();
            std::uint64_t result = 0;
            for (unsigned i = 0; i < size; ++i) result |= std::uint64_t{_image[static_cast<std::size_t>(offset + i)]} << (i * 8);
            return result;
        }
        std::uint64_t Find(Section section, std::uint64_t key) const
        {
            std::uint64_t low = 0, high = section.count;
            while (low < high)
            {
                auto middle = low + (high - low) / 2;
                auto found = Read(section.offset + middle * (section.keyBytes + 4), section.keyBytes);
                bool less = section.keyBytes == 4 ? std::bit_cast<std::int32_t>(static_cast<std::uint32_t>(found)) < static_cast<std::int64_t>(key) : found < key;
                if (less) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        float Lookup(Section section, std::uint64_t key, float fallback) const
        {
            auto index = Find(section, key);
            if (index == section.count) return fallback;
            auto offset = section.offset + index * (section.keyBytes + 4);
            return Read(offset, section.keyBytes) == key
                ? std::bit_cast<float>(static_cast<std::uint32_t>(Read(offset + section.keyBytes, 4))) : fallback;
        }
        std::span<const std::uint8_t> _image;
        Section _unigrams{}, _bigrams{}, _bigramContexts{}, _trigrams{}, _trigramContexts{};
        float _unknown = 0;
    };
}
