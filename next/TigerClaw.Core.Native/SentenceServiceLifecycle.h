#pragma once
#include "SentenceCompositionSession.h"
#include <condition_variable>
#include <chrono>
#include <exception>
#include <functional>
#include <mutex>
#include <stop_token>
#include <thread>

namespace tiger::core
{
    // Providers run serially and must outlive this object. No idle unloading.
    // Release must eventually succeed; failed cleanup blocks subsequent loads.
    // Callers must not destroy the lifecycle from a provider/stop callback.
    class SentenceServiceLifecycle
    {
    public:
        using Load = std::function<void(std::stop_token)>;
        using Release = std::function<void()>;
        using Score = std::function<std::vector<double>(const SentenceNeuralRequest&, std::stop_token)>;
        struct Completion
        {
            SentenceNeuralRequest request;
            std::vector<double> scores;
            std::exception_ptr error;
        };
        SentenceServiceLifecycle(Load load, Release release, Score score)
            : _load(std::move(load)), _release(std::move(release)), _score(std::move(score))
        {
            if (!_load || !_release || !_score) throw std::invalid_argument("Sentence lifecycle providers required");
            _thread = std::thread([this] { Run(); });
        }
        ~SentenceServiceLifecycle()
        {
            std::stop_source canceled(std::nostopstate);
            {
                std::lock_guard lock(_mutex);
                _stopping = true; _enabled = false; _loadPending = false;
                _releasePending |= _mayBeLoaded;
                _pending.reset(); _completed.reset(); canceled = _active;
            }
            canceled.request_stop(); _changed.notify_all(); _thread.join();
        }
        SentenceServiceLifecycle(const SentenceServiceLifecycle&) = delete;
        SentenceServiceLifecycle& operator=(const SentenceServiceLifecycle&) = delete;
        void SetEnabled(bool enabled)
        {
            std::stop_source canceled(std::nostopstate);
            {
                std::lock_guard lock(_mutex);
                if (_stopping || _enabled == enabled) return;
                ++_epoch; _enabled = enabled; _loadPending = enabled;
                if (!enabled) _releasePending |= _mayBeLoaded;
                _pending.reset(); _completed.reset(); canceled = _active;
            }
            canceled.request_stop(); _changed.notify_all();
        }
        std::optional<std::uint64_t> EnabledEpoch()
        {
            std::lock_guard lock(_mutex);
            return !_stopping && _enabled ? std::optional<std::uint64_t>(_epoch) : std::nullopt;
        }
        std::optional<std::uint64_t> Request(SentenceNeuralRequest request)
        {
            std::lock_guard lock(_mutex);
            if (_stopping || !_enabled) return {};
            _pending = std::move(request); _completed.reset(); ++_requestSerial;
            _changed.notify_all();
            return _epoch;
        }
        // Composition cancellation is not a residency change. A preload may
        // finish normally; only the active score and queued results are canceled.
        void CancelRequests()
        {
            std::stop_source canceled(std::nostopstate);
            {
                std::lock_guard lock(_mutex);
                ++_requestSerial; _pending.reset(); _completed.reset();
                if (_scoring) canceled = _active;
            }
            canceled.request_stop(); _changed.notify_all();
        }
        std::optional<Completion> TakeCompleted()
        {
            std::lock_guard lock(_mutex);
            auto result = std::move(_completed); _completed.reset(); return result;
        }
        bool WaitIdle(std::chrono::milliseconds timeout)
        {
            std::unique_lock lock(_mutex);
            return _changed.wait_for(lock, timeout, [this]
            { return !_busy && !_loadPending && !_releasePending && !_pending; });
        }
    private:
        Load _load;
        Release _release;
        Score _score;
        std::mutex _mutex;
        std::condition_variable _changed;
        bool _enabled = false, _stopping = false, _busy = false, _scoring = false;
        bool _loadPending = false, _releasePending = false, _mayBeLoaded = false;
        std::uint64_t _epoch = 0, _requestSerial = 0;
        std::optional<SentenceNeuralRequest> _pending;
        std::optional<Completion> _completed;
        std::stop_source _active{std::nostopstate};
        std::thread _thread;
        void Run()
        {
            for (;;)
            {
                std::unique_lock lock(_mutex);
                _changed.wait(lock, [this] { return _stopping || _releasePending || _loadPending || _pending; });
                if (_stopping && !_releasePending) return;
                bool release = _releasePending, load = !release && _loadPending;
                auto epoch = _epoch, serial = _requestSerial;
                std::optional<SentenceNeuralRequest> request;
                if (release) _releasePending = false;
                else
                {
                    _mayBeLoaded = true; _active = std::stop_source();
                    if (load) _loadPending = false;
                    else { request = std::move(_pending); _pending.reset(); }
                }
                auto token = _active.get_token(); _busy = true; _scoring = request.has_value();
                lock.unlock();
                std::vector<double> scores;
                std::exception_ptr error;
                try
                {
                    if (release) _release();
                    else if (load) _load(token);
                    else scores = _score(*request, token);
                }
                catch (...) { error = std::current_exception(); }
                lock.lock();
                if (release)
                {
                    if (error)
                    {
                        _releasePending = true;
                        // Cleanup retry only, not an idle/residency timer. Keep
                        // busy true until this retry delay has finished.
                        _changed.wait_for(lock, std::chrono::milliseconds(250), [] { return false; });
                    }
                    else { _mayBeLoaded = false; _releasePending = false; }
                }
                else if (request && !_stopping && _enabled && epoch == _epoch &&
                    serial == _requestSerial && !token.stop_requested())
                    _completed = Completion{std::move(*request), std::move(scores), error};
                _active = std::stop_source(std::nostopstate); _busy = false; _scoring = false;
                _changed.notify_all();
            }
        }
    };
}
