#pragma once
#include <string>
#include <string_view>
#include <span>
#include <vector>
namespace tiger::core
{
    using SchemaHistory = std::vector<std::u16string>;
    SchemaHistory SeedRecentSchemas(std::u16string_view persisted);
    void RecordRecentSchema(SchemaHistory& history, std::u16string_view name);
    std::u16string SerializeRecentSchemas(const SchemaHistory& history);
    // Schemas must have GetSchemaList's sorted, distinct, canonical order.
    std::u16string ChooseRecentSchema(std::span<const std::u16string> schemas,
        std::u16string_view current, const SchemaHistory& history);
}
