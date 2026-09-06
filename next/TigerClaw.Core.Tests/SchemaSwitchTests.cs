using System;
using System.Collections.Generic;
using System.IO;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void SchemaSwitchPreservesOnlyLiveRaw()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw.Core.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string schema in new[] { "普通A", "普通B", "整句A", "整句B" })
                {
                    string dir = Path.Combine(root, "码表", schema);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, schema + ".txt"),
                        (schema == "普通A" ? "甲" : "丙") + "\tab\n乙\tcd\n");
                }
                var state = new CoreRuntimeState(root);
                state.Initialize();
                state.TrySetConfigValue("当前码表", "普通A", out _, out _);
                state.ReloadLexicon();
                state.TrySetConfigValue("最大码长", "2", out _, out _);
                state.TrySetConfigValue("最大码长无重自动上屏", "否", out _, out _);
                state.TrySetConfigValue("中英文不限长混合输入", "是", out _, out _);
                using (var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" }
                })))
                {
                    void Switch(string schema)
                    {
                        state.TrySetConfigValue("当前码表", schema, out _, out _);
                        state.ReloadLexicon();
                        engine.RefreshCompositionAfterSchemaSwitch();
                    }

                    TypeLetters(engine, "abc");
                    Equal("甲", engine.GetUiSnapshot(5).CompositionPrefix, "switch.old_prefix");
                    Switch("普通B");
                    Equal("丙", engine.GetUiSnapshot(5).CompositionPrefix, "switch.new_prefix");
                    Press(engine, 0x44);
                    Equal("丙乙", Press(engine, 0x20).TextToOutput, "switch.new_commit");

                    Press(engine, 0x41);
                    Press(engine, 0x42, shift: true);
                    Switch("整句A");
                    Equal("aB", engine.GetDifferentialSnapshot(5).RawInput, "switch.case_raw");
                    Equal("aB", engine.GetUiSnapshot(5).ActiveInputCode, "switch.case_display");
                    True(!engine.IsSentenceDecodePending, "switch.case_decode_complete");
                    Switch("普通A");
                    Equal("aB", Press(engine, 0x0D).TextToOutput, "switch.case_literal");

                    Switch("整句A");
                    TypeLetters(engine, "a");
                    state.TrySetConfigValue("中英文不限长混合输入", "否", out _, out _);
                    state.TrySetConfigValue("最大码长无重自动上屏", "是", out _, out _);
                    Switch("普通A");
                    Equal("甲", Press(engine, 0x42).TextToOutput, "switch.short_auto_commit");

                    Switch("整句A");
                    TypeLetters(engine, "abcd");
                    Switch("普通B");
                    Equal("abcd", engine.GetDifferentialSnapshot(5).RawInput, "switch.long_raw");
                    Press(engine, 0x08);
                    Equal("abc", engine.GetDifferentialSnapshot(5).RawInput, "switch.long_backspace");
                    Equal("abc", Press(engine, 0x0D).TextToOutput, "switch.long_literal");

                    Switch("整句A");
                    EnableSentenceEarlyCommit(state);
                    TypeLetters(engine, "ab");
                    Equal("甲", Press(engine, 0x43).TextToOutput, "switch.early_commit");
                    Switch("整句B");
                    Equal("c", engine.GetDifferentialSnapshot(5).RawInput, "switch.live_tail");
                    Equal("", engine.GetDifferentialSnapshot(5).SentenceCommittedText, "switch.no_old_context");
                    Press(engine, 0x44);
                    Equal("乙", Press(engine, 0x20).TextToOutput, "switch.no_duplicate_commit");

                    TypeLetters(engine, "ab");
                    Equal("甲", Press(engine, 0x43).TextToOutput, "switch.normal_early_commit");
                    Switch("普通B");
                    Equal("c", engine.GetDifferentialSnapshot(5).RawInput, "switch.normal_live_tail");
                    Press(engine, 0x08);
                    True(!engine.GetUiSnapshot(5).IsComposing, "switch.tail_backspace_ends_composition");
                    Equal("", engine.GetDifferentialSnapshot(5).RawInput, "switch.no_committed_raw_after_backspace");
                }

                state.TrySetConfigValue("中英文不限长混合输入", "是", out _, out _);
                state.TrySetConfigValue("当前码表", "普通A", out _, out _);
                state.ReloadLexicon();
                using (var handler = new ProtocolHandler(_ => { }, state, null))
                {
                    foreach (int vk in new[] { 65, 66, 67 })
                        handler.Handle("{\"type\":\"key\",\"action\":\"down\",\"vk\":" + vk + "}");
                    handler.Handle("{\"type\":\"set_config\",\"key\":\"当前码表\",\"value\":\"普通B\"}");
                    string snapshot = handler.BuildDifferentialSnapshotJson();
                    True(snapshot.Contains("\"composition_prefix\":\"丙\""), "switch.menu_new_prefix");
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
