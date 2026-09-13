#pragma once
#include "SentenceLattice.h"
#include <chrono>
#include <condition_variable>
#include <exception>
#include <limits>
#include <mutex>
#include <optional>
#include <thread>
#include <stop_token>

namespace tiger::core
{
    template<class Result, class Input = std::u16string>
    struct BasicSentenceDecodeCompletion
    {
        std::uint64_t generation;
        Input raw; // owned input snapshot; retained name for the raw-string API
        std::shared_ptr<const Result> result;
        std::exception_ptr error;
    };
    // One serialized worker, one coalescing pending slot, one consumable result.
    // The provider must own/retain its decoder and must not call back into this
    // object's destruction. Running synchronous decodes are drained on shutdown.
    template<class Result, class Input = std::u16string>
    class BasicSentenceDecodeWorker
    {
    public:
        using Completion = BasicSentenceDecodeCompletion<Result, Input>;
        using Decode = std::function<std::shared_ptr<const Result>(const Input&)>;
        using CancellableDecode = std::function<std::shared_ptr<const Result>(const Input&, std::stop_token)>;
        explicit BasicSentenceDecodeWorker(Decode decode)
            : BasicSentenceDecodeWorker(Adapt(std::move(decode))) {}
        explicit BasicSentenceDecodeWorker(CancellableDecode decode) : _decode(std::move(decode))
        {
            if (!_decode) throw std::invalid_argument("Sentence decode provider is required");
            _thread = std::thread([this] { Run(); });
        }
        ~BasicSentenceDecodeWorker()
        {
            std::stop_source canceled(std::nostopstate);
            { std::lock_guard lock(_mutex); _stopping = true; _pending.reset(); _completed.reset(); canceled = _stopSource; }
            canceled.request_stop(); // callbacks never execute under the worker lock
            _changed.notify_all();
            if (_thread.joinable()) _thread.join();
        }
        BasicSentenceDecodeWorker(const BasicSentenceDecodeWorker&) = delete;
        BasicSentenceDecodeWorker& operator=(const BasicSentenceDecodeWorker&) = delete;
        std::uint64_t Submit(Input raw)
        {
            std::stop_source next, canceled(std::nostopstate);
            std::uint64_t generation;
            {
                std::lock_guard lock(_mutex);
                generation = NextGeneration();
                canceled = _stopSource; _stopSource = std::move(next);
                _pending = Request{generation, std::move(raw), _stopSource.get_token()};
                _completed.reset();
            }
            canceled.request_stop();
            _changed.notify_all();
            return generation;
        }
        void Cancel()
        {
            std::stop_source canceled(std::nostopstate);
            {
                std::lock_guard lock(_mutex);
                NextGeneration();
                _pending.reset(); _completed.reset(); canceled = _stopSource;
            }
            canceled.request_stop();
            _changed.notify_all();
        }
        std::optional<Completion> TakeCompleted()
        {
            // Host must still compare generation under its composition lock
            // when applying a taken result: a later Submit may race that apply.
            std::lock_guard lock(_mutex);
            auto result = std::move(_completed);
            _completed.reset();
            return result;
        }
        bool WaitIdle(std::chrono::milliseconds timeout)
        {
            std::unique_lock lock(_mutex);
            return _changed.wait_for(lock, timeout, [&] { return !_running && !_pending; });
        }
    private:
        static CancellableDecode Adapt(Decode decode)
        {
            if (!decode) throw std::invalid_argument("Sentence decode provider is required");
            return [decode = std::move(decode)](const Input& input, std::stop_token) { return decode(input); };
        }
        struct Request { std::uint64_t generation; Input raw; std::stop_token stop; };
        std::uint64_t NextGeneration()
        {
            if (_generation == std::numeric_limits<std::uint64_t>::max()) throw std::overflow_error("Sentence generation exhausted");
            return ++_generation;
        }
        void Run()
        {
            for (;;)
            {
                Request request;
                {
                    std::unique_lock lock(_mutex);
                    _changed.wait(lock, [&] { return _stopping || _pending.has_value(); });
                    if (_stopping) return;
                    request = std::move(*_pending);
                    _pending.reset();
                    _running = true;
                }
                Completion completion{request.generation, std::move(request.raw), {}, {}};
                try
                {
                    completion.result = _decode(completion.raw, request.stop);
                    if (!completion.result) throw std::runtime_error("Sentence decoder returned no result");
                }
                catch (...) { completion.error = std::current_exception(); }
                {
                    std::lock_guard lock(_mutex);
                    if (!_stopping && completion.generation == _generation) _completed = std::move(completion);
                    _running = false;
                }
                _changed.notify_all();
            }
        }
        CancellableDecode _decode;
        std::stop_source _stopSource;
        std::mutex _mutex;
        std::condition_variable _changed;
        std::uint64_t _generation = 0;
        bool _running = false, _stopping = false;
        std::optional<Request> _pending;
        std::optional<Completion> _completed;
        std::thread _thread;
    };
    using SentenceDecodeCompletion = BasicSentenceDecodeCompletion<SentenceLatticeResult>;
    using SentenceDecodeWorker = BasicSentenceDecodeWorker<SentenceLatticeResult>;
}
