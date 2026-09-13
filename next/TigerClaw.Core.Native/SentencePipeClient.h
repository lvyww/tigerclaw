#pragma once
#include "SentenceProtocol.h"
#include <stop_token>

namespace tiger::core
{
    // Shutdown requires the PID from a still-owned process handle. PID matching
    // is checked on the connected pipe BEFORE any shutdown bytes are sent.
    void ControlSentencePipe(std::wstring_view shortPipeName, SentenceControlCommand command,
        std::uint64_t sequence, unsigned ownedProcessId = 0, std::stop_token stop = {}, unsigned timeoutMs = 1000);
    // Explicit local pipe name, no default production endpoint or process launch.
    // Throws on transport/validation failure; caller keeps n-gram ordering.
    // A nonzero ownedProcessId verifies server ownership before sending text.
    std::vector<double> ScoreSentencePipe(std::wstring_view shortPipeName,
        const SentenceNeuralRequest& request, std::uint64_t sequence,
        std::stop_token stop = {}, unsigned connectTimeoutMs = 10000, unsigned responseTimeoutMs = 5000,
        unsigned ownedProcessId = 0);
    using SentencePipeScorer = std::function<std::vector<double>(const SentenceNeuralRequest&, std::stop_token)>;
    // Copies of the returned callback retain one endpoint and sequence counter.
    // Ready for RuntimeSentenceInput's cancellable scorer constructor. Does not
    // launch a process or contact the endpoint until the scorer is invoked.
    SentencePipeScorer MakeSentencePipeScorer(std::wstring shortPipeName,
        unsigned connectTimeoutMs = 10000, unsigned responseTimeoutMs = 5000);
}
