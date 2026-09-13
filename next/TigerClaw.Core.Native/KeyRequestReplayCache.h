#pragma once
#include <algorithm>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>

namespace tiger::core
{
    // FIFO, not LRU: reads/overwrites must not extend a physical event's life.
    // The key handler must serialize lookup + execution + store. Execute() does
    // so, matching ProtocolHandler._keyRequestLock, including exception behavior.
    class KeyRequestReplayCache final
    {
        mutable std::mutex mutex_;
        std::unordered_map<std::u16string, std::string> responses_;
        std::deque<std::u16string> order_;
        std::size_t capacity_;
        static std::string RewriteSequence(const std::string& response, std::int32_t sequence);
        std::optional<std::string> GetLocked(const std::u16string& key, std::int32_t sequence) const;
        void StoreLocked(const std::u16string& key, const std::string& response);
    public:
        explicit KeyRequestReplayCache(int capacity = 512) : capacity_(std::max(16, capacity)) {}
        static std::u16string BuildKey(std::u16string_view session, std::u16string_view event);
        std::optional<std::string> Get(const std::u16string& key, std::int32_t sequence) const;
        void Store(const std::u16string& key, const std::string& response);
        // Callback runs under the request lock, must not reenter this cache.
        std::string Execute(const std::u16string& key, std::int32_t sequence, const std::function<std::string()>& execute);
    };
}
