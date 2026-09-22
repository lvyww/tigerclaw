using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using TigerClaw.Core;
using TigerClaw.Pinyin;
using PinyinDecoder = TigerClaw.Pinyin.Decoder;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private sealed class PinyinTestModel : IPinyinLanguageModel
        {
            public double LogProbability(string a, string b, string c) => c == "泥/ni" ? -1 : c == "\u0003" ? -0.2 : -0.1;
        }
        private sealed class BlockingPinyinModel : IPinyinLanguageModel
        {
            internal int Calls;
            internal readonly ManualResetEventSlim Entered = new(false), Release = new(false), Left = new(false);
            public double LogProbability(string a, string b, string c)
            { Interlocked.Increment(ref Calls); Entered.Set(); Release.Wait(TimeSpan.FromSeconds(5)); Left.Set(); return -0.1; }
        }
        private static Lexicon PinyinTestLexicon()
        {
            var rows = new[] { ("你", "ni", 100), ("泥", "ni", 100), ("好", "hao", 100), ("你好", "nihao", 100), ("吗", "ma", 100), ("西", "xi", 100), ("安", "an", 100), ("先", "xian", 100), ("女", "nv", 100), ("儿", "er", 100), ("努", "nu", 100), ("尔", "er", 100) };
            var tokens = rows.ToDictionary(r => (r.Item1, r.Item2), r => r.Item1 == "你好" ? new[] { "你/ni", "好/hao" } : new[] { r.Item1 + "/" + r.Item2 });
            return new Lexicon(rows, tokens: tokens);
        }
        private static int RunFullPinyinTests()
        {
            var lexicon = PinyinTestLexicon(); var model = new PinyinTestModel();
            using var decoder = new PinyinDecoder(lexicon, model, 200);
            var session = new PinyinSession();
            session.Reset("NiHaoMa"); session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon);
            True(session.Choices[0].Text == "你好吗", "full-pinyin whole candidate");
            int word = Array.FindIndex(session.Choices, c => c.Text == "你好" && c.Consumed == 5);
            True(word >= 0, "prefix word menu");
            True(session.Confirm(word, true) == null && session.LockedEnd == 5 && session.LockedText == "你好", "selection locks without committing");
            long previous = session.Generation;
            session.Apply(previous - 1, decoder.Decode("ni"));
            True(session.AppliedGeneration != previous, "stale result refused");
            session.Apply(previous, decoder.Decode(session.Raw, prefix: session.Prefix));
            Equal("你好吗", session.Confirm(0, true), "full commit includes prefix once");
            session.MoveCursor(5, true); session.Backspace();
            True(session.LockedEnd == 0 && session.Raw == "NiHaoMa" && session.Cursor == 5, "backspace at lock boundary restores raw");
            session.MoveCursor(2, true); session.Insert('X'); session.Delete();
            Equal("NiXaoMa", session.Raw, "middle insertion and forward deletion");
            session.Reset("nih"); session.Apply(session.Generation, decoder.Decode(session.Raw, completeLastSyllable: true));
            True(session.HasComplete && !session.ExactComplete && session.Choices.Any(c => c.Text == "你好"), "terminal syllable completion");
            session.Reset("nh"); session.Apply(session.Generation, decoder.Decode(session.Raw, completeLastSyllable: true));
            True(!session.HasComplete, "interior abbreviation refused");
            True(decoder.Decode("nver").Candidates.Any(c => c.Text == "女儿"), "nv + er must not normalize into nue + r");
            var separated = decoder.Decode("xi'an");
            True(separated.Candidates.Any(c => c.Text == "西安") && separated.Candidates.All(c => c.Text != "先"), "apostrophe forces boundary");
            session.Reset("ni'hao'ma"); session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon);
            True(session.Choices.Any(c => c.Text == "你好" && c.Consumed == 6), "prefix word across explicit syllable boundaries");
            session.Reset("nihao"); session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon); session.ToggleMenu();
            int corrected = Array.FindIndex(session.Choices, c => c.Text == "泥好");
            True(corrected >= 0, "sentence alternative menu");
            Equal("泥好", session.Confirm(corrected, true), "non-first correction output");
            True(session.Corrections.Count == 1, "correction pending until receipt");
            session.Reset(); True(session.Corrections.Count == 0, "cancel discards corrections");

            string root = Path.Combine(Path.GetTempPath(), "TigerClaw-FullPinyin-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "tables", "测试全拼");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "schema.json"), "{\"version\":1,\"engine\":\"full_pinyin\"}");
                File.WriteAllText(Path.Combine(root, "config.txt"), "码表存储位置\ttables\n当前码表\t测试全拼\n全拼简拼\t否\n全拼拼写兼容\t否\n全拼错拼纠正\t否\n拼音英文候选\t否\n");
                var state = new CoreRuntimeState(root); state.Initialize();
                True(state.IsFullPinyinActive() && !state.IsSentenceInputActive(), "descriptor engine dispatch");
                using var engine = new InputMethodEngine(state, fullPinyinResources: new PinyinResources(lexicon, model, directory));
                KeyEngineResult Key(int vk, bool shift = false) => engine.ProcessKey(vk, 0, "down", shift, false, false, false, false, true, 1, false);
                void Type(string text) { foreach (char c in text) Key(char.ToUpperInvariant(c), char.IsUpper(c)); }
                void Wait() { True(SpinWait.SpinUntil(() => !engine.IsSentenceDecodePending, 5000), "async completion"); }
                Type("NiHaoMa"); Wait();
                True(engine.GetUiSnapshot(5).Candidates[0] == "你好吗", "engine async candidates");
                string staleToken = engine.GetUiSnapshot(5).CandidateSelectionToken;
                Key(0x28);
                True(engine.SelectFullPinyinCandidate(staleToken, 0).TextToOutput == null, "old menu click refused");
                Key(0x24); Key(0x27); Key(0x2e); // home, right, delete i
                Equal("NHaoMa", Key(0x0d).TextToOutput, "engine edits and literal casing");
                Type("nihao"); Key(0x1b); Thread.Sleep(30);
                True(!engine.GetUiSnapshot(5).IsComposing, "cancel prevents stale resurrection");
                Type("nih"); Wait(); True(Key(0xbe).TextToOutput == null, "incomplete punctuation retains code");
                Equal("nih", Key(0x0d).TextToOutput, "incomplete literal retained");
                Type("nihao"); Wait(); Equal("你好。", Key(0xbe).TextToOutput, "exact punctuation commits sentence");
                Type("nihao"); Wait(); Key(0x71); Key(0x28);
                var result = Key(0x20);
                Equal("泥好", result.TextToOutput, "keyboard sentence correction");
                var events = engine.TakeSentenceLearning(result, out var store);
                True(events.Length == 1 && store != null && !File.Exists(store.Path), "learning not persisted before commit receipt");
                var receipts = new SentenceLearningReceipts();
                // Receipt contracts are additionally exercised by --learning-tests.
                string receipt = receipts.Issue("full-pinyin-test", store, events);
                True(receipts.Acknowledge("full-pinyin-test", receipt, true), "successful receipt accepted");
                True(!receipts.Acknowledge("full-pinyin-test", receipt, true), "duplicate receipt refused");
                store.FlushAsync().GetAwaiter().GetResult();
                Type("nihao"); Wait(); Equal("泥好", engine.GetUiSnapshot(5).Candidates[0], "acknowledged correction changes ordering");
                Key(0x1b);
                Type("nihao"); Wait();
                string clickToken = engine.GetUiSnapshot(5).CandidateSelectionToken;
                Equal("泥好", engine.SelectFullPinyinCandidate(clickToken, 0).TextToOutput, "current candidate click commits");
                True(engine.SelectFullPinyinCandidate(clickToken, 0).TextToOutput == null, "repeated click cannot commit twice");
                True(engine.TryEditFullPinyinWord("泥好", "ni hao", false, out string error), "add user word " + error);
                True(File.Exists(Path.Combine(directory, PinyinUserWords.FileName)), "independent user word file");
                True(!engine.TryEditFullPinyinWord("泥好", "ni", false, out _), "word reading count validation");
                True(!engine.TryEditFullPinyinWord("泥好", "ni xyz", false, out _), "invalid syllable rejected");
                True(engine.TryEditFullPinyinWord("泥好", "ni hao", true, out _), "delete user word");
                True(PinyinUserWords.Load(directory).Length == 0, "delete survives reload");
                // Standalone characters use a separate receipt-backed competition bucket.
                Type("ni"); Wait();
                var characterUi = engine.GetUiSnapshot(200);
                int characterIndex = Array.IndexOf(characterUi.Candidates, "泥");
                True(characterIndex > 0, "standalone correction offered");
                var characterCommit = engine.SelectFullPinyinCandidate(characterUi.CandidateSelectionToken, characterIndex);
                Equal("泥", characterCommit.TextToOutput, "standalone correction commits");
                var characterEvents = engine.TakeSentenceLearning(characterCommit, out var characterStore);
                True(characterEvents.Length == 1 && characterEvents[0].Mode == SentenceLearning.PinyinCharacterMode,
                    "single-character receipt has independent mode");
                string characterReceipt = receipts.Issue("character", characterStore, characterEvents);
                True(receipts.Acknowledge("character", characterReceipt, true), "character receipt succeeds");
                characterStore.FlushAsync().GetAwaiter().GetResult();
                Type("ni"); Wait(); Equal("泥", engine.GetUiSnapshot(5).Candidates[0], "standalone learning promotes character"); Key(0x1b);
                True(characterStore.Snapshot.PinyinMatches(SentenceLearning.PinyinPhraseMode, "nihaoma").All(m => m.Text != "泥"),
                    "character absent from sentence retention and reward query");
                True(engine.ListPinyinPreferences().Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("泥"))), "character learning remains manageable");
                True(engine.TryManagePinyin("forget", "ni", "泥", out _), "forget character mode");
                characterStore.FlushAsync().GetAwaiter().GetResult();
                True(characterStore.Snapshot.PinyinCharacterMatches("ni").Length == 0, "forget removes character record");

                var legacy = new SentenceLearningEvent { Mode = SentenceLearning.PinyinPhraseMode, Code = "shi", Text = "是" };
                var phrase = new SentenceLearningEvent { Mode = SentenceLearning.PinyinPhraseMode, Code = "shi", Text = "试试" };
                var replay = SentenceLearningSnapshot.Build(new[] { legacy, phrase });
                var incremental = new SentenceLearningSnapshot.Accumulator().Update(new[] { legacy, phrase }, 0);
                foreach (var snapshot in new[] { replay, incremental })
                {
                    True(snapshot.PinyinMatches(SentenceLearning.PinyinPhraseMode, "shiyixia").All(m => m.Text != "是"), "legacy shi cannot reward 是以下");
                    True(snapshot.PinyinCharacterMatches("shiyixia").Length == 0, "character lookup requires whole raw code");
                    True(snapshot.PinyinCharacterMatches("shi").Single().Text == "是", "legacy character retained in standalone index");
                    True(snapshot.PinyinMatches(SentenceLearning.PinyinPhraseMode, "shiyixia").Single().Text == "试试", "phrase prefix learning preserved");
                }
                characterStore.ConfirmAsync(new[] { legacy }); characterStore.FlushAsync().GetAwaiter().GetResult();
                characterStore.ForgetAsync(SentenceLearning.PinyinCharacterMode, "shi", "是"); characterStore.FlushAsync().GetAwaiter().GetResult();
                True(characterStore.Snapshot.PinyinCharacterMatches("shi").Length == 0, "legacy character undo uses original journal identity");
                state.TrySetConfigValue("全拼纠正学习", "否", out _, out _);
                using (var handler = new ProtocolHandler(null, state, null, null, null, false,
                    new PinyinResources(lexicon, model, directory)))
                {
                    int id = 0;
                    string ProtocolKey(int vk) => handler.Handle("{\"type\":\"key\",\"seq\":" + (++id) + ",\"client_session\":\"pinyin-test\",\"event_id\":\"" + id + "\",\"action\":\"down\",\"vk\":" + vk + "}");
                    foreach (char c in "NIHAO") ProtocolKey(c);
                    True(handler.WaitForDifferentialIdle(5000), "protocol decode finished");
                    string moved = ProtocolKey(0x24);
                    True(moved.Contains("\"input_cursor\":0"), "TSF receives preedit cursor");
                    using var json = System.Text.Json.JsonDocument.Parse(handler.BuildDifferentialSnapshotJson());
                    string token = json.RootElement.GetProperty("candidate_selection_token").GetString();
                    string click = "{\"type\":\"key\",\"seq\":100,\"client_session\":\"pinyin-test\",\"event_id\":\"click-1\",\"action\":\"down\",\"vk\":0,\"scan\":0,\"candidate_token\":\"" + token + "\"}";
                    string response = handler.Handle(click);
                    using var commit = System.Text.Json.JsonDocument.Parse(response);
                    Equal("你好", commit.RootElement.GetProperty("commit_text").GetString(), "TSF click response contains full commit");
                    Equal(response, handler.Handle(click), "click uses physical-event replay cache");
                }
                // Space joins the pending generation without holding the engine
                // monitor or canceling/repeating its model work.
                var joiningModel = new BlockingPinyinModel();
                using (var joining = new InputMethodEngine(state, fullPinyinResources: new PinyinResources(lexicon, joiningModel, directory)))
                {
                    joining.ProcessKey('N', 0, "down", false, false, false, false, false, true, 1, false);
                    True(joiningModel.Entered.Wait(5000), "confirmation query entered");
                    var confirmation = System.Threading.Tasks.Task.Run(() => joining.ProcessKey(0x20, 0, "down", false, false, false, false, false, true, 1, false));
                    Thread.Sleep(30);
                    var inspect = System.Threading.Tasks.Task.Run(() => joining.GetUiSnapshot(5));
                    True(inspect.Wait(1000), "pending confirmation releases engine monitor");
                    joiningModel.Release.Set();
                    True(confirmation.Wait(5000), "pending confirmation completes");
                    True(!string.IsNullOrEmpty(confirmation.Result.TextToOutput), "pending confirmation commits existing result");
                    var counting = new BlockingPinyinModel();counting.Release.Set();
                    using var referenceJoin = new PinyinDecoder(lexicon, counting, 200, spellingOptions: state.GetPinyinSpellingOptions());
                    referenceJoin.Decode("n", 200, completeLastSyllable: true);
                    True(counting.Calls == joiningModel.Calls, "confirmation does not decode twice");
                }
                var blocking = new BlockingPinyinModel();
                using var racing = new InputMethodEngine(state, fullPinyinResources: new PinyinResources(lexicon, blocking, directory));
                int publications = 0;
                racing.SetSentenceDecodeCompletedCallback(() => Interlocked.Increment(ref publications));
                racing.ProcessKey('N', 0, "down", false, false, false, false, false, true, 1, false);
                True(blocking.Entered.Wait(5000), "old generation is actually running");
                racing.OnExternalCompositionCanceled(); blocking.Release.Set();
                True(blocking.Left.Wait(5000), "canceled model query drained");
                Thread.Sleep(50);
                True(publications == 0 && !racing.GetUiSnapshot(5).IsComposing, "late completed worker cannot publish after cancel");
                File.WriteAllText(Path.Combine(directory, "schema.json"), "{\"engine\":123}");
                var brokenState = new CoreRuntimeState(root); brokenState.Initialize();
                True(brokenState.IsFullPinyinActive(), "malformed descriptor fails closed instead of falling into shape input");
                using var broken = new InputMethodEngine(brokenState);
                broken.ProcessKey('N', 0, "down", false, false, false, false, false, true, 1, false);
                True(broken.ProcessKey(0x20, 0, "down", false, false, false, false, false, true, 1, false).TextToOutput == null, "failed resource confirmation preserves raw");
                Equal("n", broken.ProcessKey(0x0d, 0, "down", false, false, false, false, false, true, 1, false).TextToOutput, "failed resource literal exit");
            }
            finally { Directory.Delete(root, true); }
            RunPinyinRerankerTests();
            RunExpandedPinyinTests();
            Console.WriteLine("Full-pinyin decoder/session/Core integration tests passed."); return 0;
        }
    }
}
