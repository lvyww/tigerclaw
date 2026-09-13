#include "SentenceDecodeWorker.h"
#include "SentenceCompositionSession.h"
#include <semaphore>
#include <iostream>

using namespace tiger::core;
using namespace std::chrono_literals;
namespace
{
    void Check(bool value) { if (!value) throw std::runtime_error("Sentence worker regression"); }
}
int main()
{
    try
    {
        std::binary_semaphore entered(0), release(0);
        std::vector<std::u16string> calls;
        SentenceDecodeWorker worker([&](std::u16string_view raw)
        {
            calls.emplace_back(raw);
            if (raw == u"block" || raw == u"cancel") { entered.release(); release.acquire(); }
            if (raw == u"fail") throw std::runtime_error("injected decode failure");
            auto result = std::make_shared<SentenceLatticeResult>(); result->raw = raw;
            return result;
        });
        worker.Submit(u"block");
        bool started = entered.try_acquire_for(2s);
        if (!started) release.release(); // never strand destruction on failed assertion
        Check(started);
        worker.Submit(u"skipped");
        auto latest = worker.Submit(u"latest");
        release.release();
        Check(worker.WaitIdle(2s));
        auto completed = worker.TakeCompleted();
        Check(completed && completed->generation == latest && completed->result->raw == u"latest" && !completed->error);
        Check(!worker.TakeCompleted());
        Check(calls == std::vector<std::u16string>({u"block", u"latest"}));
        worker.Submit(u"cancel");
        started = entered.try_acquire_for(2s);
        if (!started) release.release();
        Check(started);
        worker.Cancel();
        release.release();
        Check(worker.WaitIdle(2s) && !worker.TakeCompleted());
        auto failureGeneration = worker.Submit(u"fail");
        Check(worker.WaitIdle(2s));
        auto failure = worker.TakeCompleted();
        Check(failure && failure->generation == failureGeneration && failure->error && !failure->result);
        worker.Submit(u"recovered");
        Check(worker.WaitIdle(2s));
        Check(worker.TakeCompleted()->result->raw == u"recovered");
        worker.Submit(u"discard");
        Check(worker.WaitIdle(2s));
        worker.Cancel();
        Check(!worker.TakeCompleted());
        std::binary_semaphore shutdownEntered(0), finish(0), destroyed(0);
        auto closing = std::make_unique<SentenceDecodeWorker>([&](auto raw)
        {
            shutdownEntered.release(); finish.acquire();
            auto result = std::make_shared<SentenceLatticeResult>(); result->raw = raw;
            return result;
        });
        closing->Submit(u"shutdown");
        started = shutdownEntered.try_acquire_for(2s);
        if (!started) finish.release();
        Check(started);
        std::thread destroyer([owned = std::move(closing), &destroyed]() mutable { owned.reset(); destroyed.release(); });
        bool prematurelyDestroyed = destroyed.try_acquire();
        finish.release();
        destroyer.join();
        Check(!prematurelyDestroyed && destroyed.try_acquire());
        std::binary_semaphore snapshotEntered(0), snapshotRelease(0);
        BasicSentenceDecodeWorker<SentenceLatticeResult, SentenceCompositionRequest> snapshotWorker(
            [&](const SentenceCompositionRequest& request)
            {
                snapshotEntered.release(); snapshotRelease.acquire();
                auto result = std::make_shared<SentenceLatticeResult>();
                result->raw = request.raw;
                SentenceBeamState candidate; candidate.text = request.requiredPrefix;
                result->candidates.push_back(candidate);
                return result;
            });
        SentenceCompositionRequest snapshot{42, 9, u"AaBb", u"committed"};
        auto scheduleToken = snapshotWorker.Submit(snapshot);
        started = snapshotEntered.try_acquire_for(2s);
        if (!started) snapshotRelease.release();
        Check(started);
        snapshot.raw = u"edited"; snapshot.requiredPrefix = u"changed"; snapshot.lexiconVersion = 10;
        snapshotRelease.release();
        Check(snapshotWorker.WaitIdle(2s));
        auto snapshotReady = snapshotWorker.TakeCompleted();
        Check(snapshotReady && !snapshotReady->error && snapshotReady->generation == scheduleToken);
        Check(snapshotReady->raw.generation == 42 && snapshotReady->raw.lexiconVersion == 9);
        Check(snapshotReady->raw.raw == u"AaBb" && snapshotReady->raw.requiredPrefix == u"committed");
        Check(snapshotReady->result->raw == u"AaBb" && snapshotReady->result->candidates[0].text == u"committed");
        std::binary_semaphore cancellableEntered(0), canceledCallback(0);
        SentenceDecodeWorker* callbackOwner = nullptr;
        SentenceDecodeWorker cancellable([&](const std::u16string& raw, std::stop_token stop)
        {
            if (raw == u"wait")
            {
                std::stop_callback callback(stop, [&]
                {
                    callbackOwner->TakeCompleted(); // exercises reentry: worker lock must not be held
                    canceledCallback.release();
                });
                cancellableEntered.release(); canceledCallback.acquire();
            }
            auto result = std::make_shared<SentenceLatticeResult>(); result->raw = raw; return result;
        });
        callbackOwner = &cancellable;
        cancellable.Submit(u"wait");
        bool cancellationStarted = cancellableEntered.try_acquire_for(2s);
        cancellable.Submit(u"replacement");
        Check(cancellable.WaitIdle(2s));
        Check(cancellationStarted && cancellable.TakeCompleted()->result->raw == u"replacement");
        std::cout << "Sentence worker latest-only/coalescing/cancel/error tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
