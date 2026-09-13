#pragma once
#include "RuntimeSentenceDecoder.h"
#include "SentenceCompositionSession.h"
#include "SentenceDecodeWorker.h"
#include "SentenceServiceLifecycle.h"
#include "RuntimeSentenceInputSettings.h"

namespace tiger::core
{
    // Inner sentence-mode event host. Caller serializes public methods and keeps
    // decoder alive until this object (including its joined worker) is destroyed.
    // No physical modifier capture, output postprocessing, UI or production IPC.
    class RuntimeSentenceInput
    {
    public:
        using NeuralScorer = std::function<std::vector<double>(const SentenceNeuralRequest&)>;
        using CancellableNeuralScorer = std::function<std::vector<double>(const SentenceNeuralRequest&, std::stop_token)>;
    private:
        RuntimeSentenceDecoder& _decoder;
        const RuntimeSentenceInputSettings _settings;
        SentenceCompositionSession _session;
        std::exception_ptr _lastError;
        BasicSentenceDecodeWorker<RuntimeSentenceResult, SentenceCompositionRequest> _worker;
        std::unique_ptr<BasicSentenceDecodeWorker<std::vector<double>, SentenceNeuralRequest>> _neuralWorker;
        SentenceServiceLifecycle* _neuralService = nullptr;
        std::uint64_t _neuralScheduled = 0;
        std::uint64_t _neuralScheduledEpoch = 0;
        std::exception_ptr _lastNeuralError;
        static CancellableNeuralScorer AdaptScorer(NeuralScorer score)
        {
            if (!score) return {};
            return [score = std::move(score)](const SentenceNeuralRequest& request, std::stop_token) { return score(request); };
        }
        void CancelNeural()
        {
            if (_neuralWorker) _neuralWorker->Cancel();
            if (_neuralService) _neuralService->CancelRequests();
            _neuralScheduled = 0; _neuralScheduledEpoch = 0; _lastNeuralError = {};
        }
        void ScheduleNeural()
        {
            if (!_neuralWorker && !_neuralService) return;
            auto epoch = _neuralService ? _neuralService->EnabledEpoch() : std::optional<std::uint64_t>(0);
            if (!epoch) return;
            auto request = _session.NeuralRequest();
            if (!request || (request->composition.generation == _neuralScheduled && *epoch == _neuralScheduledEpoch)) return;
            auto generation = request->composition.generation;
            if (_neuralService)
            {
                auto accepted = _neuralService->Request(std::move(*request));
                if (!accepted) return;
                _neuralScheduledEpoch = *accepted;
            }
            else _neuralWorker->Submit(std::move(*request));
            _neuralScheduled = generation;
        }
        void Schedule()
        {
            CancelNeural();
            if (_session.Context().Raw().empty()) _worker.Cancel();
            else _worker.Submit(_session.Request());
        }
        void EnsureCurrent()
        {
            Pump();
            if (_session.DecodeCurrent()) return;
            auto request = _session.Request();
            auto result = _decoder.DecodeResult(request.raw, _settings.autoCommit, request.requiredPrefix);
            if (_session.Apply(request, *result->lattice, result->evidence)) ScheduleNeural();
            _lastError = {};
        }
    public:
        explicit RuntimeSentenceInput(RuntimeSentenceDecoder& decoder, RuntimeSentenceInputSettings settings = {}, NeuralScorer scorer = {})
            : RuntimeSentenceInput(decoder, settings, AdaptScorer(std::move(scorer))) {}
        RuntimeSentenceInput(RuntimeSentenceDecoder& decoder, RuntimeSentenceInputSettings settings, CancellableNeuralScorer scorer)
            : _decoder(decoder), _settings(settings), _worker([this](const SentenceCompositionRequest& request)
            { return _decoder.DecodeResult(request.raw, _settings.autoCommit, request.requiredPrefix); })
        {
            if (scorer)
                _neuralWorker = std::make_unique<BasicSentenceDecodeWorker<std::vector<double>, SentenceNeuralRequest>>(
                    [score = std::move(scorer)](const SentenceNeuralRequest& request, std::stop_token stop)
                    { return std::make_shared<const std::vector<double>>(score(request, stop)); });
        }
        // Exclusive borrowed service; caller refreshes its eligibility and keeps
        // it alive beyond this input host. No second neural worker is created.
        RuntimeSentenceInput(RuntimeSentenceDecoder& decoder, RuntimeSentenceInputSettings settings,
            SentenceServiceLifecycle& service)
            : RuntimeSentenceInput(decoder, settings, CancellableNeuralScorer{}) { _neuralService = &service; }
        ~RuntimeSentenceInput() { CancelNeural(); }
        // Deferred binding lets a runtime prepare a replacement without touching
        // the live service. Bind before publishing/using the replacement host.
        void AttachNeuralService(SentenceServiceLifecycle& service)
        {
            if (_neuralWorker || _neuralService) throw std::logic_error("Sentence scorer already attached");
            _neuralService = &service;
        }
        const SentenceCompositionSession& Session() const { return _session; }
        std::exception_ptr LastDecodeError() const { return _lastError; }
        std::exception_ptr LastNeuralError() const { return _lastNeuralError; }
        bool Pump()
        {
            auto ready = _worker.TakeCompleted();
            bool applied = false;
            if (ready)
            {
                if (ready->error) _lastError = ready->error;
                else if (_session.Apply(ready->raw, *ready->result->lattice, ready->result->evidence))
                { applied = true; _lastError = {}; ScheduleNeural(); }
            }
            if (_neuralWorker)
            {
                auto neural = _neuralWorker->TakeCompleted();
                if (neural)
                {
                    if (neural->error) _lastNeuralError = neural->error;
                    else if (_session.ApplyNeural(neural->raw, *neural->result, _decoder.AllowsDuplicateSingles()))
                    { applied = true; _lastNeuralError = {}; }
                }
            }
            if (_neuralService)
            {
                auto neural = _neuralService->TakeCompleted();
                if (neural)
                {
                    if (neural->error) _lastNeuralError = neural->error;
                    else if (_session.ApplyNeural(neural->request, neural->scores, _decoder.AllowsDuplicateSingles()))
                    { applied = true; _lastNeuralError = {}; }
                }
            }
            // Eligibility can change without a raw edit or new Beam result.
            // Retry only once per accepted (composition, service epoch).
            if (_neuralService) ScheduleNeural();
            return applied;
        }
        bool WaitIdle(std::chrono::milliseconds timeout) { return _worker.WaitIdle(timeout); }
        bool WaitNeuralIdle(std::chrono::milliseconds timeout)
        {
            if (_neuralService) return _neuralService->WaitIdle(timeout);
            return !_neuralWorker || _neuralWorker->WaitIdle(timeout);
        }
        void Cancel() { _session.Cancel(); _worker.Cancel(); CancelNeural(); _lastError = {}; }
        std::u16string ExportUncommittedRaw() const { return std::u16string(_session.Context().UncommittedRaw()); }
        // Literal exits deliberately ignore Enter-clear and never run a decode.
        std::u16string FinishLiteral()
        {
            auto raw = _session.FinishLiteral();
            _worker.Cancel(); CancelNeural(); _lastError = {};
            return raw;
        }
        void ImportRaw(std::u16string_view raw, std::uint64_t lexiconVersion)
        {
            _session.ReplaceRaw(raw, lexiconVersion);
            _lastError = {};
            Schedule();
        }
        SentenceControlResult KeyDown(int vk, bool shift = false)
        {
            Pump();
            auto generation = _session.Request().generation;
            auto control = _session.ControlKey(vk, shift, _settings.pageSize, _settings.enterClear, _settings.tabClear);
            if (control.needsDecode)
            {
                EnsureCurrent();
                control = _session.ControlKey(vk, shift, _settings.pageSize, _settings.enterClear, _settings.tabClear);
            }
            if (control.handled)
            {
                if (_session.Request().generation != generation) Schedule();
                return control;
            }
            char16_t key = 0;
            if (vk >= 0x41 && vk <= 0x5a) key = static_cast<char16_t>(u'a' + vk - 0x41);
            else if (!shift && vk >= 0x30 && vk <= 0x39) key = static_cast<char16_t>(u'0' + vk - 0x30);
            else if (!shift && vk >= 0x60 && vk <= 0x69) key = static_cast<char16_t>(u'0' + vk - 0x60);
            else if (!shift && vk == 0xba && _settings.semicolonRank) key = u';';
            else if (!shift && vk == 0xde && _settings.quoteRank) key = u'\'';
            if (!key) return {};
            auto commit = _session.AppendWithAutoCommit(key, _settings.autoCommit, _settings.minimumRetained,
                [&](auto raw, auto required, std::optional<std::u16string_view> excluded, bool group)
                { return _decoder.HasCompleteCandidate(raw, required, excluded, group); },
                [&](auto raw) { return _decoder.IsProperCodePrefix(raw); });
            if (_session.Request().generation != generation) Schedule();
            return {true, false, std::move(commit)};
        }
        std::u16string FinishWithPunctuation(std::u16string_view punctuation)
        {
            EnsureCurrent();
            auto result = _session.FinishSelected(punctuation);
            if (!result) throw std::logic_error("Current sentence completion was not applied");
            _worker.Cancel();
            CancelNeural();
            return std::move(*result);
        }
    };
}
