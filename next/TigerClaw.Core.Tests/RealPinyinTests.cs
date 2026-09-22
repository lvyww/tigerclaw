using System;
using System.IO;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Explicit fixture root: no IPC, TSF registration or application input.
        // The caller supplies fresh test-owned tables/config; never a daily root.
        private static int RunRealPinyinTests(string root)
        {
            if (!File.Exists(Path.Combine(root, "full-pinyin-test-fixture")))
                throw new InvalidOperationException("Expected an isolated full-pinyin test fixture");
            var state = new CoreRuntimeState(root); state.Initialize();
            True(state.TrySetConfigValue("当前码表", "虎爪全拼", out _, out _), "real full-pinyin scheme initialization");
            // Reproduce the real legacy record that previously promoted 是以下.
            var legacyStore = new SentenceLearningStore(Path.Combine(state.GetFullPinyinDirectory(), ".tigerclaw-learning-pinyin-v1.log"));
            legacyStore.ConfirmAsync(new[] { new SentenceLearningEvent { Mode = SentenceLearning.PinyinPhraseMode, Code = "shi", Text = "是" } });
            legacyStore.FlushAsync().GetAwaiter().GetResult();
            using var engine = new InputMethodEngine(state);
            KeyEngineResult Key(int vk, bool shift = false) => engine.ProcessKey(vk, 0, "down", shift, false, false, false, false, true, 1, false);
            void Type(string value) { foreach (char c in value) Key(char.ToUpperInvariant(c), char.IsUpper(c)); }
            void Wait() => True(SpinWait.SpinUntil(() => !engine.IsSentenceDecodePending, 30000), "real resources decode completion");
            KeyEngineResult Pick(string value)
            {
                Wait();
                for (int n = 0; n < 200; n++)
                {
                    var ui = engine.GetUiSnapshot(5);
                    if (ui.Candidates.Length > ui.SelectedCandidateIndex && ui.Candidates[ui.SelectedCandidateIndex] == value)
                        return Key(0x20);
                    Key(0x28);
                }
                throw new InvalidOperationException("Real candidate missing: " + value);
            }
            foreach (var item in new[] { ("nh", "你好"), ("nhao", "你好"), ("zhongg", "中国"), ("nve", "虐"), ("nver", "女儿") })
            { Type(item.Item1); Wait(); Equal(item.Item2, engine.GetUiSnapshot(5).Candidates[0], "real default first " + item.Item1); Equal(item.Item2, Key(0x20).TextToOutput, "real default commit"); }
            Type("shiyixia"); Wait(); Equal("试一下", engine.GetUiSnapshot(5).Candidates[0], "legacy shi character learning does not affect whole sentence"); Key(0x1b);
            Type("Windowsma"); Wait(); True(Array.IndexOf(engine.GetUiSnapshot(9).Candidates, "Windows") < 0, "real English reverse-prefix rejected"); Key(0x1b);
            Type("Windows"); Equal("Windows", Pick("Windows").TextToOutput, "real English exact match");
            Type("Wind"); Equal("Windows", Pick("Windows").TextToOutput, "real English completion");
            Type("windowsbanben"); Equal("Windows版本", Pick("Windows版本").TextToOutput, "real mixed-word resource");
            Type("ni"); Key(0xc0); Type("kcvb"); Wait(); Equal("泥", Key(0x20).TextToOutput, "real Tiger auxiliary resource");
            state.TrySetConfigValue("拼音繁体输出", "是", out _, out _);
            Type("zhongguo"); Wait(); Equal("中國", Key(0x20).TextToOutput, "real traditional output");
            state.TrySetConfigValue("拼音繁体输出", "否", out _, out _);
            Type("changyongzi"); Wait();
            var boundaryMenu = engine.GetUiSnapshot(9);
            True(Array.TrueForAll(boundaryMenu.Candidates, c => c is not ("差" or "查" or "chan" or "chang")), "real chang boundary and English full-input matching");
            True(Pick("常用").TextToOutput == null, "real changyong prefix locks");
            Equal("常用字", Pick("字").TextToOutput, "real legal prefix continuation");
            foreach (string prefix in new[] { "你", "你好" })
            {
                Type("nihaoma");
                True(Pick(prefix).TextToOutput == null, "real full-spelling prefix locks " + prefix);
                Equal("你好吗", Pick(prefix == "你" ? "好吗" : "吗").TextToOutput, "real full-spelling prefix continuation " + prefix);
            }
            // Change the scheme through the same settings path used by Dialog.
            True(state.TrySetConfigValue("当前码表", "虎爪小鹤双拼", out _, out _), "real Xiaohe schema switch");
            foreach (var item in new[] { ("nihc", "你好"), ("vsgo", "中国"), ("ybhhka", "银行卡"), ("isqk", "重庆") })
            { Type(item.Item1); Wait(); Equal(item.Item2, Key(0x20).TextToOutput, "real Xiaohe commit " + item.Item1); }
            Console.WriteLine("Real-model Core spelling/English/mixed/auxiliary/traditional/schema-switch tests passed.");
            return 0;
        }
    }
}
