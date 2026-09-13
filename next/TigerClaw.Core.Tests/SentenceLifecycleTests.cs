using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void RunSentenceLifecycleTests()
        {
            var state = new CoreRuntimeState();
            state.TrySetConfigValue("自动启用整句模式", "是", out _, out _);
            state.TrySetConfigValue("整句神经重排", "是", out _, out _);
            state.TrySetConfigValue("当前码表", "虎码", out _, out _);
            True(!SentenceRerankClient.IsEligible(state), "ordinary schema releases Sentence");
            state.TrySetConfigValue("当前码表", "虎整句", out _, out _);
            True(SentenceRerankClient.IsEligible(state), "sentence schema enables Sentence");
            state.TrySetConfigValue("整句神经重排", "否", out _, out _);
            True(!SentenceRerankClient.IsEligible(state), "neural switch disables Sentence");
            state.TrySetConfigValue("整句神经重排", "是", out _, out _);
            state.TrySetConfigValue("自动启用整句模式", "否", out _, out _);
            True(!SentenceRerankClient.IsEligible(state), "sentence switch disables Sentence");
            var operations = new ConcurrentQueue<string>();
            int loads = 0, releases = 0, completions = 0;
            bool scoringCancelled = false;
            using var scoring = new ManualResetEventSlim();
            using var finishScore = new ManualResetEventSlim();
            using var service = new SentenceServiceLifecycle(
                token => { operations.Enqueue("load"); Interlocked.Increment(ref loads); },
                () => { operations.Enqueue("release"); Interlocked.Increment(ref releases); },
                (request, token) =>
                {
                    if (request.Generation == 1)
                    {
                        scoring.Set();
                        finishScore.Wait(5000);
                        scoringCancelled = token.IsCancellationRequested;
                    }
                    return new[] { 1.0 };
                },
                (generation, raw, scores) => Interlocked.Increment(ref completions));
            void WaitFor(Func<bool> condition, string reason) =>
                True(SpinWait.SpinUntil(condition, 5000), reason);

            service.SetEnabled(false);
            service.Request(new SentenceRerankRequest { Generation = 0 });
            True(loads == 0, "disabled service must not preload");
            service.SetEnabled(true);
            WaitFor(() => Volatile.Read(ref loads) == 1, "eligible service preloads");
            service.SetEnabled(true);
            service.Request(new SentenceRerankRequest { Generation = 1, RawCode = "ab" });
            True(scoring.Wait(5000), "score started");
            service.Request(new SentenceRerankRequest { Generation = 2 });
            service.SetEnabled(false);
            service.SetEnabled(true);
            finishScore.Set();
            WaitFor(() => Volatile.Read(ref loads) == 2, "rapid re-enable reloads after release");
            True(operations.ToArray().SequenceEqual(new[] { "load", "release", "load" }),
                "release barrier precedes reload");
            True(completions == 0, "old scoring and queued work must be discarded");
            True(scoringCancelled, "disabled in-flight scoring must be cancelled");
            service.Request(new SentenceRerankRequest { Generation = 3 });
            WaitFor(() => Volatile.Read(ref completions) == 1, "new score completes after re-enable");
            True(releases == 1 && loads == 2, "scoring does not unload or reload resident service");
            service.SetEnabled(false);
            WaitFor(() => Volatile.Read(ref releases) == 2, "disabled service releases");
            service.SetEnabled(true);
            WaitFor(() => Volatile.Read(ref loads) == 3, "service reloads again");
            service.Dispose();
            True(releases == 3, "dispose releases resident service");

            using var loading = new ManualResetEventSlim();
            int cancelledLoadRelease = 0;
            using var duringLoad = new SentenceServiceLifecycle(
                token => { loading.Set(); token.WaitHandle.WaitOne(5000); token.ThrowIfCancellationRequested(); },
                () => Interlocked.Increment(ref cancelledLoadRelease),
                (request, token) => null, null);
            duringLoad.SetEnabled(true);
            True(loading.Wait(5000), "preload started");
            duringLoad.SetEnabled(false);
            WaitFor(() => Volatile.Read(ref cancelledLoadRelease) == 1, "cancelled preload releases resources");

            int attempts = 0, cleanupAttempts = 0, recoveredScores = 0;
            using var failedLoad = new ManualResetEventSlim();
            using var recovery = new SentenceServiceLifecycle(
                token => { Interlocked.Increment(ref attempts); failedLoad.Set(); throw new InvalidOperationException("fake load failure"); },
                () =>
                {
                    if (Interlocked.Increment(ref cleanupAttempts) == 1)
                        throw new InvalidOperationException("fake cleanup failure");
                },
                (request, token) => new[] { 1.0 },
                (generation, raw, scores) => Interlocked.Increment(ref recoveredScores));
            recovery.SetEnabled(true);
            True(failedLoad.Wait(5000), "failed preload attempted");
            recovery.Request(new SentenceRerankRequest { Generation = 4 });
            WaitFor(() => Volatile.Read(ref recoveredScores) == 1, "load failure does not stop later scoring");
            recovery.SetEnabled(false);
            recovery.SetEnabled(true);
            WaitFor(() => Volatile.Read(ref attempts) == 2, "reload survives cleanup failure");
            True(cleanupAttempts >= 2, "failed cleanup retried before reload");
        }
    }
}
