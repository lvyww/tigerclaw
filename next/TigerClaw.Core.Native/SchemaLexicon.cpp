#include "SchemaLexicon.h"
#include "LexiconOrder.h"
#include "LexiconFile.h"
#include "LexiconAssembly.h"
#include "CodeCase.h"

namespace tiger::core
{
    SchemaLexicon LoadSchemaLexicon(const std::filesystem::path& directory, const std::string* locale)
    {
        if (directory.empty()) return {CompactLexicon::Build({}), {}, {}};
        std::vector<LexiconRow> coded, uncoded, constructRows;
        auto constructPath = directory / u"\u6784\u8bcd.txt";
        bool hasConstruct = std::filesystem::is_regular_file(constructPath);
        if (std::filesystem::is_directory(directory))
            for (const auto& path : GetOrderedLexiconFiles(directory, locale))
            {
                auto name = FoldOrdinalCode(path.filename().u16string());
                if (name == FoldOrdinalCode(u"\u8865\u5145\u8bed\u6599.txt")) continue;
                if (hasConstruct && name == FoldOrdinalCode(u"\u6784\u8bcd.txt")) continue;
                ReadLexiconFile(path, [&](LexiconRow row)
                { (row.code.empty() ? uncoded : coded).push_back(std::move(row)); });
            }
        auto initial = AssembleCodedRows(coded);
        if (hasConstruct) ReadLexiconFile(constructPath, [&](LexiconRow row) { constructRows.push_back(std::move(row)); });
        auto construction = BuildConstructCodeMap(constructRows, initial);
        for (const auto& row : constructRows) if (row.code.empty()) uncoded.push_back(row);
        AppendInferredRows(uncoded, construction, coded);
        auto finalEntries = AssembleCodedRows(coded);
        construction = BuildConstructCodeMap(constructRows, finalEntries); // C# computes this before adjustments.
        if (std::filesystem::is_directory(directory))
            LoadAdjustmentFile(finalEntries, directory / u"\u7528\u6237\u8c03\u6574.txt");
        auto metadata = BuildLexiconMetadata(finalEntries);
        metadata.comments = LoadTextMetadata(directory, false);
        metadata.splits = LoadTextMetadata(directory, true);
        auto supplements = std::make_shared<const std::vector<SentenceSupplementEntry>>(LoadSentenceSupplements(directory));
        return {CompactLexicon::Build(finalEntries), std::move(construction), std::move(metadata), std::move(supplements)};
    }
}
