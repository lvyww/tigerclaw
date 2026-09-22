using System;
using System.IO;
using System.Linq;
using System.Threading;
using TigerClaw.Core;
using TigerClaw.Pinyin;
using PinyinDecoder = TigerClaw.Pinyin.Decoder;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private sealed class PinyinAbbreviationBoundaryModel : IPinyinLanguageModel
        {
            public double LogProbability(string a, string b, string c) => c == "嗯/n" ? -10 : c == "哈/ha" ? -20 : -0.1;
        }
        private static void RunExpandedPinyinTests()
        {
            RunPinyinMenuTests();
            RunPinyinPerformanceTests();
            var rows = new[] { ("嗯", "n", 1), ("你", "ni", 100), ("泥", "ni", 80), ("好", "hao", 100), ("你好", "nihao", 100), ("吗", "ma", 100),
                ("中", "zhong", 100), ("国", "guo", 100), ("中国", "zhongguo", 100), ("女", "nv", 100), ("儿", "er", 100), ("虐", "nue", 100),
                ("绿", "lv", 100), ("略", "lue", 100), ("去", "qu", 100), ("来", "lai", 100), ("耐", "nai", 100), ("心", "xin", 100), ("星", "xing", 100) };
            var tokens = rows.ToDictionary(x => (x.Item1, x.Item2), x => x.Item1 == "你好" ? new[] { "你/ni", "好/hao" } :
                x.Item1 == "中国" ? new[] { "中/zhong", "国/guo" } : new[] { x.Item1 + "/" + (x.Item2 == "nue" ? "nve" : x.Item2 == "lue" ? "lve" : x.Item2) });
            var lexicon = new Lexicon(rows, tokens: tokens); var model = new PinyinTestModel();
            var spelling = PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Aliases;
            using var decoder = new PinyinDecoder(lexicon, model, 200, spellingOptions: spelling);
            foreach (string raw in new[] { "nh", "nhao", "nih", "n'h", "ni'hao" })
                True(decoder.Decode(raw, incremental: false).Candidates.Any(c => c.Text == "你好"), "abbreviation " + raw);
            foreach (var item in new[] { ("nve", "虐"), ("lve", "略"), ("qv", "去"), ("nver", "女儿"), ("nv'er", "女儿") })
                True(decoder.Decode(item.Item1, incremental: false).Candidates.Any(c => c.Text == item.Item2), "syllable alias " + item.Item1);
            var session = new PinyinSession(); session.Reset("nhm");
            foreach (var item in new[] { ("nihaoma", 2, 5), ("ni'hao'ma", 2, 6), ("nihm", 2, 3), ("nhm", 1, 2) })
            {
                foreach (var word in new[] { ("你", item.Item2), ("你好", item.Item3) })
                {
                    session.Reset(item.Item1);
                    session.Apply(session.Generation, decoder.Decode(session.Raw, incremental: false), lexicon: lexicon, spelling: spelling);
                    int choice = Array.FindIndex(session.Choices, c => c.Text == word.Item1);
                    True(choice >= 0, "prefix available " + item.Item1 + word.Item1);
                    True(word.Item2 == session.Choices[choice].Consumed, "prefix consumes complete spelling " + item.Item1 + word.Item1 + ": actual=" + session.Choices[choice].Consumed);
                    True(session.Confirm(choice, false) == null && session.LockedEnd == word.Item2, "prefix lock raw boundary");
                    session.Apply(session.Generation, decoder.Decode(session.Raw, prefix: session.Prefix), lexicon: lexicon, spelling: spelling);
                    Equal("你好吗", session.Confirm(0, false), "prefix continuation " + item.Item1 + word.Item1);
                }
            }
            session.Reset("nhm");
            session.Apply(session.Generation, decoder.Decode(session.Raw, incremental: false), lexicon: lexicon, spelling: spelling);
            int nihao = Array.FindIndex(session.Choices, c => c.Text == "你好" && c.Consumed == 2);
            True(nihao >= 0, "abbreviated prefix word raw boundary");
            True(session.Confirm(nihao, false) == null && session.LockedEnd == 2, "abbreviated prefix locks two keys");
            session.Apply(session.Generation, decoder.Decode(session.Raw, prefix: session.Prefix), lexicon: lexicon, spelling: spelling);
            Equal("你好吗", session.Confirm(0, false), "abbreviation locked continuation");
            session.MoveCursor(2, true); session.Backspace(); True(session.LockedEnd == 0 && session.Raw == "nhm", "abbreviation unlock keeps raw");
            session.Reset("nihao"); session.Apply(session.Generation, decoder.Decode(session.Raw), lexicon: lexicon, spelling: spelling);
            session.EditSyllable(false, false); True(session.Cursor == 2, "previous syllable navigation");
            session.EditSyllable(true, true); Equal("ni", session.Raw, "whole syllable deletion");
            using var typo = new PinyinDecoder(lexicon, model, 200, spellingOptions: PinyinSpellingOptions.Typos);
            True(typo.Decode("zhogn").Candidates.Any(c => c.Text == "中"), "bounded transposition spelling");
            using var fuzzy = new PinyinDecoder(lexicon, model, 200, spellingOptions: PinyinSpellingOptions.NL | PinyinSpellingOptions.InIng);
            True(fuzzy.Decode("nai").Candidates.Any(c => c.Text == "来"), "n-l fuzzy");
            True(fuzzy.Decode("xin").Candidates.Any(c => c.Text == "星"), "in-ing fuzzy");
            using var exact = new PinyinDecoder(lexicon, model, 200);
            True(exact.Decode("nai").Candidates.All(c => c.Text != "来"), "fuzzy off preserves exact search");
            Equal(exact.Decode("nnihao").Candidates[0].Text, decoder.Decode("nnihao").Candidates[0].Text,
                "consonant interjection in a fully spelled sentence keeps exact priority");
            var boundaryRows = rows.Concat(new[] { ("哈", "ha", 1), ("哦", "o", 1) }).ToArray();
            var boundaryTokens = boundaryRows.ToDictionary(x => (x.Item1, x.Item2), x => tokens.TryGetValue((x.Item1, x.Item2), out var value) ? value : new[] { x.Item1 + "/" + x.Item2 });
            using var boundaryDecoder = new PinyinDecoder(new Lexicon(boundaryRows, tokens: boundaryTokens), new PinyinAbbreviationBoundaryModel(), 200, spellingOptions: spelling);
            Equal("你好", boundaryDecoder.Decode("nhao").Candidates[0].Text,
                "low-ranked n-ha-o path must not suppress n-hao abbreviation");
            using var doublePinyin = new PinyinDecoder(lexicon, model, 200, spellingOptions: PinyinSpellingOptions.DoublePinyin);
            True(doublePinyin.Decode("nihc").Candidates.Any(c => c.Text == "你好"), "xiaohe ni hao");
            True(doublePinyin.Decode("vsgo").Candidates.Any(c => c.Text == "中国"), "xiaohe zhong guo");
            foreach (var item in new[] { (exact, PinyinSpellingOptions.None, "ni'hao'ma", 6),
                (doublePinyin, PinyinSpellingOptions.DoublePinyin, "nihcma", 4),
                (decoder, spelling, "nihaom", 5) })
            {
                session.Reset(item.Item3);
                session.Apply(session.Generation, item.Item1.Decode(session.Raw, completeLastSyllable: true, incremental: false), lexicon: lexicon, spelling: item.Item2);
                int choice = Array.FindIndex(session.Choices, c => c.Text == "你好");
                True(choice >= 0 && session.Choices[choice].Consumed == item.Item4, "exact/double/completion word boundary " + item.Item3);
                session.Confirm(choice, false);
                session.Apply(session.Generation, item.Item1.Decode(session.Raw, prefix: session.Prefix, completeLastSyllable: true), lexicon: lexicon, spelling: item.Item2);
                Equal("你好吗", session.Confirm(0, false), "exact/double/completion continuation " + item.Item3);
            }
            Equal("yk", Lexicon.XiaoheCode("ying"), "xiaohe ing"); Equal("ah", Lexicon.XiaoheCode("ang"), "xiaohe zero initial");
            using var auxiliary = new PinyinDecoder(lexicon, model, 200, spellingOptions: spelling, firstCharacters: new System.Collections.Generic.HashSet<string> { "泥" });
            True(auxiliary.Decode("nihao").Candidates.All(c => c.Text.StartsWith("泥", StringComparison.Ordinal)), "auxiliary constrains search before beam");
            var english = new PinyinEnglish(new[] { ("Windows", "windows", 100), ("hello", "hello", 100), ("Windows版本", "windowsbanben", 100) });
            True(english.Candidates("HEllo", null).Any(c => c.Text == "HELLO"), "English case");
            True(english.Candidates("windowsbanben", null).Any(c => c.Text == "Windows版本" && c.Whole), "mixed word");
            True(!english.Candidates("Windowsma", null).Any(), "English word cannot consume only the input prefix");
            True(english.Candidates("wind", null).Any(c => c.Text == "Windows" && c.Whole && c.Consumed == 4), "English completion consumes the entire input");
            Equal("20", PinyinTools.Candidates("/v(2+3)*4", DateTime.Now)[0], "calculator precedence");
            True(PinyinTools.Candidates("/v1/0", DateTime.Now).Length == 0, "division by zero keeps composition");
            True(PinyinTools.Candidates("/vSystem.exit()", DateTime.Now).Length == 0, "calculator rejects non arithmetic");
            Equal("壹佰零壹元零伍分", PinyinTools.Candidates("/r101.05", DateTime.Now)[0], "money internal zeros");
            Equal("壹亿零壹元整", PinyinTools.Candidates("/r100000001", DateTime.Now)[0], "money group zeros");
            Equal("2026-09-21", PinyinTools.Candidates("/rq", new DateTime(2026, 9, 21))[0], "date formatting");
            True(PinyinTools.Candidates("/emoji", DateTime.Now).Contains("👍"), "symbol tool");

            string root = Path.Combine(Path.GetTempPath(), "TigerClaw-Pinyin-Expanded-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "tables", "全拼"); Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "schema.json"), "{\"version\":1,\"engine\":\"full_pinyin\"}");
                File.WriteAllText(Path.Combine(root, "config.txt"), "码表存储位置\ttables\n当前码表\t全拼\n");
                var preferences = PinyinPreferences.Empty.Edit("phrase_add", "yx", "me@example.com\n第二行").Edit("pin", "nihao", "泥好");
                preferences.Save(directory); preferences = PinyinPreferences.Load(directory);
                True(preferences.IsPinned("nihao", "泥好") && preferences.GetPhrases("yx")[0].Text.Contains('\n'), "preferences durable roundtrip");
                Directory.CreateDirectory(Path.Combine(directory, "resources", "english"));
                File.WriteAllText(Path.Combine(directory, "resources", "english", "en.dict.yaml"), "Windows\tWindows\t100\nhello\thello\t100\n");
                File.WriteAllText(Path.Combine(directory, "resources", "tiger-codes.txt"), "你\tjx\n泥\tkcvb\n好\tbh\n");
                var state = new CoreRuntimeState(root); state.Initialize();
                using var engine = new InputMethodEngine(state, fullPinyinResources: new PinyinResources(lexicon, model, directory));
                KeyEngineResult Key(int vk, bool shift = false, bool ctrl = false) => engine.ProcessKey(vk, 0, "down", shift, ctrl, false, false, false, true, 1, false);
                void Type(string value) { foreach (char c in value) Key(char.ToUpperInvariant(c), char.IsUpper(c)); }
                void Wait() => True(SpinWait.SpinUntil(() => !engine.IsSentenceDecodePending, 5000), "expanded async completion");
                Type("nh"); Wait(); True(engine.GetUiSnapshot(5).Candidates.Contains("你好"), "Core defaults enable abbreviations"); Key(0x1b);
                Type("ni"); Wait(); Key(0x08, ctrl: true); Wait();
                True(engine.GetUiSnapshot(5).Candidates.Length == 0, "delete last syllable clears candidates");
                Type("hao"); Wait(); Equal("好", Key(0x20).TextToOutput, "fresh composition after syllable deletion");
                Type("yx"); Wait(); Equal("me@example.com\n第二行", Key(0x20).TextToOutput, "fixed phrase literal commit");
                Type("Windows"); Wait();
                Equal("Windows", Key(0x20).TextToOutput, "English exact match commits whole input");
                Type("Windowsma"); Wait();
                True(!engine.GetUiSnapshot(5).Candidates.Contains("Windows"), "English reverse-prefix candidate rejected"); Key(0x1b);
                Type("ni"); Key(0xc0); Type("k"); Wait();
                Equal("泥", engine.GetUiSnapshot(5).Candidates[0], "Tiger auxiliary through Core");
                Key(0x08); Key(0x08); Wait(); Equal("ni", Key(0x0d).TextToOutput, "auxiliary backspace leaves pinyin raw");
                Type("nihao"); Wait(); Equal("泥好", engine.GetUiSnapshot(5).Candidates[0], "pin retention and ranking");
                True(engine.TryManagePinyin("unpin", "nihao", "泥好", out _), "unpin management"); Wait();
                Equal("你好", engine.GetUiSnapshot(5).Candidates[0], "unpin restores model"); Key(0x1b);
                Type("user"); Key(0x32, true); Type("example"); Key(0xbe); Type("com");
                Equal("user@example.com", Key(0x20).TextToOutput, "email preserves punctuation");
                Type("https"); Key(0xba, true); Key(0xbf); Key(0xbf); Type("example"); Key(0xbe); Type("com");
                Equal("https://example.com", Key(0x0d).TextToOutput, "URL literal exit");
                Key(0xbf); Type("v"); Key(0x32); Key(0xbb, true); Key(0x33);
                Equal("5", Key(0x20).TextToOutput, "calculator through key routing");
                Key(0xbf); Type("r"); Key(0x31); Key(0x30); Key(0x31); Key(0xbe); Key(0x30); Key(0x35);
                Equal("壹佰零壹元零伍分", Key(0x20).TextToOutput, "amount digits are not selection keys");
                Type("nihao"); Wait(); Equal("好", Key(0xdd, ctrl: true).TextToOutput, "extract last character");
                state.TrySetConfigValue("拼音繁体输出", "是", out _, out _);
                Type("zhongguo"); Wait(); Equal("中國", engine.GetUiSnapshot(5).Candidates[0], "traditional candidate display");
                Equal("中國", Key(0x20).TextToOutput, "traditional commit");
                state.TrySetConfigValue("拼音繁体输出", "否", out _, out _);
                Type("nihao"); Wait();
                string actionToken = engine.GetUiSnapshot(5).CandidateSelectionToken;
                True(engine.SelectFullPinyinCandidate(actionToken, 16).TextToOutput == null, "mouse pin action never commits");
                Wait(); True(PinyinPreferences.Load(directory).IsPinned("nihao", "你好"), "mouse pin is durable");
                True(engine.SelectFullPinyinCandidate(actionToken, 48).TextToOutput == null, "stale management token rejected");
                Key(0x1b);
                var store = new SentenceLearningStore(Path.Combine(directory, ".tigerclaw-learning-pinyin-v1.log"));
                store.ConfirmAsync(new[] { new SentenceLearningEvent { Mode = "full-pinyin-v1", Code = "nihao", Text = "泥好" } }); store.FlushAsync().GetAwaiter().GetResult();
                store.ForgetAsync("full-pinyin-v1", "nihao", "泥好"); store.FlushAsync().GetAwaiter().GetResult();
                True(store.Entries().Length == 0, "forget persisted tombstones");
                var reopened = new SentenceLearningStore(store.Path); reopened.Refresh(); True(reopened.Entries().Length == 0, "forget survives restart");
                store.ConfirmAsync(new[] { new SentenceLearningEvent { Mode = "full-pinyin-v1", Code = "nihao", Text = "泥好" } }); store.FlushAsync().GetAwaiter().GetResult();
                True(store.Entries().Length == 1, "new correction may relearn after forget");
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("Expanded pinyin spelling/preferences/English/tools/double-pinyin tests passed.");
        }
    }
}
