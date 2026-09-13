#pragma once
#include "OwnedSentenceProcess.h"
#include "SentenceServiceLifecycle.h"
#include "SentenceEligibility.h"

namespace tiger::core
{
    // The lifecycle is destroyed FIRST, draining all providers and releasing
    // the process before the retained process owner itself is destroyed.
    class OwnedSentenceService
    {
        OwnedSentenceProcess _process;
        SentenceServiceLifecycle _lifecycle;
    public:
        OwnedSentenceService(std::filesystem::path executable, std::filesystem::path model, std::wstring pipe,
            unsigned connectTimeoutMs = 10000, unsigned responseTimeoutMs = 5000, unsigned releaseTimeoutMs = 1000)
            : _process(std::move(executable), std::move(model), std::move(pipe)),
              _lifecycle(
                  [this, connectTimeoutMs](std::stop_token stop) { _process.Preload(stop, connectTimeoutMs); },
                  [this, releaseTimeoutMs] { _process.Stop(releaseTimeoutMs); },
                  [this, connectTimeoutMs, responseTimeoutMs](const SentenceNeuralRequest& request, std::stop_token stop)
                  { return _process.Score(request, stop, connectTimeoutMs, responseTimeoutMs); }) {}
        void SetEligible(bool sentenceEnabled, bool neuralEnabled)
        { _lifecycle.SetEnabled(sentenceEnabled && neuralEnabled); }
        void RefreshConfiguration(const ConfigValues& config, std::u16string_view schema)
        { _lifecycle.SetEnabled(GetSentenceEligibility(config, schema).Resident()); }
        void Request(SentenceNeuralRequest request) { _lifecycle.Request(std::move(request)); }
        auto TakeCompleted() { return _lifecycle.TakeCompleted(); }
        // One input host at a time; it must be destroyed before this service.
        SentenceServiceLifecycle& Lifecycle() { return _lifecycle; }
        bool WaitIdle(std::chrono::milliseconds timeout) { return _lifecycle.WaitIdle(timeout); }
    };
}
