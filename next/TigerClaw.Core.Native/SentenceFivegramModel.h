#pragma once
#include "ReadOnlyMapping.h"
#include "SentenceLmHistory.h"
#include <memory>

namespace tiger::core
{
    // TCSKNM03 Q16/Q8, aligned with the maintained C# reader. A query owns a
    // shared mapping lease and bounded private caches; calls on one query are
    // serialized by its decoder. Independent queries can run concurrently.
    class SentenceFivegramModel
    {
        struct Data;
        std::shared_ptr<const Data> _data;
    public:
        explicit SentenceFivegramModel(const std::filesystem::path& path);
        class Query
        {
            struct Cache;
            std::shared_ptr<const Data> _data;
            std::unique_ptr<Cache> _cache;
        public:
            explicit Query(std::shared_ptr<const Data> data);
            ~Query();
            Query(const Query&) = delete;
            Query& operator=(const Query&) = delete;
            SentenceLmHistory BeginHistory() const;
            double Step(SentenceLmHistory& history, std::u16string_view target);
            bool HasObservedBigram(std::u16string_view a, std::u16string_view b);
        };
        std::unique_ptr<Query> CreateQuery() const;
        int Version() const;
    };
}
