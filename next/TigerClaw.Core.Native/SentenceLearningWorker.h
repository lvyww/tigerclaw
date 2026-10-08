#pragma once
#include "SentenceLearningStore.h"
#include <condition_variable>
#include <deque>

namespace tiger::core
{
    // Owns all journal I/O. Queued corrections are copied; published snapshots
    // are immutable and may be read by the key/decoder threads without I/O.
    class SentenceLearningWorker
    {
        SentenceLearningStore _store;
        mutable std::mutex _mutex;
        std::condition_variable _wake, _idle;
        std::deque<std::function<void()>> _queue;
        bool _stopping = false, _busy = false;
        std::exception_ptr _error;
        std::chrono::steady_clock::time_point _lastRefresh{};
        std::thread _thread;
        void Run()
        {
            std::unique_lock lock(_mutex);
            for (;;)
            {
                _wake.wait(lock, [&] { return _stopping || !_queue.empty(); });
                if (_queue.empty() && _stopping) return;
                auto task = std::move(_queue.front()); _queue.pop_front(); _busy = true;
                lock.unlock();
                std::exception_ptr error;
                try { task(); } catch (...) { error = std::current_exception(); }
                lock.lock(); _error = error; _busy = false; _idle.notify_all();
            }
        }
        bool Queue(std::function<void()> task)
        {
            std::lock_guard lock(_mutex);
            if (_stopping || _queue.size() + (_busy ? 1 : 0) >= 256) return false;
            _queue.push_back(std::move(task)); _wake.notify_one(); return true;
        }
    public:
        explicit SentenceLearningWorker(std::filesystem::path path) : _store(std::move(path)), _thread([this] { Run(); }) {}
        ~SentenceLearningWorker()
        {
            { std::lock_guard lock(_mutex); _stopping = true; _wake.notify_one(); }
            _thread.join(); // shutdown only; drain accepted corrections
        }
        const std::filesystem::path& Path() const { return _store.path(); }
        auto Snapshot() const { return _store.snapshot(); }
        std::exception_ptr LastError() const { std::lock_guard lock(_mutex); return _error; }
        bool Confirm(std::vector<SentenceLearningEvent> events)
        {
            if (events.empty()) return true;
            return Queue([this, events = std::move(events)] { _store.confirm(events); });
        }
        bool Forget(std::u16string mode, std::u16string code, std::u16string text)
        { return Queue([this, mode=std::move(mode), code=std::move(code), text=std::move(text)] { _store.forget(mode, code, text); }); }
        void Refresh()
        {
            {
                std::lock_guard lock(_mutex);
                auto now = std::chrono::steady_clock::now();
                if (now - _lastRefresh < std::chrono::seconds(1)) return;
                _lastRefresh = now;
            }
            Queue([this] { _store.refresh(); });
        }
        bool WaitIdle(std::chrono::milliseconds timeout)
        {
            std::unique_lock lock(_mutex);
            return _idle.wait_for(lock, timeout, [&] { return !_busy && _queue.empty(); });
        }
    };
    class SentenceLearningReceipts
    {
        struct Pending
        {
            std::u16string client;
            std::int64_t tick;
            std::shared_ptr<SentenceLearningWorker> store;
            std::vector<SentenceLearningEvent> events;
        };
        std::mutex _mutex;
        std::map<std::string, Pending> _pending;
        std::function<std::int64_t()> _clock;
        void Prune()
        {
            auto now = _clock();
            std::erase_if(_pending, [&](const auto& p) { return now < p.second.tick || now - p.second.tick > 30000; });
        }
    public:
        explicit SentenceLearningReceipts(std::function<std::int64_t()> clock = {}) : _clock(std::move(clock))
        {
            if (!_clock) _clock = [] { return std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count(); };
        }
        std::string Issue(std::u16string client, std::shared_ptr<SentenceLearningWorker> store, std::vector<SentenceLearningEvent> events)
        {
            if (client.empty() || client.size() > 128 || !store || events.empty()) return {};
            std::lock_guard lock(_mutex); Prune();
            if (_pending.size() >= 128) return {};
            auto token = learningId();
            _pending.emplace(token, Pending{std::move(client), _clock(), std::move(store), std::move(events)});
            return token;
        }
        bool Acknowledge(std::u16string_view client, const std::string& token, bool success)
        {
            std::lock_guard lock(_mutex); Prune();
            auto found = _pending.find(token);
            if (found == _pending.end() || found->second.client != client) return false;
            auto pending = std::move(found->second); _pending.erase(found);
            if (!success) return false;
            for (auto& event : pending.events) event.time = learningNow();
            return pending.store->Confirm(std::move(pending.events));
        }
        void Cancel() { std::lock_guard lock(_mutex); _pending.clear(); }
    };
}
