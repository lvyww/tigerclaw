using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void ReviewRerankCancellation(string folder)
        {
            using var started = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            int canceled = 0;
            long accepted = 0;
            using (var lifecycle = new SentenceServiceLifecycle(_ => { }, () => { }, (request, token) =>
            {
                if (request.Generation == 1)
                {
                    started.Set();
                    if (!token.WaitHandle.WaitOne(5000)) throw new TimeoutException("score cancellation missing");
                    Interlocked.Exchange(ref canceled, 1);
                    token.ThrowIfCancellationRequested();
                }
                return new[] { 0.0 };
            }, (generation, _, _) => { accepted = generation; completed.Set(); }))
            {
                lifecycle.SetEnabled(true);
                lifecycle.Request(new() { Generation = 1, RawCode = "aa", Candidates = new[] { "甲" } });
                Review(started.Wait(5000), "first neural request started");
                lifecycle.Request(new() { Generation = 2, RawCode = "aabb", Candidates = new[] { "甲乙" } });
                Review(completed.Wait(5000) && accepted == 2 && canceled == 1, "new request cancels old scoring and suppresses stale callback");
            }
            using var loading = new ManualResetEventSlim();
            using var releaseLoad = new ManualResetEventSlim();
            using var loadedScore = new ManualResetEventSlim();
            CancellationToken loadToken = default;
            using (var lifecycle = new SentenceServiceLifecycle(token =>
            {
                loadToken = token; loading.Set(); releaseLoad.Wait(5000); token.ThrowIfCancellationRequested();
            }, () => { }, (_, _) => new[] { 0.0 }, (_, _, _) => loadedScore.Set()))
            {
                lifecycle.SetEnabled(true);
                try
                {
                    Review(loading.Wait(5000), "preload started");
                    lifecycle.Request(new() { Generation = 3, RawCode = "aa", Candidates = new[] { "甲" } });
                    Review(!loadToken.IsCancellationRequested, "ordinary request does not cancel preload");
                }
                finally { releaseLoad.Set(); }
                Review(loadedScore.Wait(5000), "queued score follows preload");
            }
            ReviewEngineOwnership(folder);
            ReviewEngineWorkerCancellation(folder);
        }

        private static void ReviewEngineOwnership(string folder)
        {
            var lex = new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "国" } };
            var state = new CoreRuntimeState(Path.Combine(folder, "engine"));
            EnableSentenceMode(state);
            state.TrySetConfigValue("整句Tab自学习", "否", out _, out _);
            state.TrySetConfigValue("整句神经重排", "是", out _, out _);
            using var decoder = ReviewDecoder(lex);
            using var engine = new InputMethodEngine(state, decoder);
            TypeLetters(engine, "aabb");
            var before = decoder.Decode("aabb");
            long generation = engine.GetDifferentialSnapshot(20).SentenceGeneration;
            var scores = before.Candidates.Take(5).Select((_, i) => i == 0 ? -100.0 : 100.0).ToArray();
            Review(engine.ApplySentenceNeuralScores(generation, "aabb", scores), "real Engine accepts matching neural result");
            ReviewSame(before, decoder.Decode("aabb"));
            Review(engine.GetUiSnapshot(20).Candidates[0] != before.Candidates[0].Text, "Qwen menu changed without altering base cache");
            var many = Enumerable.Range(0, 2000).Select(i => ((char)(0x4e00 + i)).ToString()).ToList();
            var all = SentenceLexiconIndex.Build(new Dictionary<string, List<string>> { ["aa"] = many });
            var biased = new ReviewCountingModel { Score = (_, _, c) => c == many[0] ? 16 : 0 };
            using var largeDecoder = new SentenceInputDecoder(all, biased, rankPenalty: 0,
                isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true);
            var largeState = new CoreRuntimeState(Path.Combine(folder, "confidence-engine"));
            EnableSentenceMode(largeState);
            largeState.TrySetConfigValue("整句自动提前上屏", "是", out _, out _);
            largeState.TrySetConfigValue("整句Tab自学习", "否", out _, out _);
            using var largeEngine = new InputMethodEngine(largeState, largeDecoder);
            TypeLetters(largeEngine, "aa");
            var result = largeDecoder.Decode("aa", 20, true);
            double displayShare = 1 / result.Candidates.Sum(c => Math.Exp(c.ConfidenceScore - result.Candidates[0].ConfidenceScore));
            Review(displayShare >= .99999 && result.ConfidenceCandidates.Count == 2000, "strong-empty-code denominator fixture");
            var method = typeof(InputMethodEngine).GetMethod("GetEmptyCodeAutoCommitCandidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Review(method.Invoke(largeEngine, new object[] { false }) == null, "hidden full-pool competition blocks false strong empty-code acceptance");
        }
    }
}
