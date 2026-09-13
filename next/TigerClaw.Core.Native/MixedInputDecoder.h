#pragma once
#include <functional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace tiger::core
{
    struct MixedSegment
    {
        std::u16string code, candidate;
    };
    struct MixedDecodeResult
    {
        std::u16string raw, prefix, active, surface;
        std::vector<MixedSegment> segments;
        std::u16string ComposeChinese(std::u16string_view output, std::u16string_view suffix = {}) const
        { return prefix + std::u16string(output) + std::u16string(suffix); }
    };
    class FixedLengthMixedDecoder
    {
    public:
        using Resolver = std::function<std::u16string(std::u16string_view)>;
        // First packed candidate (empty if none), then its output converter.
        FixedLengthMixedDecoder(Resolver firstCandidate, Resolver output);
        MixedDecodeResult Decode(std::u16string_view raw, int maximum, int version,
            const std::unordered_map<std::size_t, std::u16string>& preferred = {});
        void ClearCache();
    private:
        Resolver _firstCandidate, _output;
        int _version = -1;
        std::unordered_map<std::u16string, std::u16string> _cache;
    };
}
