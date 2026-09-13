#include "LexiconMetadata.h"
#include "LexiconFile.h"
#include <algorithm>

namespace tiger::core
{
    std::vector<std::u16string> CodeSet::Keys() const
    {
        std::vector<std::u16string> result;
        for (const auto& pair : keys_) result.push_back(pair.second);
        std::sort(result.begin(), result.end());
        return result;
    }
    TextMap LoadTextMetadata(const std::filesystem::path& directory, bool splits)
    {
        TextMap result;
        if (directory.empty() || !std::filesystem::is_directory(directory)) return result;
        auto extension = splits ? u".\u62c6\u5206" : u".\u6ce8\u91ca";
        // Do not reuse the schema-name/culture sort: C# consumes metadata files
        // in raw directory enumeration order, independently of main tables.
        for (const auto& item : std::filesystem::directory_iterator(directory))
        {
            if (item.is_directory() || item.path().extension().u16string() != extension) continue;
            ReadLexiconLines(item.path(), [&](std::u16string_view raw)
            {
                auto line = TrimText(raw);
                if (line.empty() || line.starts_with(u"#")) return;
                std::u16string_view fields[2];
                int count = 0;
                for (std::size_t start = 0; start < line.size() && count < 2;)
                {
                    auto end = line.find_first_of(u"\t ", start);
                    if (end == line.npos) end = line.size();
                    if (end > start) fields[count++] = line.substr(start, end - start);
                    start = end + 1;
                }
                if (count < 2) return;
                auto key = DecodeLexiconEscapes(fields[0]), value = DecodeLexiconEscapes(fields[1]);
                if (key.empty() || value.empty()) return;
                if (splits) result[key] = std::move(value);
                else
                {
                    auto [found, inserted] = result.emplace(key, value);
                    if (!inserted) found->second += u" " + value;
                }
            });
        }
        return result;
    }
    LexiconMetadata BuildLexiconMetadata(std::span<const CompactLexicon::Entry> entries)
    {
        LexiconMetadata result;
        CodeSet heads;
        bool zIsCode = false;
        for (const auto& [code, candidates] : entries)
        {
            if (code.empty()) continue;
            heads.Add(std::u16string_view(code).substr(0, 1));
            if (FoldOrdinalCode(std::u16string_view(code).substr(0, 1)) != u"Z" && code.find(u'z') != code.npos) zIsCode = true;
            if (!candidates.empty())
                for (std::size_t i = 1; i < code.size(); ++i) result.nonTerminal.Add(std::u16string_view(code).substr(0, i));
            for (const auto& candidate : candidates)
            {
                auto text = CandidateCommitText(candidate);
                if (text.empty()) continue;
                auto [found, inserted] = result.fullCodes.emplace(std::u16string(text), code);
                if (!inserted && found->second.size() < code.size()) found->second = code;
            }
        }
        for (const auto& [code, candidates] : entries)
            if (!code.empty() && candidates.size() == 1 && !result.nonTerminal.Contains(code)) result.unique.Add(code);
        result.shortSemicolon = heads.Contains(u";");
        result.shortSlash = heads.Contains(u"/");
        result.shortBracket = heads.Contains(u"[");
        result.shortZ = !zIsCode && heads.Contains(u"a");
        struct Symbol { std::u16string code; std::size_t candidates, count = 0; };
        std::unordered_map<std::u16string, Symbol> symbols;
        for (const auto& [code, candidates] : entries)
        {
            if (code.empty()) continue;
            if ((result.shortSemicolon && code.starts_with(u";")) || (result.shortSlash && code.starts_with(u"/")) ||
                (result.shortBracket && code.starts_with(u"[")) || (result.shortZ && FoldOrdinalCode(code).starts_with(u"Z")))
                symbols.emplace(FoldOrdinalCode(code), Symbol{code, candidates.size(), 0});
        }
        for (const auto& [key, symbol] : symbols)
            for (std::size_t i = 1; i <= symbol.code.size(); ++i)
            {
                auto found = symbols.find(FoldOrdinalCode(std::u16string_view(symbol.code).substr(0, i)));
                if (found != symbols.end()) found->second.count += found->second.candidates;
            }
        for (const auto& [key, symbol] : symbols) if (symbol.count == 1) result.autoShort.Add(symbol.code);
        return result;
    }
}
