#pragma once
#include "TextElements.h"
#include "ConfigParser.h"
#include <cstdint>
#include <span>
#include <stdexcept>
#include <algorithm>

namespace tiger::core
{
    // Same immutable Bloom filter and nonoverlapping phrase DP as C#.
    class SentenceLexicalPrior
    {
        std::span<const unsigned char> _data;
        std::uint32_t _bits, _hashes, _minimum, _maximum;
    public:
        explicit SentenceLexicalPrior(std::span<const unsigned char> data) : _data(data)
        {
            auto read = [&](std::size_t at) { return std::uint32_t(data[at]) | (std::uint32_t(data[at+1]) << 8) |
                (std::uint32_t(data[at+2]) << 16) | (std::uint32_t(data[at+3]) << 24); };
            if (data.size() < 32 || std::string_view(reinterpret_cast<const char*>(data.data()), 8) != "TCSLEX01")
                throw std::invalid_argument("Invalid lexical prior magic");
            _bits = read(16); _hashes = read(20); _minimum = read(24); _maximum = read(28);
            if (read(8) != 1 || !read(12) || _bits < 8 || _bits % 8 || !_hashes || _hashes > 32 ||
                _minimum < 2 || _maximum < _minimum || _maximum > 16 || data.size() != 32 + _bits / 8)
                throw std::invalid_argument("Invalid lexical prior header");
        }
        static const SentenceLexicalPrior& Embedded();
        bool Contains(std::u16string_view text) const
        {
            if (text.empty()) return false;
            std::uint64_t first = 2166136261, second = 16777619;
            for (auto byte : EncodeUtf8Text(text, false))
            {
                auto value = static_cast<unsigned char>(byte);
                first = (first * 131 + value + 17) % 4294967291ull;
                second = (second * 137 + value + 53) % 4294967291ull;
            }
            if (!second) second = 1;
            for (std::uint64_t i = 0; i < _hashes; ++i)
            {
                auto bit = (first + i * second + i * i * 97) % _bits;
                if (!(_data[32 + bit / 8] & (1 << (bit % 8)))) return false;
            }
            return true;
        }
        double Score(std::u16string_view text) const
        {
            auto starts = TextElementStarts(text);
            starts.push_back(text.size());
            std::vector<double> best(starts.size());
            for (std::size_t end = 1; end < starts.size(); ++end)
            {
                best[end] = best[end - 1];
                for (std::size_t length = _minimum; length <= _maximum && length <= end; ++length)
                {
                    auto begin = end - length;
                    if (Contains(text.substr(starts[begin], starts[end] - starts[begin])))
                        best[end] = std::max(best[end], best[begin] + 1 + .2 * (length - 2));
                }
            }
            return best.back();
        }
    };
}
