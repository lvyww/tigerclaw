using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void LearningSeedBoundaries()
        {
            void Plan(List<SentenceLearningEvent> events, SentenceLearningSnapshot snapshot, double gap)
            {
                snapshot ??= SentenceLearningSnapshot.Empty;
                var head = new SentencePathBoundary { RawLength = 2, TextLength = 1 };
                SentenceCandidate Candidate(string text, double score) => new() { Text = "甲" + text,
                    Boundary = new SentencePathBoundary { Previous = head, RawLength = 4, TextLength = 2 },
                    FinalScore = score };
                SentenceLearning.PlanCorrectionLevels(events, snapshot, "zzaa", Candidate("丙", gap), Candidate("乙", 0));
            }
            foreach (var item in new[] { (-10.0, 1), (0.0, 1), (8.0, 1), (8.000001, 2),
                (10.0, 2), (10.000001, 3), (12.0, 3), (100.0, 3) })
            {
                var original = LearningEvent("aa", "乙", "甲");
                original.RawStart = 2; original.RawEnd = 4; original.TextStart = 1; original.TextEnd = 2;
                var events = new List<SentenceLearningEvent> { original };
                Plan(events, null, item.Item1);
                LearningCheck(events.Count == 1 && events[0].Levels == item.Item2, "adaptive seed boundary " + item.Item1);
                LearningCheck(events[0].Id == original.Id && events[0].RawStart == 2 && events[0].RawEnd == 4 &&
                    events[0].TextStart == 1 && events[0].TextEnd == 2, "one correction preserves receipt provenance");
                var snapshot = SentenceLearningSnapshot.Build(events);
                LearningCheck(snapshot.Score(original.Mode, "aa", "乙", "甲") == 7 + 2 * item.Item2,
                    "seed same-context reward");
                LearningCheck(snapshot.ConfidenceScore(original.Mode, "aa", "乙", "甲") == 9,
                    "one confirmation regardless of ranking jump");
            }
            var known = LearningEvent("aa", "乙", "甲");
            var existing = SentenceLearningSnapshot.Build(new[] { known });
            var subsequent = new List<SentenceLearningEvent> { LearningEvent("aa", "乙", "甲") };
            Plan(subsequent, existing, 100);
            LearningCheck(subsequent.Count == 1 && subsequent[0].Levels == 3,
                "existing preference can also advance three levels");
            var duplicate = new List<SentenceLearningEvent> { known.Copy(), known.Copy() };
            Plan(duplicate, existing, 100);
            LearningCheck(duplicate.Count == 1 && duplicate[0].Levels == 3, "duplicate fragment advances once");
            Plan(null, null, 100);
            var empty = new List<SentenceLearningEvent>(); Plan(empty, null, 100);
            LearningCheck(empty.Count == 0, "empty seed no-op");
        }

        private static void LearningSeedProtocol(string root)
        {
            foreach (string frontend in new[] { "tsf", "hook_native" })
            foreach (double penalty in new[] { 0.0, 8.0, 10.0, 12.0, 20.0 })
            {
                string folder = Path.Combine(root, "seed-" + frontend + "-" + penalty);
                string schema = Path.Combine(folder, "码表", "虎整句"); Directory.CreateDirectory(schema);
                File.WriteAllText(Path.Combine(schema, "fixture.txt"), "aa\t甲\t乙\nbb\t中\n", new UTF8Encoding(false));
                var state = new CoreRuntimeState(folder); EnableSentenceMode(state);
                state.TrySetConfigValue("整句Tab自学习", "是", out _, out _);
                state.TrySetConfigValue("整句自动提前上屏", "否", out _, out _);
                state.TrySetConfigValue("整句神经重排", "否", out _, out _);
                state.TrySetConfigValue("允许单字重码组句", "是", out _, out _);
                var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>> {
                    ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "中" } });
                var model = new ReviewCountingModel { Score = (_, _, target) => target == "乙" ? -penalty : 0 };
                var decoder = new SentenceInputDecoder(lexicon, model, rankPenalty: 0, beamWidth: 100,
                    isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true);
                using var handler = new ProtocolHandler(_ => { }, state, null, decoder, true);
                var engine = (InputMethodEngine)typeof(ProtocolHandler).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
                int serial = 0; string last = "";
                string Key(int vk)
                {
                    last = JsonSerializer.Serialize(new { type = "key", frontend, seq = ++serial,
                        client_session = "seed-test", event_id = serial.ToString(), action = "down", vk, learning_ack_version = 1 });
                    return handler.Handle(last);
                }
                Key('A'); Key('A'); Key('B'); Key('B');
                var result = (SentenceDecodeResult)typeof(InputMethodEngine).GetField("_sentenceDecodeResult", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                double gap = result.Candidates[0].FinalScore - result.Candidates.Single(c => c.Text == "乙中").FinalScore;
                int level = Math.Clamp((int)Math.Ceiling((Math.Max(0, gap) + 1 - 7) / 2), 1, 3);
                Key(9);
                using var response = JsonDocument.Parse(Key(32));
                LearningCheck(response.RootElement.GetProperty("commit_text").GetString() == "乙中", "seed corrected commit");
                string receipt = response.RootElement.GetProperty("learning_receipt").GetString();
                var store = (SentenceLearningStore)typeof(InputMethodEngine).GetField("_learningStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                LearningCheck(!File.Exists(store.Path), "seed waits for successful frontend acknowledgement");
                using var retry = JsonDocument.Parse(handler.Handle(last));
                LearningCheck(retry.RootElement.GetProperty("learning_receipt").GetString() == receipt, "seed retry same receipt");
                string ack = JsonSerializer.Serialize(new { type = "learning_commit", client_session = "seed-test", learning_receipt = receipt, applied = true });
                handler.Handle(ack); store.FlushAsync().GetAwaiter().GetResult();
                handler.Handle(ack); store.FlushAsync().GetAwaiter().GetResult();
                LearningCheck(store.Entries().Length == 1 && store.Entries()[0].Levels == level, "one correction persists planned levels, repeated ack deduplicated");
                var restored = new SentenceLearningStore(store.Path);
                restored.RefreshAsync(); restored.FlushAsync().GetAwaiter().GetResult();
                var records = restored.Entries(); var first = records[0];
                LearningCheck(records.Length == 1 && records[0].Levels == level && restored.Snapshot.Score(first.Mode, first.Code, first.Text, first.Context) == 7 + 2 * level,
                    "seed level survives journal reload");
                Key('A'); Key('A'); Key('B'); Key('B');
                string top = engine.GetUiSnapshot(5).Candidates[0];
                LearningCheck(top == (gap < 7 + 2 * level ? "乙中" : "甲中"), "seed next composition rank including cap");
                Key(27);
                Console.WriteLine(JsonSerializer.Serialize(new { test = "first_correction_seed", frontend, gap, level,
                    journal_records = records.Length, next_top = top, physicalTypingTested = false }));
            }
        }
    }
}
