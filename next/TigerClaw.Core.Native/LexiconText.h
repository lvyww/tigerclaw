#pragma once
#include <string>
#include <string_view>
#include <vector>
#include <cstdint>
#include <utility>

namespace tiger::core
{
    bool IsDotNetWhiteSpace(char16_t value);
    std::u16string_view TrimText(std::u16string_view value);
    std::u16string StripInlineComment(std::u16string_view line);
    std::u16string DecodeLexiconEscapes(std::u16string_view value);
    std::u16string ParseLexiconEntryToken(std::u16string_view token);
    std::u16string_view CandidateCommitText(std::u16string_view entry);
    std::u16string_view CandidateDisplayText(std::u16string_view entry);
    bool ParseIntegerToken(std::u16string_view token, std::int32_t& value,
        std::u16string_view positive = u"+", std::u16string_view negative = u"-");
    struct LexiconRow
    {
        std::u16string code;
        std::u16string text;
        std::int32_t frequency = 0;
    };
    // Stateful streaming line parser. File decoding and culture-specific number
    // signs belong to the loader; default signs match zh-CN/en-US/invariant.
    class LexiconLineParser
    {
    public:
        explicit LexiconLineParser(bool yaml, std::u16string positive = u"+", std::u16string negative = u"-")
            : body_(!yaml), positive_(std::move(positive)), negative_(std::move(negative)) {}
        std::vector<LexiconRow> Parse(std::u16string_view raw);
    private:
        bool Integer(std::u16string_view token, std::int32_t& value) const;
        bool body_;
        std::u16string positive_, negative_;
    };
}
