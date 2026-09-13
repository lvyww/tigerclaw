#include "MixedInputDecoder.h"
#include "CodeCase.h"
#include <algorithm>
#include <stdexcept>

namespace tiger::core
{
    FixedLengthMixedDecoder::FixedLengthMixedDecoder(Resolver firstCandidate, Resolver output)
        : _firstCandidate(std::move(firstCandidate)), _output(std::move(output))
    {
        if (!_firstCandidate || !_output) throw std::invalid_argument("Mixed decoder resolvers are required");
    }
    void FixedLengthMixedDecoder::ClearCache() { _cache.clear(); _version = -1; }
    MixedDecodeResult FixedLengthMixedDecoder::Decode(std::u16string_view raw, int maximum, int version,
        const std::unordered_map<std::size_t, std::u16string>& preferred)
    {
        if (version != _version) { _cache.clear(); _version = version; }
        MixedDecodeResult result;
        result.raw = raw;
        if (raw.empty()) return result;
        auto length = static_cast<std::size_t>(std::max(1, maximum));
        auto completed = (raw.size() - 1) / length;
        result.segments.reserve(completed);
        for (std::size_t i = 0; i < completed; ++i)
        {
            auto start = i * length;
            auto code = raw.substr(start, length);
            std::u16string candidate;
            if (auto found = preferred.find(start); found != preferred.end()) candidate = found->second;
            if (candidate.empty())
            {
                auto key = FoldOrdinalCode(code);
                auto found = _cache.find(key);
                if (found == _cache.end())
                {
                    auto packed = _firstCandidate(code);
                    auto resolved = packed.empty() ? std::u16string{} : _output(packed);
                    found = _cache.emplace(std::move(key), std::move(resolved)).first;
                }
                candidate = found->second;
            }
            result.prefix += candidate.empty() ? std::u16string(code) : candidate;
            result.segments.push_back({std::u16string(code), std::move(candidate)});
        }
        result.active = raw.substr(completed * length);
        result.surface = result.prefix + result.active;
        return result;
    }
}
