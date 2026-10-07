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
        private static int RunLearningFusionTests()
        {
            learningChecks = 0;
            string root = Path.Combine(Path.GetTempPath(), "tigerclaw-fusion-learning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                LearningFusionProtocol(root);
                Console.WriteLine(JsonSerializer.Serialize(new { test = "fusion_learning_protocol", status = "passed",
                    checks = learningChecks, physicalTsfTested = false }));
                return 0;
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        private static void LearningFusionProtocol(string root)
        {
            foreach (string frontend in new[] { "tsf", "hook_native" })
            {
                string folder = Path.Combine(root, "fusion-" + frontend);
                string schema = Path.Combine(folder, "码表", "虎整句");
                Directory.CreateDirectory(schema);
                File.WriteAllText(Path.Combine(schema, "fixture.txt"), "uj\t拾\nkf\t滑\nujkf\t捡\n", new UTF8Encoding(false));
                var state = new CoreRuntimeState(folder);
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句Tab自学习", "是", out _, out _);
                state.TrySetConfigValue("整句自动提前上屏", "否", out _, out _);
                state.TrySetConfigValue("整句神经重排", "否", out _, out _);
                state.TrySetConfigValue("允许单字重码组句", "是", out _, out _);
                state.TrySetConfigValue("高频字仅使用最优码组句", "0", out _, out _);
                SentenceInputDecoder Decoder() => new(
                    SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
                    {
                        ["uj"] = new() { "拾" }, ["kf"] = new() { "滑" }, ["ujkf"] = new() { "捡" }
                    }),
                    // A gap beyond the ordinary learning cap proves that the
                    // persisted cross-source preference reaches fusion ordering.
                    new ReviewCountingModel { Score = (_, _, target) => target == "捡" ? -100 : 0 },
                    rankPenalty: 0, beamWidth: 100, isolationPenalty: SentenceIsolationPenalty.None,
                    allowDuplicateSingleCharacters: true);
                using var handler = new ProtocolHandler(_ => { }, state, null, Decoder(), true);
                var engine = (InputMethodEngine)typeof(ProtocolHandler).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
                SentenceLearningStore Store() => (SentenceLearningStore)typeof(InputMethodEngine)
                    .GetField("_learningStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                SentenceDecodeResult Result() => (SentenceDecodeResult)typeof(InputMethodEngine)
                    .GetField("_sentenceDecodeResult", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                int serial = 0;
                string lastKey = "";
                string Key(int vk)
                {
                    lastKey = JsonSerializer.Serialize(new { type = "key", frontend, seq = ++serial,
                        client_session = "fusion-test", event_id = serial.ToString(), action = "down", vk, learning_ack_version = 1 });
                    return handler.Handle(lastKey);
                }
                void Type() { foreach (char c in "UJKF") Key(c); }
                void Ack(string receipt, bool applied)
                {
                    handler.Handle(JsonSerializer.Serialize(new { type = "learning_commit", client_session = "fusion-test",
                        learning_receipt = receipt, applied }));
                    Store().FlushAsync().GetAwaiter().GetResult();
                    LearningCheck(Store().LastError == null, frontend + " fusion journal write succeeds");
                }
                SentenceLearningStore Reload()
                {
                    var reloaded = new SentenceLearningStore(Store().Path);
                    reloaded.RefreshAsync();
                    reloaded.FlushAsync().GetAwaiter().GetResult();
                    LearningCheck(reloaded.LastError == null, frontend + " fusion journal reload succeeds");
                    return reloaded;
                }

                Type();
                LearningCheck(Result().Candidates.Select(c => c.Text).SequenceEqual(new[] { "拾滑", "捡" }),
                    frontend + " ujkf reproduces composed first and direct second");
                LearningCheck(SentenceFusionPreference.IsComposedOnly(Result().Candidates[0]) &&
                    SentenceFusionPreference.IsDirect(Result().Candidates[1]), frontend + " candidates retain their source");
                Key(9); Key(27);
                LearningCheck(!File.Exists(Store().Path), frontend + " cancelled Tab preview never persists");

                Type(); Key(9);
                using (var failed = JsonDocument.Parse(Key(32)))
                {
                    LearningCheck(failed.RootElement.GetProperty("commit_text").GetString() == "捡", frontend + " selected Direct commit");
                    string receipt = failed.RootElement.GetProperty("learning_receipt").GetString();
                    LearningCheck(!File.Exists(Store().Path), frontend + " response alone cannot learn fusion");
                    Ack(receipt, false);
                    Ack(receipt, true);
                    LearningCheck(Store().Entries().Length == 0, frontend + " failed receipt cannot later become learning");
                }

                Type();
                LearningCheck(Result().Candidates[0].Text == "拾滑", frontend + " failed confirmation preserves baseline");
                Key(9);
                using (var confirmed = JsonDocument.Parse(Key(32)))
                {
                    string receipt = confirmed.RootElement.GetProperty("learning_receipt").GetString();
                    using var replay = JsonDocument.Parse(handler.Handle(lastKey));
                    LearningCheck(replay.RootElement.GetProperty("learning_receipt").GetString() == receipt,
                        frontend + " key retry retains one fusion receipt");
                    Ack(receipt, true); Ack(receipt, true);
                }
                var events = Store().Entries();
                LearningCheck(events.Length == 1 && events[0].Mode.StartsWith("fusion-v1|", StringComparison.Ordinal) &&
                    events[0].Text == SentenceFusionPreference.DirectToken,
                    frontend + " actual adapter persists one fusion event after duplicate acknowledgement");
                Type();
                string mode = Result().LearningMode;
                LearningCheck(Result().Candidates[0].Text == "捡" && Result().Candidates[0].LearningScore == 0,
                    frontend + " next composition promotes Direct without sentence reward");
                using (var top = JsonDocument.Parse(Key(32)))
                    LearningCheck(!top.RootElement.TryGetProperty("learning_receipt", out _), frontend + " normal top1 commit does not reinforce");
                LearningCheck(Store().Entries().Length == 1, frontend + " normal top1 leaves journal unchanged");

                var reloaded = Reload();
                using var freshDecoder = Decoder();
                freshDecoder.SetLearning(reloaded.Snapshot, mode);
                LearningCheck(freshDecoder.Decode("ujkf").Candidates[0].Text == "捡",
                    frontend + " fresh store and decoder retain the learned first choice");
                freshDecoder.SetLearning(reloaded.Snapshot, mode + "|different");
                LearningCheck(freshDecoder.Decode("ujkf").Candidates[0].Text == "拾滑",
                    frontend + " another mode cannot consume this fusion preference");

                Type(); Key(9);
                using (var reversed = JsonDocument.Parse(Key(32)))
                {
                    LearningCheck(reversed.RootElement.GetProperty("commit_text").GetString() == "拾滑",
                        frontend + " reverse correction commits Composed candidate");
                    Ack(reversed.RootElement.GetProperty("learning_receipt").GetString(), true);
                }
                reloaded = Reload();
                LearningCheck(reloaded.Entries().Length == 2, frontend + " reverse correction persists exactly once");
                freshDecoder.SetLearning(reloaded.Snapshot, mode);
                LearningCheck(freshDecoder.Decode("ujkf").Candidates[0].Text == "拾滑",
                    frontend + " persisted reverse preference restores Composed first");
                Console.WriteLine(frontend + ": ujkf fusion receipt -> journal -> reload -> ordering passed");
            }
        }
    }
}
