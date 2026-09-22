using System;
using System.IO;
using System.Threading;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // The supplied root is a fresh, test-owned copy of the real model and
        // schemas. Exercise receipts locally; never send keys to production IPC.
        private static int RunPinyinFeedbackTests(string root, bool fivegram = false)
        {
            string raw = fivegram ? "yaoyouhuahenjiu" : "yaoyouhuahaojiu";
            string target = fivegram ? "要优化很久" : "要优化好久";
            if (!File.Exists(Path.Combine(root, "full-pinyin-test-fixture")))
                throw new InvalidOperationException("Expected an isolated full-pinyin fixture");
            string journal = Path.Combine(root, "tables", "虎爪全拼", ".tigerclaw-learning-pinyin-v1.log");
            if (File.Exists(journal)) throw new InvalidOperationException("Use a fresh fixture to test a first correction");
            var state = new CoreRuntimeState(root); state.Initialize();
            True(state.TrySetConfigValue("当前码表", "虎爪全拼", out _, out _), "feedback full-pinyin scheme");
            using (var engine = new InputMethodEngine(state))
            {
                KeyEngineResult Key(int vk) => engine.ProcessKey(vk, 0, "down", false, false, false, false, false, true, 1, false);
                void Type() { foreach (char c in raw) Key(char.ToUpperInvariant(c)); }
                void Wait() => True(SpinWait.SpinUntil(() => !engine.IsSentenceDecodePending, 30000), "feedback decode completion");
                Type(); Wait(); Key(0x71); // F2: whole-sentence candidates.
                var menu = engine.GetUiSnapshot(5);
                int index = Array.IndexOf(menu.Candidates, target);
                True(index > 0, "reported correction is a non-first candidate on page one");
                Console.WriteLine("Before correction: " + menu.Candidates[0] + "; target rank=" + (index + 1));
                var result = Key(0x31 + index);
                Equal(target, result.TextToOutput, "F2 plus number chooses the whole intended sentence");
                var events = engine.TakeSentenceLearning(result, out var store);
                True(events.Length == 1 && store != null, "explicit correction produces one receipt-bound event");
                True(!File.Exists(journal), "selection alone does not write learning");
                var receipts = new SentenceLearningReceipts();
                string receipt = receipts.Issue("pinyin-feedback", store, events);
                True(receipts.Acknowledge("pinyin-feedback", receipt, true), "successful insertion receipt accepted");
                True(!receipts.Acknowledge("pinyin-feedback", receipt, true), "duplicate receipt ignored");
                store.FlushAsync().GetAwaiter().GetResult();
                Type(); Wait(); Equal(target, engine.GetUiSnapshot(5).Candidates[0], "one acknowledged correction promotes intended text");
                Key(0x1b);
            }
            var reopenedState = new CoreRuntimeState(root); reopenedState.Initialize();
            using (var reopened = new InputMethodEngine(reopenedState))
            {
                foreach (char c in raw) reopened.ProcessKey(char.ToUpperInvariant(c), 0, "down", false, false, false, false, false, true, 1, false);
                True(SpinWait.SpinUntil(() => !reopened.IsSentenceDecodePending, 30000), "restarted feedback decode completion");
                Equal(target, reopened.GetUiSnapshot(5).Candidates[0], "correction survives a new engine and model load");
                reopened.OnExternalCompositionCanceled();
            }
            Console.WriteLine("Real-model feedback correction and restart tests passed: " + target);
            return 0;
        }
    }
}
