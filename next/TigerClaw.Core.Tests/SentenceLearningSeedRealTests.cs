using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunLearningSeedReal(string runtime, string work, string raw = "tlleo", string baseline = "龙族", string corrected = "陲机", bool scoresOnly = false)
        {
            Directory.CreateDirectory(work);
            string schema = Path.Combine(work, "码表", "虎整句"); Directory.CreateDirectory(schema);
            LearningCheck(!File.Exists(Path.Combine(schema, "自学习-虎爪.txt")) &&
                !File.Exists(Path.Combine(schema, ".tigerclaw-learning-v1.log")), "real seed requires fresh isolated journal");
            foreach (string source in Directory.GetFiles(Path.Combine(runtime, "码表", "虎整句"), "*.txt"))
                if (!Path.GetFileName(source).StartsWith("自学习-", StringComparison.Ordinal))
                    File.Copy(source, Path.Combine(schema, Path.GetFileName(source)), true);
            File.Copy(Path.Combine(runtime, "config.txt"), Path.Combine(work, "config.txt"), true);
            Directory.CreateDirectory(Path.Combine(work, "Models"));
            File.Copy(Path.Combine(runtime, "Models", SentenceFivegramModel.FileName), Path.Combine(work, "Models", SentenceFivegramModel.FileName), true);
            var state = new CoreRuntimeState(work); state.Initialize();
            EnableSentenceMode(state);
            state.TrySetConfigValue("码表存储位置", "码表", out _, out _);
            state.TrySetConfigValue("当前码表", "虎整句", out _, out _);
            state.TrySetConfigValue("整句Tab自学习", "是", out _, out _);
            state.TrySetConfigValue("整句自动提前上屏", "否", out _, out _);
            state.TrySetConfigValue("整句神经重排", "否", out _, out _);
            using var handler = new ProtocolHandler(_ => { }, state, null, null, true, false);
            var engine = (InputMethodEngine)typeof(ProtocolHandler).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
            int serial = 0;
            string Key(int vk) => handler.Handle(JsonSerializer.Serialize(new { type = "key", frontend = "hook_native",
                seq = ++serial, client_session = "seed-real", event_id = serial.ToString(), action = "down", vk, learning_ack_version = 1 }));
            SentenceDecodeResult Result() => (SentenceDecodeResult)typeof(InputMethodEngine).GetField("_sentenceDecodeResult", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            object[] Scores() => Result().Candidates.Select(c => (object)new { c.Text, c.FinalScore, c.BaseScore, c.LearningScore, source = c.Source.ToString() }).ToArray();
            void Type() { foreach (char c in raw.ToUpperInvariant()) Key(c); }
            Type();
            var before = Scores();
            var initial = Result().Candidates;
            Console.WriteLine(JsonSerializer.Serialize(new { schema = state.GetCurrentCodeTablePath(), before }));
            if (scoresOnly)
            {
                var a = initial.Single(c => c.Text == baseline);
                var b = initial.Single(c => c.Text == corrected);
                var data = new { raw, baseline, corrected, gap = a.FinalScore - b.FinalScore, before,
                    neuralRerank = false, freshJournal = true, physicalTypingTested = false };
                string output = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(Path.Combine(work, "result.json"), output); Console.WriteLine(output);
                Key(27); return 0;
            }
            LearningCheck(initial.Any(c => c.Text == "龙族") && initial.Any(c => c.Text == "陲机"), "real pair present");
            double gap = initial[0].FinalScore - initial.Single(c => c.Text == "陲机").FinalScore;
            int target = Array.FindIndex(initial, c => c.Text == "陲机");
            for (int i = 0; i < target; i++) Key(9);
            using var commit = JsonDocument.Parse(Key(32));
            LearningCheck(commit.RootElement.GetProperty("commit_text").GetString() == "陲机", "real pair selected commit");
            string receipt = commit.RootElement.GetProperty("learning_receipt").GetString();
            var store = (SentenceLearningStore)typeof(InputMethodEngine).GetField("_learningStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            LearningCheck(!File.Exists(store.Path), "real pair not learned before ack");
            string ack = JsonSerializer.Serialize(new { type = "learning_commit", client_session = "seed-real", learning_receipt = receipt, applied = true });
            handler.Handle(ack); store.FlushAsync().GetAwaiter().GetResult();
            Type(); var after = Scores(); string top = Result().Candidates[0].Text;
            Key(27);
            var records = store.Entries();
            var reloaded = new SentenceLearningStore(store.Path); reloaded.RefreshAsync(); reloaded.FlushAsync().GetAwaiter().GetResult();
            LearningCheck(reloaded.Entries().Length == records.Length, "real journal reload");
            var rounds = new System.Collections.Generic.List<object>();
            rounds.Add(new { corrections = 1, top, reward = reloaded.Snapshot.Score(records[0].Mode, records[0].Code, records[0].Text, records[0].Context) });
            for (int round = 2; round <= 4; round++)
            {
                Type();
                int rank = Array.FindIndex(Result().Candidates, c => c.Text == "陲机");
                LearningCheck(rank >= 0, "pair remains in candidates before round " + round);
                if (rank == 0) { Key(27); break; }
                for (int i = 0; i < rank; i++) Key(9);
                using var nextCommit = JsonDocument.Parse(Key(32));
                string token = nextCommit.RootElement.GetProperty("learning_receipt").GetString();
                handler.Handle(JsonSerializer.Serialize(new { type = "learning_commit", client_session = "seed-real", learning_receipt = token, applied = true }));
                store.FlushAsync().GetAwaiter().GetResult();
                LearningCheck(store.Entries().Length == records.Length + round - 1, "later correction adds one confirmed event");
                Type();
                var choice = Result().Candidates.Single(c => c.Text == "陲机");
                rounds.Add(new { corrections = round, top = Result().Candidates[0].Text, reward = choice.LearningScore });
                if (round == 4) LearningCheck(Result().Candidates[0].Text == "陲机", "adaptive corrections overtake real model gap within four rounds");
                Key(27);
            }
            var report = new { raw, baseline, corrected, gap, before, after, next_top = top,
                events = records.Select(e => new { e.Code, e.Text, e.Context, e.Levels, score = reloaded.Snapshot.Score(e.Mode, e.Code, e.Text, e.Context) }),
                model = Path.Combine(runtime, "Models", SentenceFivegramModel.FileName), neuralRerank = false,
                freshJournal = true, receiptAcknowledged = true, rounds, physicalTypingTested = false };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(Path.Combine(work, "result.json"), json); Console.WriteLine(json);
            return 0;
        }
    }
}
