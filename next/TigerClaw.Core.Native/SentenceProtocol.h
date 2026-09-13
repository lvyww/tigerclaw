#pragma once
#include "SentenceCompositionSession.h"

namespace tiger::core
{
    enum class SentenceControlCommand { Hello, Shutdown };
    std::string EncodeSentenceControl(SentenceControlCommand command, std::uint64_t sequence);
    bool DecodeSentenceControl(std::string_view line, std::uint64_t sequence);
    // UTF-8 JSON-lines codec, independent of transport and process ownership.
    std::string EncodeSentenceRerank(const SentenceNeuralRequest& request, std::uint64_t sequence);
    std::optional<std::vector<double>> DecodeSentenceRerank(std::string_view line,
        const SentenceNeuralRequest& request, std::uint64_t sequence);
}
