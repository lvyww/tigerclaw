#include "SentenceServiceLifecycle.h"
#include "SentenceEligibility.h"
#include "RuntimeSentenceInputSettings.h"
#include "RuntimeSentenceSettings.h"
#include <atomic>
#include <iostream>
#include <semaphore>

using namespace tiger::core;
using namespace std::chrono_literals;
namespace { void Check(bool value) { if (!value) throw std::runtime_error("Sentence lifecycle regression"); } }
int main()
{
    try
    {
        const auto schema = u"test\u6574\u53e5";
        const std::u16string automatic = u"\u81ea\u52a8\u542f\u7528\u6574\u53e5\u6a21\u5f0f";
        const std::u16string neural = u"\u6574\u53e5\u795e\u7ecf\u91cd\u6392";
        Check(GetSentenceEligibility({}, schema).Resident());
        Check(!GetSentenceEligibility({}, u"test").Resident());
        Check(!GetSentenceEligibility({}, u"").input);
        Check(!GetSentenceEligibility({{automatic, u"false"}}, schema).input);
        auto beamOnly = GetSentenceEligibility({{neural, u"false"}}, schema);
        Check(beamOnly.input && !beamOnly.Resident());
        Check(GetSentenceEligibility({{automatic, u"false"}, {automatic, u"true"}, {neural, u"invalid"}}, schema).Resident());
        Check(GetSentenceEligibility({{automatic, u""}, {neural, u""}}, schema).Resident());
        auto defaults = LoadSentenceInputSettings({});
        Check(!defaults.autoCommit && !defaults.enterClear && defaults.tabClear && defaults.pageSize == 5);
        Check(defaults.semicolonRank && defaults.quoteRank && defaults.minimumRetained == 0);
        const std::u16string retained = u"\u4fdd\u7559\u6700\u5c11\u7f16\u7801\u6570\u91cf";
        auto configured = LoadSentenceInputSettings({
            {u"\u6574\u53e5\u81ea\u52a8\u63d0\u524d\u4e0a\u5c4f", u"true"},
            {u"\u56de\u8f66\u6e05\u5c4f", u"true"}, {u"TAB\u6e05\u5c4f", u"false"},
            {u"\u5206\u53f7\u6b21\u9009", u"false"}, {u"\u5f15\u53f7\u4e09\u9009", u"false"},
            {u"\u6bcf\u9875\u5019\u9009\u4e2a\u6570", u"99"}, {retained, u"99"}});
        Check(configured.autoCommit && configured.enterClear && !configured.tabClear);
        Check(!configured.semicolonRank && !configured.quoteRank && configured.pageSize == 10 && configured.minimumRetained == 32);
        Check(LoadSentenceInputSettings({{retained, u"-1"}}).minimumRetained == 0);
        Check(LoadSentenceInputSettings({{retained, u"12"}, {retained, u"2147483648"}}).minimumRetained == 0);
        Check(LoadSentenceInputSettings({{retained, u"plus7"}}, u"plus", u"minus").minimumRetained == 7);
        const std::u16string optimal = u"\u9ad8\u9891\u5b57\u4ec5\u4f7f\u7528\u6700\u4f18\u7801\u7ec4\u53e5";
        const std::u16string white = u"\u6574\u53e5\u5141\u8bb8\u5168\u7801\u7ec4\u53e5\u767d\u540d\u5355";
        const std::u16string duplicates = u"\u5141\u8bb8\u5355\u5b57\u91cd\u7801\u7ec4\u53e5";
        auto decoderDefaults = LoadSentenceSettings({});
        Check(decoderDefaults.optimalCodeHighFrequencyLimit == 1500 && decoderDefaults.lattice.duplicateSingles);
        Check(decoderDefaults.fullCodeWhitelist.contains(u"\u4fbf") && decoderDefaults.fullCodeWhitelist.contains(u"\u7ed5"));
        for (auto value : {u"", u"-1", u"invalid", u"2147483648"})
            Check(LoadSentenceSettings({{optimal, value}}).optimalCodeHighFrequencyLimit == 0);
        auto decoderConfigured = LoadSentenceSettings({{optimal, u"\u3000+42\u3000"}, {duplicates, u"false"},
            {white, u" A\u0301 \U00020000\r\nA\u0301 "}});
        Check(decoderConfigured.optimalCodeHighFrequencyLimit == 42 && !decoderConfigured.lattice.duplicateSingles);
        Check(decoderConfigured.fullCodeWhitelist.size() == 2 && decoderConfigured.fullCodeWhitelist.contains(u"A\u0301") &&
            decoderConfigured.fullCodeWhitelist.contains(u"\U00020000"));
        Check(LoadSentenceSettings({{white, u""}}).fullCodeWhitelist.empty());
        std::atomic<int> loads = 0, releases = 0, scores = 0;
        std::binary_semaphore scoring(0), canceled(0), resume(0);
        bool loaded = false;
        SentenceNeuralRequest request{};
        {
            SentenceServiceLifecycle service(
                [&](std::stop_token) { Check(!loaded); loaded = true; ++loads; },
                [&] { Check(loaded); loaded = false; ++releases; },
                [&](const SentenceNeuralRequest&, std::stop_token token)
                {
                    Check(loaded);
                    if (++scores == 1)
                    {
                        std::stop_callback onStop(token, [&] { canceled.release(); });
                        scoring.release(); resume.acquire();
                    }
                    return std::vector<double>{-1};
                });
            service.Request(request); Check(service.WaitIdle(2s) && scores == 0);
            service.SetEnabled(true); Check(service.WaitIdle(2s) && loads == 1);
            service.SetEnabled(true); Check(service.WaitIdle(2s) && loads == 1 && releases == 0);
            service.Request(request);
            if (!scoring.try_acquire_for(2s)) { resume.release(); throw std::runtime_error("Score did not start"); }
            service.SetEnabled(false);
            bool cancellationObserved = canceled.try_acquire_for(2s);
            service.SetEnabled(true); resume.release();
            Check(cancellationObserved && service.WaitIdle(2s));
            Check(loads == 2 && releases == 1 && !service.TakeCompleted());
            service.Request(request); Check(service.WaitIdle(2s));
            auto result = service.TakeCompleted();
            Check(result && !result->error && result->scores == std::vector<double>{-1});
            Check(!service.TakeCompleted());
            service.SetEnabled(false); Check(service.WaitIdle(2s) && releases == 2);
            service.SetEnabled(true); Check(service.WaitIdle(2s) && loads == 3);
        }
        Check(releases == 3 && !loaded);
        // A failed cleanup must be retried before another preload can run.
        std::atomic<int> attempts = 0;
        {
            SentenceServiceLifecycle service(
                [&](std::stop_token) { Check(!loaded); loaded = true; },
                [&]
                {
                    if (++attempts == 1) throw std::runtime_error("Injected cleanup failure");
                    loaded = false;
                },
                [](const SentenceNeuralRequest&, std::stop_token) { return std::vector<double>{}; });
            service.SetEnabled(true); Check(service.WaitIdle(2s));
            service.SetEnabled(false); service.SetEnabled(true);
            Check(service.WaitIdle(2s) && attempts == 2 && loaded);
        }
        Check(!loaded && attempts == 3);
        std::cout << "Sentence lifecycle residency/cancel/reload/stale/release retry tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
