#pragma once
#include <filesystem>
#include <stop_token>
#include <cstdint>
#include "SentenceProtocol.h"

namespace tiger::core
{
    // Serialized ownership; no process-name lookup or attachment to an existing
    // process. The owner must outlive any scorer using the corresponding pipe.
    class OwnedSentenceProcess
    {
        std::filesystem::path _executable, _model;
        std::wstring _pipe;
        void* _process = nullptr;
        std::uint64_t _sequence = 0;
        std::uint64_t NextSequence();
    public:
        OwnedSentenceProcess(std::filesystem::path executable, std::filesystem::path model, std::wstring pipe);
        ~OwnedSentenceProcess();
        OwnedSentenceProcess(const OwnedSentenceProcess&) = delete;
        OwnedSentenceProcess& operator=(const OwnedSentenceProcess&) = delete;
        bool Running() const;
        unsigned ProcessId() const;
        void Start();
        void Preload(std::stop_token stop = {}, unsigned timeoutMs = 10000);
        std::vector<double> Score(const SentenceNeuralRequest& request, std::stop_token stop = {},
            unsigned connectTimeoutMs = 10000, unsigned responseTimeoutMs = 5000);
        void Stop(unsigned timeoutMs = 1000);
    };
}
