using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int learningChecks;
        private static void LearningCheck(bool ok, string why)
        {
            learningChecks++; if (!ok) throw new Exception("Learning: " + why);
        }
        private static SentenceLearningEvent LearningEvent(string code = "aa", string text = "乙", string context = "", string id = null) =>
            new() { Code = code, Text = text, Context = context, Mode = "test-v1", Id = id ?? Guid.NewGuid().ToString("N") };
        private static int RunLearningWorker(string path, string prefix, int count)
        {
            new SentenceLearningStore(path).Confirm(Enumerable.Range(0, count).Select(i => LearningEvent(id: prefix + "-" + i)));
            return 0;
        }
        private static int RunLearningTests()
        {
            learningChecks = 0;
            string root = Path.Combine(Path.GetTempPath(), "tigerclaw-learning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                LearningRules(); LearningStorage(root); LearningDecoder(); LearningEngineAndProtocol(root);
                LearningPerformance();
                Console.WriteLine(JsonSerializer.Serialize(new { test = "tab_learning", status = "passed", checks = learningChecks, physicalTsfTested = false }));
                return 0;
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
        private static void LearningRules()
        {
            LearningCheck(SentenceLearning.Characters("虎𰻞") == 2, "Unicode scalar length");
            LearningCheck(SentenceLearning.Context("前虎𰻞") == "虎𰻞", "context no surrogate split");
            LearningCheck(!SentenceLearning.StaticText("\ud800") && !SentenceLearning.StaticText("{日期}") && !SentenceLearning.StaticText("显示\x1e输出"), "dynamic/invalid text rejected");
            LearningCheck(SentenceLearning.StaticText(new string('中', 16)) && !SentenceLearning.StaticText(new string('中', 17)), "16 scalar limit");
            var e = LearningEvent("aabb", "虎娘", "设置"); var s = SentenceLearningSnapshot.Build(new[] { e }, e.Time);
            LearningCheck(s.Score(e.Mode, e.Code, e.Text, e.Context) == 6, "first correction effective");
            LearningCheck(s.Score(e.Mode, e.Code, e.Text, "其他") == 0 && s.Score("other", e.Code, e.Text, e.Context) == 0, "mode/context scope");
            LearningCheck(s.PrefixScore(e.Mode, "aa", "虎", e.Context) == 6 && s.PrefixScore(e.Mode, "aa", "狼", e.Context) == 0, "prefix retention hint");
            LearningCheck(SentenceLearningSnapshot.Build(new[] { e }, e.Time + 30 * 86400).Score(e.Mode, e.Code, e.Text, e.Context) == 3, "30 day half life");
            var b = LearningEvent(e.Code, e.Text, e.Context); b.Time = e.Time;
            var c = LearningEvent(e.Code, e.Text, "测试"); c.Time = e.Time;
            LearningCheck(SentenceLearningSnapshot.Build(new[] { e, b, c }, e.Time).Score(e.Mode, e.Code, e.Text, "其他") == 2, "weak cross-context generalization");
            var single1 = LearningEvent("aa", "乙", "前甲"); var single2 = LearningEvent("aa", "乙", "前甲"); var single3 = LearningEvent("aa", "乙", "后甲");
            LearningCheck(SentenceLearningSnapshot.Build(new[] { single1, single2, single3 }).Score(e.Mode, "aa", "乙", "其他") == 0, "single character not generalized");
            var change = LearningEvent(e.Code, "虎爪", e.Context); change.Time = e.Time;
            var compete = SentenceLearningSnapshot.Build(new[] { e, b, change }, e.Time);
            LearningCheck(compete.Score(e.Mode, e.Code, change.Text, e.Context) > compete.Score(e.Mode, e.Code, e.Text, e.Context), "new correction wins old habit");
            LearningCheck(SentenceLearningSnapshot.Build(Enumerable.Repeat(e, 100), e.Time).Score(e.Mode, e.Code, e.Text, e.Context) <= 10, "bounded score");
            LearningCheck(SentenceInputDecoder.LearningEarlyCommitMaturity(6) == 0 &&
                Math.Abs(SentenceInputDecoder.LearningEarlyCommitMaturity(8) - 0.5) < 1e-12 &&
                SentenceInputDecoder.LearningEarlyCommitMaturity(10) == 1,
                "early-commit maturity grows 0 -> 0.5 -> 1");
            LearningCheck(SentenceInputDecoder.LearningEarlyCommitContribution(6) == 0 &&
                Math.Abs(SentenceInputDecoder.LearningEarlyCommitContribution(8) - 0.30) < 1e-12 &&
                Math.Abs(SentenceInputDecoder.LearningEarlyCommitContribution(10) - 0.75) < 1e-12,
                "learning confidence contribution follows maturity multiplier");
            string fusionMode = "sentence-v2|test";
            SentenceLearningEvent fusionEvent = SentenceFusionPreference.CreateEvent(
                fusionMode, "ii", "C", "A", true, 2);
            SentenceLearningSnapshot fusionSnapshot = SentenceLearningSnapshot.Build(
                new[] { fusionEvent }, fusionEvent.Time);
            LearningCheck(
                SentenceFusionPreference.SignedScore(fusionSnapshot, fusionMode, "ii", "C", "A") > 0,
                "fusion preference records Direct over Composed without changing table rank");
            SentenceCandidate learnedCandidate = Candidate("设置" + e.Text, (2, 2), (6, 4));
            SentenceLearningEvent[] reinforcement = SentenceLearning.Reinforce("xx" + e.Code, learnedCandidate, 0, e.Mode, s);
            LearningCheck(reinforcement.Length == 1 && reinforcement[0].Code == e.Code &&
                reinforcement[0].Text == e.Text && reinforcement[0].Context == e.Context,
                "stable learned top1 can be reinforced in the same context");
            foreach (var name in new[] { ".tigerclaw-learning-v1.log", ".TIGIRL-LEARNING-v1.log.bak.txt", ".tigerclaw-learning-v1.log.tmp.dict.yaml", ".tigirl-learning-v1.log.lock" })
                LearningCheck(SentenceLearning.IsReservedFile(name), "reserved filename " + name);
            LearningCheck(!SentenceLearning.IsReservedFile("正常码表.txt"), "normal table allowed");
            SentenceCandidate Candidate(string text, params (int raw, int chars)[] bounds)
            {
                SentencePathBoundary boundary = null;
                foreach (var point in bounds) boundary = new SentencePathBoundary { Previous = boundary, RawLength = point.raw, TextLength = point.chars };
                return new SentenceCandidate { Text = text, Boundary = boundary };
            }
            var old = Candidate("设置虎狼窗口", (2, 2), (4, 4), (6, 6)); var chosen = Candidate("设置虎娘窗口", (2, 2), (4, 4), (6, 6));
            var diff = SentenceLearning.Diff("AAbbcc", old, chosen, 0, "test-v1");
            LearningCheck(diff.Count == 1 && diff[0].Code == "bb" && diff[0].Text == "虎娘" && diff[0].Context == "设置", "local changed span only");
            old = Candidate("甲乙", (4, 2)); chosen = Candidate("甲丙", (2, 1), (4, 2));
            diff = SentenceLearning.Diff("aabb", old, chosen, 0, "test-v1");
            LearningCheck(diff.Count == 1 && diff[0].Code == "aabb" && diff[0].Text == "甲丙", "shared raw boundaries");
            LearningCheck(SentenceLearning.Diff("aabb", old, chosen, 2, "test-v1").Count == 0, "locked floor cannot be crossed");
        }
        private static void LearningStorage(string root)
        {
            string path = Path.Combine(root, ".tigerclaw-learning-v1.log"); var store = new SentenceLearningStore(path);
            store.Refresh(); LearningCheck(!File.Exists(path), "read-only startup does not create a log");
            var a = LearningEvent(); store.Confirm(new[] { a }); store.Confirm(new[] { a, a });
            LearningCheck(store.Entries().Length == 1, "persistent event idempotency");
            File.AppendAllText(path, "TCL1\tE\tpartial", Encoding.ASCII); var b = LearningEvent("bb", "国", "乙"); store.Confirm(new[] { b });
            LearningCheck(store.Entries().Length == 2, "torn tail repaired"); File.AppendAllText(path, "bad\tcrc\t1\n", Encoding.ASCII);
            LearningCheck(store.Entries().Length == 2, "corrupt rows ignored");
            LearningCheck(store.UndoLast() && store.Entries().Length == 1, "undo exact event");store.Confirm(new[] { b });
            LearningCheck(store.Entries().Length == 1, "undo cannot replay back");store.Clear();store.Confirm(new[] { a });
            LearningCheck(store.Entries().Length == 0 && store.Snapshot.IsEmpty, "clear without resurrection");
            var fresh = LearningEvent(); store.Confirm(new[] { fresh }); LearningCheck(store.Entries().Length == 1, "new records after clear");
            var other = new SentenceLearningStore(path);other.Refresh();LearningCheck(other.Snapshot.Score(fresh.Mode, fresh.Code, fresh.Text, fresh.Context) > 0, "other reader sees learning");
            long tick = 1; var receipts = new SentenceLearningReceipts(() => tick);
            var pending = LearningEvent();string receipt = receipts.Issue("client-a", store, new[] { pending });
            LearningCheck(store.Entries().Length == 1, "issuing response does not learn");
            LearningCheck(!receipts.Acknowledge("client-b", receipt, true), "wrong client rejected");
            LearningCheck(receipts.Acknowledge("client-a", receipt, true), "matching successful receipt queued");store.FlushAsync().GetAwaiter().GetResult();
            LearningCheck(store.Entries().Length == 2 && !receipts.Acknowledge("client-a", receipt, true), "duplicate receipt exactly once");
            receipt = receipts.Issue("client-a", store, new[] { LearningEvent() });
            LearningCheck(!receipts.Acknowledge("client-a", receipt, false) && !receipts.Acknowledge("client-a", receipt, true), "failure is terminal");
            receipt = receipts.Issue("client-a", store, new[] { LearningEvent() });tick += 30001;
            LearningCheck(!receipts.Acknowledge("client-a", receipt, true), "expired receipt rejected");
            receipt = receipts.Issue("client-a", store, new[] { LearningEvent() });receipts.Cancel();
            LearningCheck(!receipts.Acknowledge("client-a", receipt, true), "canceled focus receipt rejected");
            string directory = Path.Combine(root, "tables"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "普通.txt"), "aa\t甲\n");
            File.WriteAllText(Path.Combine(directory, ".tigirl-learning-v1.log.bak.txt"), "xx\t污染\n");
            File.WriteAllText(Path.Combine(directory, ".tigerclaw-learning-v1.tmp.dict.yaml"), "xx\t污染\n");
            var files = CoreRuntimeState.GetOrderedLexiconFiles(directory);
            LearningCheck(files.Length == 1 && Path.GetFileName(files[0]) == "普通.txt", "actual lexicon enumerator excludes backups/temp files");
            string concurrent = Path.Combine(root, "concurrent", ".tigerclaw-learning-v1.log");Directory.CreateDirectory(Path.GetDirectoryName(concurrent));
            var children = new List<Process>();
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--learning-worker");
                    start.ArgumentList.Add(concurrent);start.ArgumentList.Add("p" + i);start.ArgumentList.Add("20");
                    children.Add(Process.Start(start));
                }
                foreach (var child in children) LearningCheck(child.WaitForExit(60000) && child.ExitCode == 0, "independent writer completion");
            }
            finally { foreach (var child in children) { if (!child.HasExited) child.Kill(true); child.Dispose(); } }
            var merged = new SentenceLearningStore(concurrent);LearningCheck(merged.Entries().Length == 320, "16 process writes do not overwrite");
            RunLearningWorker(concurrent, "p0", 20);LearningCheck(merged.Entries().Length == 320, "cross-process duplicate IDs not counted");
            Console.WriteLine("{\"test\":\"learning_multi_process\",\"status\":\"passed\",\"processes\":16,\"events\":320}");
        }
        private static void LearningDecoder()
        {
            var lexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙", "重庆" }, ["bb"] = new() { "中" }, ["cc"] = new() { "国" } });
            var decoder = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance, beamWidth: 1, isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true);
            var plain = decoder.Decode("aabb", 20, true);LearningCheck(plain.Candidates[0].Text == "甲中" && !plain.Candidates.Any(c => c.Text == "乙中"), "baseline prunes second path");
            var e = LearningEvent("aabb", "乙中");decoder.SetLearning(SentenceLearningSnapshot.Build(new[] { e }), e.Mode);
            var learned = decoder.Decode("aabb", 20, true);
            LearningCheck(learned.Candidates[0].Text == "乙中", "learned multi-edge path survives beam one");
            LearningCheck(learned.LearningAffected, "learning affected flag retained for diagnostics/empty-code safety");
            LearningCheck(Math.Abs(learned.Candidates[0].FinalScore - learned.Candidates[0].BaseScore - 6) < 1e-5, "one reward only");

            var confidenceDecoder = new SentenceInputDecoder(
                lexicon, NeutralSentenceLanguageModel.Instance, beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true);
            SentenceDecodeResult confidencePlain = confidenceDecoder.Decode("aabb", 20, true);
            SentenceCandidate plainTarget = confidencePlain.Candidates.Single(c => c.Text == "乙中");
            confidenceDecoder.SetLearning(SentenceLearningSnapshot.Build(new[] { e }, e.Time), e.Mode);
            SentenceDecodeResult firstLearning = confidenceDecoder.Decode("aabb", 20, true);
            SentenceCandidate firstTarget = firstLearning.Candidates.Single(c => c.Text == "乙中");
            LearningCheck(firstLearning.LearningAffected && firstLearning.EarlyCommitEvidence.Prefixes.Length > 0,
                "learning no longer disables early-commit evidence");
            LearningCheck(Math.Abs(firstTarget.EarlyCommitConfidenceScore - plainTarget.EarlyCommitConfidenceScore) < 1e-12,
                "first correction has zero early-commit confidence contribution");
            var e2 = LearningEvent(e.Code, e.Text, e.Context); e2.Time = e.Time;
            confidenceDecoder.SetLearning(SentenceLearningSnapshot.Build(new[] { e, e2 }, e.Time), e.Mode);
            SentenceCandidate secondTarget = confidenceDecoder.Decode("aabb", 20, true).Candidates.Single(c => c.Text == "乙中");
            var e3 = LearningEvent(e.Code, e.Text, e.Context); e3.Time = e.Time;
            confidenceDecoder.SetLearning(SentenceLearningSnapshot.Build(new[] { e, e2, e3 }, e.Time), e.Mode);
            SentenceCandidate matureTarget = confidenceDecoder.Decode("aabb", 20, true).Candidates.Single(c => c.Text == "乙中");
            LearningCheck(secondTarget.EarlyCommitConfidenceScore > firstTarget.EarlyCommitConfidenceScore &&
                matureTarget.EarlyCommitConfidenceScore > secondTarget.EarlyCommitConfidenceScore,
                "stable learning progressively increases early-commit confidence");
            LearningCheck(decoder.Decode("aabbcc").Candidates[0].Text == "乙中国", "local preference with suffix");
            LearningCheck(decoder.Decode("aa2bb").Candidates[0].Text == "乙中", "explicit selectors retain meaning");
            var illegal = LearningEvent("aabb", "重庆中");decoder.SetLearning(SentenceLearningSnapshot.Build(new[] { illegal }), illegal.Mode);
            LearningCheck(!decoder.Decode("aabb").Candidates.Any(c => c.Text == illegal.Text), "no illegal dictionary edges injected");
            decoder.SetLearning(null, "");var restored = decoder.Decode("aabb");
            LearningCheck(restored.Candidates[0].Text == plain.Candidates[0].Text && !restored.LearningAffected, "disable restores baseline");
            var single = LearningEvent();decoder.SetLearning(SentenceLearningSnapshot.Build(new[] { single }), single.Mode);var first = decoder.Decode("aa");
            LearningCheck(first.Candidates[0].Text == "甲" &&
                SentenceFusionPreference.IsDirect(first.Candidates[0]) &&
                first.Candidates.All(candidate => candidate.LearningScore == 0),
                "Direct-to-Direct history never changes exact table order");
            var prefix = new SentenceLockedPrefix("aa", first.Candidates[0].Text, first.Candidates[0].Boundary);decoder.SetLearning(null, "");
            var locked = decoder.Decode("aabb", lockedPrefix: prefix);LearningCheck(locked.Candidates[0].LearningScore == 0 && locked.Candidates[0].Text == "甲中", "lock no stale scores");

            var fusionDecoder = new SentenceInputDecoder(
                lexicon, NeutralSentenceLanguageModel.Instance, beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true);
            var directB = new SentenceCandidate { Text = "B", Source = SentenceCandidateSource.Direct, DirectRank = 1 };
            var directC = new SentenceCandidate { Text = "C", Source = SentenceCandidateSource.Direct, DirectRank = 2 };
            var composedA = new SentenceCandidate { Text = "A", Source = SentenceCandidateSource.Composed };
            var merge = new[] { composedA, directB, directC };
            fusionDecoder.ApplyFusionOrdering("ii", merge);
            LearningCheck(string.Concat(merge.Select(c => c.Text)) == "ABC", "baseline cross-source order preserved");
            string fusionMode = "sentence-v2|test";
            var fusionEvent = SentenceFusionPreference.CreateEvent(fusionMode, "ii", "C", "A", true, 2);
            fusionDecoder.SetLearning(SentenceLearningSnapshot.Build(new[] { fusionEvent }, fusionEvent.Time), fusionMode);
            fusionDecoder.Decode("aa");
            merge = new[]
            {
                new SentenceCandidate { Text = "A", Source = SentenceCandidateSource.Composed },
                new SentenceCandidate { Text = "B", Source = SentenceCandidateSource.Direct, DirectRank = 1 },
                new SentenceCandidate { Text = "C", Source = SentenceCandidateSource.Direct, DirectRank = 2 }
            };
            fusionDecoder.ApplyFusionOrdering("ii", merge);
            LearningCheck(string.Concat(merge.Select(c => c.Text)) == "BCA",
                "selecting Direct C promotes only the Direct prefix B,C across Composed A");
        }
        private static void LearningEngineAndProtocol(string root)
        {
            string scheme = Path.Combine(root, "engine", "码表", "虎整句");Directory.CreateDirectory(scheme);
            File.WriteAllText(Path.Combine(scheme, "fixture.txt"), "aa\t甲\t乙\nbb\t中\t国\ncc\t人\n", new UTF8Encoding(false));
            var state = new CoreRuntimeState(Path.Combine(root, "engine"));EnableSentenceMode(state);
            state.TrySetConfigValue("整句Tab自学习", "是", out _, out _);state.TrySetConfigValue("整句自动提前上屏", "否", out _, out _);
            state.TrySetConfigValue("允许单字重码组句", "是", out _, out _);state.TrySetConfigValue("整句神经重排", "否", out _, out _);
            var decoder = CreateSentenceDecoder(new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" }, ["bb"] = new() { "中", "国" }, ["cc"] = new() { "人" } }, true);
            using var engine = new InputMethodEngine(state, decoder);
            TypeLetters(engine, "aa");
            string[] directBefore = engine.GetUiSnapshot(5).Candidates;
            Press(engine, 9);var output = Press(engine, 32);var events = engine.TakeSentenceLearning(output, out var store);
            LearningCheck(output.TextToOutput == directBefore[1] && events.Length == 0 && store != null,
                "ordinary selection between Direct candidates never creates learning");
            LearningCheck(store.Path == Path.Combine(scheme, ".tigerclaw-learning-v1.log") && !File.Exists(store.Path), "scheme source folder, not cache, no pre-ack write");
            TypeLetters(engine, "aa");
            LearningCheck(engine.GetUiSnapshot(5).Candidates.Take(2).SequenceEqual(directBefore.Take(2)),
                "Direct table order survives ordinary manual selection");
            Press(engine, 27);

            TypeLetters(engine, "aabb");
            string[] composedBefore = engine.GetUiSnapshot(5).Candidates;
            string baseComposedTop = composedBefore[0], learnedComposed = composedBefore[1];
            Press(engine, 9);output = Press(engine, 32);events = engine.TakeSentenceLearning(output, out store);
            LearningCheck(output.TextToOutput == learnedComposed && events.Length == 1,
                "Composed-to-Composed manual selection still stages sentence learning");
            store.ConfirmAsync(events);store.FlushAsync().GetAwaiter().GetResult();
            TypeLetters(engine, "aabb");LearningCheck(engine.GetUiSnapshot(5).Candidates[0] == learnedComposed, "confirmed Composed preference used by next composition");
            output = Press(engine, 32);events = engine.TakeSentenceLearning(output, out _);
            LearningCheck(events.Length == 1 && store.Entries().Length == 1,
                "explicit stable Composed top1 stages one reinforcement without writing before ack");
            store.ConfirmAsync(events);store.FlushAsync().GetAwaiter().GetResult();
            LearningCheck(store.Entries().Length == 2 &&
                Math.Abs(store.Snapshot.Score(events[0].Mode, events[0].Code, events[0].Text, events[0].Context) - 8) < 1e-9,
                "second stable Composed observation reaches half maturity");
            TypeLetters(engine, "aabb");output = Press(engine, 32);events = engine.TakeSentenceLearning(output, out _);
            LearningCheck(events.Length == 1, "mature Composed top1 stages another explicit reinforcement");
            store.ConfirmAsync(events);store.FlushAsync().GetAwaiter().GetResult();
            double matureScore = store.Snapshot.Score(events[0].Mode, events[0].Code, events[0].Text, events[0].Context);
            LearningCheck(store.Entries().Length == 3 && matureScore > 9.99 &&
                SentenceInputDecoder.LearningEarlyCommitMaturity(matureScore) > 0.99,
                "third stable Composed observation reaches effectively full maturity");
            TypeLetters(engine, "aabb");output = Press(engine, 32);
            LearningCheck(engine.TakeSentenceLearning(output, out _).Length == 0,
                "fully mature Composed top1 does not append redundant reinforcement");
            TypeLetters(engine, "aabb");Press(engine, 9);output = Press(engine, 27);LearningCheck(engine.TakeSentenceLearning(output, out _).Length == 0, "Escape discards browsing");
            state.TrySetConfigValue("整句Tab自学习", "否", out _, out _);TypeLetters(engine, "aabb");LearningCheck(engine.GetUiSnapshot(5).Candidates[0] == baseComposedTop, "disabled engine order restored");Press(engine, 27);
            state.TrySetConfigValue("整句Tab自学习", "是", out _, out _);
            // Protocol test uses a fresh scheme and simulated TSF success/failure
            // acknowledgements; it is not a live document editing test.
            string separate = Path.Combine(root, "protocol");Directory.CreateDirectory(Path.Combine(separate, "码表", "虎整句"));
            File.WriteAllText(Path.Combine(separate, "码表", "虎整句", "fixture.txt"), "aa\t甲\t乙\n", new UTF8Encoding(false));
            var protocolState = new CoreRuntimeState(separate);EnableSentenceMode(protocolState);
            protocolState.TrySetConfigValue("整句Tab自学习", "是", out _, out _);protocolState.TrySetConfigValue("整句自动提前上屏", "否", out _, out _);
            protocolState.TrySetConfigValue("整句神经重排", "否", out _, out _);
            using var handler = new ProtocolHandler(_ => { }, protocolState, null, CreateSentenceDecoder(new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" } }, true), true);
            var actual = (InputMethodEngine)typeof(ProtocolHandler).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
            int serial = 0;string last = "";
            string Key(int vk, bool capability = true)
            {
                last = JsonSerializer.Serialize(new { type = "key", seq = ++serial, client_session = "test-client", event_id = serial.ToString(), action = "down", vk, learning_ack_version = capability ? 1 : 0 });
                return handler.Handle(last);
            }
            Key('A');Key('A');Key(9);string response = Key(32);using var json = JsonDocument.Parse(response);
            LearningCheck(json.RootElement.GetProperty("commit_text").GetString() == "乙", "protocol actual corrected commit");
            string receipt = json.RootElement.GetProperty("learning_receipt").GetString();
            var actualStore = (SentenceLearningStore)typeof(InputMethodEngine).GetField("_learningStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actual);
            LearningCheck(!File.Exists(actualStore.Path), "Core response alone cannot learn");
            using var replay = JsonDocument.Parse(handler.Handle(last));LearningCheck(replay.RootElement.GetProperty("learning_receipt").GetString() == receipt, "same key retry replays receipt");
            string ack = JsonSerializer.Serialize(new { type = "learning_commit", client_session = "test-client", learning_receipt = receipt, applied = true });
            LearningCheck(handler.Handle(ack) == null, "ack does not pollute response stream");actualStore.FlushAsync().GetAwaiter().GetResult();handler.Handle(ack);actualStore.FlushAsync().GetAwaiter().GetResult();
            LearningCheck(actualStore.Entries().Length == 1, "protocol repeated success persists once");
            Key('A');Key('A');Key(9);response = Key(32, false);using var oldBridge = JsonDocument.Parse(response);
            LearningCheck(!oldBridge.RootElement.TryGetProperty("learning_receipt", out _), "old bridge safely does not learn");
        }
    }
}
