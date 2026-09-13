#include "RecentSchemas.h"
#include "CodeCase.h"
#include "LexiconText.h"
#include <algorithm>
namespace tiger::core
{
    SchemaHistory SeedRecentSchemas(std::u16string_view persisted)
    {
        SchemaHistory history;
        while (!persisted.empty() && history.size() < 2)
        {
            auto separator = persisted.find(u'|');
            auto name = TrimText(persisted.substr(0, separator));
            auto folded = FoldOrdinalCode(name);
            if (!name.empty() && std::none_of(history.begin(), history.end(), [&](const auto& old) { return FoldOrdinalCode(old) == folded; }))
                history.emplace_back(name);
            if (separator == persisted.npos) break;
            persisted.remove_prefix(separator + 1);
        }
        return history;
    }
    void RecordRecentSchema(SchemaHistory& history, std::u16string_view name)
    {
        name = TrimText(name);
        if (name.empty()) return;
        // Own before erasing: callers may pass a view into the existing history.
        std::u16string owned(name);
        auto folded = FoldOrdinalCode(owned);
        std::erase_if(history, [&](const auto& old) { return FoldOrdinalCode(old) == folded; });
        history.insert(history.begin(), std::move(owned));
        if (history.size() > 2) history.resize(2);
    }
    std::u16string SerializeRecentSchemas(const SchemaHistory& history)
    {
        std::u16string value;
        for (std::size_t index = 0; index < history.size(); ++index)
        {
            if (index) value.push_back(u'|');
            value += history[index];
        }
        return value;
    }
    std::u16string ChooseRecentSchema(std::span<const std::u16string> schemas,
        std::u16string_view current, const SchemaHistory& history)
    {
        if (schemas.size() < 2) return {};
        auto currentKey = FoldOrdinalCode(TrimText(current));
        for (const auto& recent : history)
        {
            auto key = FoldOrdinalCode(recent);
            if (key == currentKey) continue;
            for (const auto& schema : schemas) if (FoldOrdinalCode(schema) == key) return schema;
        }
        std::size_t next = 0;
        for (std::size_t index = 0; index < schemas.size(); ++index)
            if (FoldOrdinalCode(schemas[index]) == currentKey) { next = (index + 1) % schemas.size(); break; }
        return FoldOrdinalCode(schemas[next]) == currentKey ? std::u16string{} : schemas[next];
    }
}
