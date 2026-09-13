#pragma once
#include "ReadOnlyMapping.h"
#include "SentenceNgram.h"
#include "CachedSentenceNgram.h"

namespace tiger::core
{
    class MappedSentenceNgram
    {
    public:
        explicit MappedSentenceNgram(const std::filesystem::path& path)
            : _mapping(path, std::uint64_t{4} << 30), _model(_mapping.Bytes()), _cached(_model) {}
        const CachedSentenceNgram& Model() const { return _cached; }
        const SentenceNgram& UncachedModel() const { return _model; }
    private:
        ReadOnlyMapping _mapping; // declared first: outlives model on destruction/failure
        SentenceNgram _model;
        CachedSentenceNgram _cached;
    };
}
