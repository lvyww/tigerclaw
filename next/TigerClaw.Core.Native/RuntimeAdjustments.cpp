#include "RuntimeLexicons.h"
#include "CodeCase.h"
#include <algorithm>
#include <fstream>
#include "OutputServices.h"
#include <sstream>
#include <iomanip>

namespace tiger::core
{
    std::filesystem::path RuntimeLexicons::ExportLexicon() const
    {
        auto snapshot=Read();
        if (!snapshot || snapshot->schemaName.empty()) throw std::runtime_error("current schema is empty");
        std::u16string name;
        for (auto c:snapshot->schemaName)
            if (c>=32 && std::u16string_view(u"<>:\"/\\|?*").find(c)==std::u16string_view::npos) name+=c;
        if (TrimText(name).empty()) name=u"export";
        auto clock=ReadLocalOutputClock(); std::ostringstream stamp;
        stamp << ' ' << std::setfill('0') << std::setw(4) << clock.year << std::setw(2) << clock.month << std::setw(2) << clock.day
            << '-' << std::setw(2) << clock.hour << std::setw(2) << clock.minute << ".txt";
        auto suffix=stamp.str(); name.append(suffix.begin(),suffix.end());
        auto directory=_executableDirectory / u"码表导出"; std::filesystem::create_directories(directory);
        auto path=directory / name; std::ofstream file(path,std::ios::binary|std::ios::trunc);
        file.write("\xef\xbb\xbf",3);
        const auto& table=snapshot->schema->table;
        for (std::uint32_t i=0;i<table.Count();++i)
        {
            if (!table.CandidateCount(i)) continue;
            auto line=table.Code(i); line+=u' ';
            for (std::uint32_t j=0;j<table.CandidateCount(i);++j)
            {
                if(j) line+=u' ';
                auto entry=table.Candidate(i,j);
                auto display=CandidateDisplayText(entry),commit=CandidateCommitText(entry);
                auto value=display==commit ? std::u16string(commit) : std::u16string(display)+u"=>"+std::u16string(commit);
                for (std::size_t k=0;k<value.size();++k)
                {
                    auto c=value[k];
                    if(c==u'\\') line+=u"\\\\";
                    else if(c==u' ') line+=u"\\s";
                    else if(c==u'\t') line+=u"\\t";
                    else if(c==u'\n') line+=u"\\n";
                    else if(c==u'\r' && k+1<value.size() && value[k+1]==u'\n') { line+=u"\\n"; ++k; }
                    else line+=c;
                }
            }
            auto bytes=EncodeUtf8Text(line+u"\r\n"); file.write(reinterpret_cast<const char*>(bytes.data()),static_cast<std::streamsize>(bytes.size()));
        }
        file.close(); if(!file) throw std::runtime_error("Cannot export lexicon");
        return path;
    }
    bool RuntimeLexicons::AdjustCandidate(CandidateAdjustment operation, std::u16string_view code,
        std::u16string_view candidate)
    {
        auto normalized = NormalizeCode(code);
        auto text = operation == CandidateAdjustment::Add ? ParseLexiconEntryToken(candidate) : std::u16string(CandidateCommitText(candidate));
        if (normalized.empty() || text.empty()) return false;
        std::lock_guard guard(_writer);
        auto previous = Read();
        if (!previous) return false;
        const auto& table = previous->schema->table;
        auto found = table.Find(normalized);
        std::vector<std::u16string> candidates;
        if (found)
            for (std::uint32_t i = 0; i < table.CandidateCount(*found); ++i) candidates.push_back(table.Candidate(*found, i));
        auto match = std::find_if(candidates.begin(), candidates.end(), [&](const auto& entry) { return CandidateCommitText(entry) == text; });
        if (operation == CandidateAdjustment::Add)
        {
            std::erase_if(candidates, [&](const auto& entry) { return CandidateCommitText(entry) == CandidateCommitText(text); });
            candidates.push_back(text);
        }
        else if (operation == CandidateAdjustment::Delete)
        {
            if (match == candidates.end()) return false;
            std::erase_if(candidates, [&](const auto& entry) { return CandidateCommitText(entry) == text; });
        }
        else if (operation == CandidateAdjustment::Advance)
        {
            if (match == candidates.end() || match == candidates.begin()) return false;
            std::iter_swap(match, match - 1);
        }
        else if (operation == CandidateAdjustment::Top)
        {
            if (match == candidates.begin() && match != candidates.end()) return false;
            auto stored = match == candidates.end() ? text : *match;
            if (match != candidates.end()) candidates.erase(match);
            candidates.insert(candidates.begin(), std::move(stored));
        }
        else return false;
        std::vector<CompactLexicon::Entry> entries;
        entries.reserve(table.Count() + (found ? 0 : 1));
        for (std::uint32_t i = 0; i < table.Count(); ++i)
        {
            std::vector<std::u16string> values;
            if (found && i == *found) values = candidates;
            else for (std::uint32_t j = 0; j < table.CandidateCount(i); ++j) values.push_back(table.Candidate(i, j));
            entries.emplace_back(table.Code(i), std::move(values));
        }
        if (!found) entries.emplace_back(normalized, std::move(candidates));
        auto schema = std::make_shared<SchemaLexicon>(*previous->schema);
        schema->table = CompactLexicon::Build(entries);
        auto metadata = BuildLexiconMetadata(entries);
        schema->metadata.unique = std::move(metadata.unique);
        schema->metadata.nonTerminal = std::move(metadata.nonTerminal);
        schema->metadata.autoShort = std::move(metadata.autoShort);
        schema->metadata.shortSemicolon = metadata.shortSemicolon;
        schema->metadata.shortSlash = metadata.shortSlash;
        schema->metadata.shortBracket = metadata.shortBracket;
        schema->metadata.shortZ = metadata.shortZ;
        // Construction/full-code/comment/split metadata is unchanged by live
        // adjustment in the reference. Pinyin and old snapshots remain shared.
        auto next = std::make_shared<RuntimeLexiconSnapshot>(*previous);
        next->schema = std::move(schema);
        next->generation = previous->generation + 1;
        next->lexiconVersion = previous->lexiconVersion + 1;
        _current.store(next);
        try
        {
            if (next->schemaName.empty()) return true;
            auto directory = next->root / next->schemaName;
            std::filesystem::create_directories(directory);
            std::u16string escaped;
            for (std::size_t i = 0; i < text.size(); ++i)
            {
                auto c = text[i];
                if (c == u'\\') escaped += u"\\\\";
                else if (c == u'\t') escaped += u"\\t";
                else if (c == u' ') escaped += u"\\s";
                else if (c == u'\n') escaped += u"\\n";
                else if (c == u'\r' && i + 1 < text.size() && text[i + 1] == u'\n') { escaped += u"\\n"; ++i; }
                else escaped += c;
            }
            auto prefix = operation == CandidateAdjustment::Add ? u"{添加}" : operation == CandidateAdjustment::Delete ? u"{\u5220\u9664}" : operation == CandidateAdjustment::Top ? u"{\u7f6e\u9876}" : u"{\u524d\u79fb}";
            auto bytes = EncodeUtf8Text(prefix + normalized + u"\t" + escaped + u"\r\n");
            std::ofstream file(directory / u"\u7528\u6237\u8c03\u6574.txt", std::ios::binary | std::ios::app);
            file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        }
        catch (...) {} // Best effort, as in AppendAdjustOperationNoThrow.
        return true;
    }
}
