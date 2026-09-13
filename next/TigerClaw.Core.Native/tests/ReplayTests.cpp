#include "KeyRequestReplayCache.h"
#include <atomic>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

using tiger::core::KeyRequestReplayCache;
static void Check(bool ok) { if (!ok) throw std::runtime_error("replay assertion failed"); }
int main()
{
    try
    {
        KeyRequestReplayCache cache(1); // C# clamps to 16.
        for (char16_t c = u'a'; c <= u'p'; ++c) cache.Store(std::u16string(1, c), "{\"seq\":1,\"commit_text\":\"x\"}");
        Check(cache.Get(u"a", 91) == "{\"seq\":91,\"commit_text\":\"x\"}");
        cache.Store(u"a", "{\"seq\":3}");
        cache.Store(u"q", "{\"seq\":4}");
        Check(!cache.Get(u"a", 0)); // Access/overwrite did not move oldest key.
        Check(cache.Get(u"b", -1).has_value());
        Check(KeyRequestReplayCache::BuildKey(u"\u3000", u"1").empty());
        Check(KeyRequestReplayCache::BuildKey(u" s ", u"1") == u" s \n1");
        std::atomic<int> executed = 0;
        KeyRequestReplayCache concurrent;
        std::vector<std::thread> threads;
        std::atomic<bool> success = true;
        for (int i = 0; i < 16; ++i) threads.emplace_back([&, i]
        {
            try
            {
                auto response = concurrent.Execute(u"session\nevent", i, [&]
                { ++executed; return "{\"seq\":" + std::to_string(i) + ",\"commit_text\":\"one\"}"; });
                if (response != "{\"seq\":" + std::to_string(i) + ",\"commit_text\":\"one\"}") success = false;
            }
            catch (...) { success = false; }
        });
        for (auto& thread : threads) thread.join();
        Check(success && executed == 1);
        try { concurrent.Execute(u"error", 1, []() -> std::string { throw std::runtime_error("injected"); }); }
        catch (const std::runtime_error&) {}
        Check(!concurrent.Get(u"error", 2));
        Check(concurrent.Execute(u"error", 2, [] { return "{\"seq\":2}"; }) == "{\"seq\":2}");
        std::cout << "Native replay FIFO/concurrency tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
