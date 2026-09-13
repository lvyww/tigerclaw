#include "KeyRequestReplayCache.h"
#include "LexiconText.h"

namespace tiger::core
{
    std::u16string KeyRequestReplayCache::BuildKey(std::u16string_view session, std::u16string_view event)
    {
        auto blank = [](std::u16string_view text) { return std::all_of(text.begin(), text.end(), IsDotNetWhiteSpace); };
        if (blank(session) || blank(event)) return {};
        return std::u16string(session) + u'\n' + std::u16string(event);
    }
    std::string KeyRequestReplayCache::RewriteSequence(const std::string& response, std::int32_t sequence)
    {
        constexpr std::string_view marker = "\"seq\":";
        auto start = response.find(marker);
        if (start == std::string::npos) return response;
        start += marker.size();
        auto end = response.find(',', start);
        if (end == std::string::npos) end = response.find('}', start);
        if (end == std::string::npos) return response;
        return response.substr(0, start) + std::to_string(sequence) + response.substr(end);
    }
    std::optional<std::string> KeyRequestReplayCache::GetLocked(const std::u16string& key, std::int32_t sequence) const
    {
        if (key.empty()) return std::nullopt;
        auto found = responses_.find(key);
        if (found == responses_.end()) return std::nullopt;
        return RewriteSequence(found->second, sequence);
    }
    void KeyRequestReplayCache::StoreLocked(const std::u16string& key, const std::string& response)
    {
        if (key.empty() || response.empty()) return;
        auto found = responses_.find(key);
        if (found != responses_.end()) { found->second = response; return; }
        // Queue/map updates are rolled back together if allocation fails.
        order_.push_back(key);
        try { responses_.emplace(key, response); }
        catch (...) { order_.pop_back(); throw; }
        while (responses_.size() > capacity_)
        {
            responses_.erase(order_.front()); order_.pop_front();
        }
    }
    std::optional<std::string> KeyRequestReplayCache::Get(const std::u16string& key, std::int32_t sequence) const
    {
        std::lock_guard lock(mutex_);
        return GetLocked(key, sequence);
    }
    void KeyRequestReplayCache::Store(const std::u16string& key, const std::string& response)
    {
        std::lock_guard lock(mutex_);
        StoreLocked(key, response);
    }
    std::string KeyRequestReplayCache::Execute(const std::u16string& key, std::int32_t sequence,
        const std::function<std::string()>& execute)
    {
        std::lock_guard lock(mutex_);
        if (auto cached = GetLocked(key, sequence)) return *cached;
        auto response = execute();
        StoreLocked(key, response);
        return response;
    }
}
