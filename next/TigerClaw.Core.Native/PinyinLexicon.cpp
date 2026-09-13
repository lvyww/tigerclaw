#include "PinyinLexicon.h"
#include "LexiconFile.h"
#include "LexiconAssembly.h"
#include "CodeCase.h"

namespace tiger::core
{
    CompactLexicon LoadPinyinLexicon(const std::filesystem::path& executableDirectory)
    {
        auto root = std::filesystem::absolute(executableDirectory / u"\u62fc\u97f3\u53cd\u67e5\u7801\u8868").lexically_normal();
        std::vector<LexiconRow> rows;
        if (std::filesystem::is_directory(root))
            for (const auto& file : std::filesystem::directory_iterator(root))
            {
                if (file.is_directory() || FoldOrdinalCode(file.path().extension().u16string()) != u".TXT") continue;
                // Pinyin deliberately retains raw file enumeration order, not
                // schema-name/CurrentCulture ordering, and does not load YAML.
                ReadLexiconFile(file.path(), [&](LexiconRow row)
                { if (!row.code.empty()) rows.push_back(std::move(row)); });
            }
        auto entries = AssembleCodedRows(rows);
        return CompactLexicon::Build(entries);
    }
}
