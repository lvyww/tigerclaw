#pragma once
#include "CandidatePage.h"
#include <optional>

namespace tiger::core
{
    enum class RuntimeInputMode { English, Idle, Ordinary, Pinyin, UpperCase, Sentence };

    // Owned values captured on the serialized host thread after one synchronization
    // and at most one async-result Pump. No table/session views escape. This is
    // an input-state projection, not the complete Overlay/IPC wire protocol.
    struct RuntimeInputSnapshot
    {
        std::uint64_t lexiconGeneration = 0;
        std::optional<std::uint64_t> sentenceGeneration;
        std::u16string schemaName;
        RuntimeInputMode mode = RuntimeInputMode::Idle;
        bool isChinese = true;
        bool composing = false;
        bool candidatesCurrent = true;
        bool decodePending = false;
        bool decodeFailed = false;
        bool neuralFailed = false;
        std::u16string raw;
        std::u16string activeCode;
        std::u16string displayCode;
        CandidatePage page;
        int pageSize = 1;
        std::size_t selectedCandidateIndex = 0;
    };
}
