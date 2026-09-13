#include "LexiconAssembly.h"
#include "CodeCase.h"
#include "LexiconFile.h"
#include <algorithm>
#include <numeric>
#include <unordered_map>
#include <unordered_set>
#include <stdexcept>

namespace tiger::core
{
    std::vector<CompactLexicon::Entry> AssembleCodedRows(std::span<const LexiconRow> rows)
    {
        std::vector<std::size_t> order(rows.size());
        std::iota(order.begin(), order.end(), std::size_t(0));
        std::stable_sort(order.begin(), order.end(), [&](auto a, auto b) { return rows[a].frequency > rows[b].frequency; });
        std::vector<CompactLexicon::Entry> result;
        std::unordered_map<std::u16string, std::size_t> codes;
        std::vector<std::unordered_set<std::u16string_view>> seen;
        for (auto index : order)
        {
            const auto& row = rows[index];
            auto code = NormalizeCode(row.code);
            if (code.empty()) continue;
            auto [found, inserted] = codes.emplace(FoldOrdinalCode(code), result.size());
            if (inserted) { result.emplace_back(std::move(code), std::vector<std::u16string>{}); seen.emplace_back(); }
            auto position = found->second;
            // Views point into immutable input rows, not vectors that grow below.
            if (seen[position].insert(row.text).second) result[position].second.push_back(row.text);
        }
        return result;
    }
    static void ApplyEditRows(std::vector<CompactLexicon::Entry>& entries, std::span<const std::u16string> lines, bool custom)
    {
        std::unordered_map<std::u16string, std::size_t> codes;
        for (std::size_t i = 0; i < entries.size(); ++i)
            if (!codes.emplace(FoldOrdinalCode(entries[i].first), i).second)
                throw std::invalid_argument("Duplicate adjustment code");
        for (const auto& raw : lines)
        {
            auto line = TrimText(raw);
            if (custom && (line.empty() || line.starts_with(u"#"))) continue;
            int action = custom ? 4 : -1;
            std::u16string_view prefixes[] = {u"{\u6dfb\u52a0}", u"{\u7f6e\u9876}", u"{\u5220\u9664}", u"{\u524d\u79fb}"};
            for (int i = 0; !custom && i < 4; ++i)
                if (line.starts_with(prefixes[i])) { action = i; line.remove_prefix(prefixes[i].size()); break; }
            if (action < 0) continue;
            // Only tabs split the payload; empty fields are ignored exactly as
            // TryParsePair. Unlike ordinary table rows, '#' is not a comment.
            std::u16string_view fields[2];
            int count = 0;
            for (std::size_t start = 0; start < line.size() && count < 2;)
            {
                auto end = line.find(u'\t', start);
                if (end == line.npos) end = line.size();
                if (end > start) fields[count++] = line.substr(start, end - start);
                start = end + 1;
            }
            if (count < 2) continue;
            auto code = NormalizeCode(fields[0]);
            auto text = ParseLexiconEntryToken(fields[1]);
            if (code.empty() || text.empty()) continue;
            auto folded = FoldOrdinalCode(code);
            auto found = codes.find(folded);
            if (found == codes.end())
            {
                if (action == 2 || action == 3) continue;
                auto index = entries.size();
                entries.emplace_back(std::move(code), std::vector<std::u16string>{});
                found = codes.emplace(std::move(folded), index).first;
            }
            auto& candidates = entries[found->second].second;
            auto same = [&](const std::u16string& candidate) { return CandidateCommitText(candidate) == CandidateCommitText(text); };
            if (action == 4)
            { std::erase_if(candidates, same); candidates.insert(candidates.begin(), std::move(text)); continue; }
            if (action == 2)
            { std::erase_if(candidates, same); continue; }
            auto match = std::find_if(candidates.begin(), candidates.end(), same);
            if (action == 3)
            {
                if (match != candidates.end() && match != candidates.begin()) std::iter_swap(match, match - 1);
                continue;
            }
            auto stored = match == candidates.end() ? std::move(text) : *match;
            if (match != candidates.end()) candidates.erase(match);
            if (action == 1) candidates.insert(candidates.begin(), std::move(stored));
            else candidates.push_back(std::move(stored));
        }
    }
    void ApplyAdjustments(std::vector<CompactLexicon::Entry>& entries, std::span<const std::u16string> lines)
    { ApplyEditRows(entries, lines, false); }
    void ApplyCustomRows(std::vector<CompactLexicon::Entry>& entries, std::span<const std::u16string> lines)
    { ApplyEditRows(entries, lines, true); }
    void LoadAdjustmentFile(std::vector<CompactLexicon::Entry>& entries, const std::filesystem::path& path, bool custom)
    {
        if (!std::filesystem::is_regular_file(path)) return;
        std::vector<std::u16string> lines;
        ReadLexiconLines(path, [&](std::u16string_view line) { lines.emplace_back(line); });
        ApplyEditRows(entries, lines, custom);
    }
}
