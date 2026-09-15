using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void ReviewEngineWorkerCancellation(string folder)
        {
            foreach (bool replace in new[] { false, true })
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var completed = new ManualResetEventSlim();
                int calls = 0;
                var blocked = new ReviewCountingModel { Score = (_, _, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(5000); }
                    return 0;
                } };
                var lexicon = new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "国" } };
                using var original = ReviewDecoder(lexicon, blocked);
                using var replacement = ReviewDecoder(new Dictionary<string, List<string>> { ["aa"] = new() { "丙", "丁" }, ["bb"] = new() { "国" } });
                var state = new CoreRuntimeState(Path.Combine(folder, "worker-" + replace));
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句Tab自学习", "否", out _, out _);
                state.TrySetConfigValue("整句神经重排", "是", out _, out _);
                using var engine = new InputMethodEngine(state, original, sentenceDecodeSynchronously: false);
                TypeLetters(engine, "aa");
                long oldGeneration = engine.GetDifferentialSnapshot(20).SentenceGeneration;
                try
                {
                    Review(entered.Wait(5000), "real Engine worker entered scoring");
                    engine.SetSentenceDecodeCompletedCallback(() => completed.Set());
                    if (replace)
                    {
                        object gate = typeof(InputMethodEngine).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                        lock (gate)
                            typeof(InputMethodEngine).GetMethod("ReplaceSentenceDecoder", BindingFlags.Instance | BindingFlags.NonPublic)
                                .Invoke(engine, new object[] { replacement });
                    }
                    else TypeLetters(engine, "bb");
                }
                finally { release.Set(); }
                Review(completed.Wait(5000), "replacement/latest generation publishes without another key");
                string expected = replace ? "丙" : "甲国";
                Review(engine.GetUiSnapshot(20).Candidates[0] == expected, "only latest decoder result published (replace=" + replace + ")");
                if (replace)
                {
                    long currentGeneration = engine.GetDifferentialSnapshot(20).SentenceGeneration;
                    Review(currentGeneration > oldGeneration, "decoder replacement advances same-raw generation");
                    Review(!engine.ApplySentenceNeuralScores(oldGeneration, "aa", new[] { 999.0, -999.0 }),
                        "old Qwen response rejected after decoder replacement");
                    Review(engine.ApplySentenceNeuralScores(currentGeneration, "aa", new[] { 0.0, 0.0 }),
                        "replacement generation still accepts its own Qwen response");
                }
                Press(engine, 27);
                Review(original.RetainedStatePositions == 0, "old worker state released after composition");
            }
        }
    }
}
