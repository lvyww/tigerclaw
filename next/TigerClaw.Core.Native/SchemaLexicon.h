#pragma once
#include "WordConstruction.h"
#include "LexiconMetadata.h"
#include "SentenceSupplementFile.h"
#include <memory>
#include <filesystem>
namespace tiger::core
{
    // Main table, construction, display/prefix metadata and immutable supplements.
    // Pinyin remains runtime-owned rather than schema-owned.
    struct SchemaLexicon
    {
        CompactLexicon table;
        ConstructCodeMap construction;
        LexiconMetadata metadata;
        std::shared_ptr<const std::vector<SentenceSupplementEntry>> supplements =
            std::make_shared<const std::vector<SentenceSupplementEntry>>();
    };
    SchemaLexicon LoadSchemaLexicon(const std::filesystem::path& directory, const std::string* locale = nullptr);
}
