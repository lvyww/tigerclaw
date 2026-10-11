using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using TigerClaw.Core;
using TigerClaw.Overlay;
using TigerClaw.Shared;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void CandidateLayoutSettings()
        {
            string root = Path.Combine(Path.GetTempPath(), "TigerClaw-Layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "config.txt"), "竖排候选\t否\n隐藏候选\t是\n候选窗显示编码\t是\n");
                var state = new CoreRuntimeState(root);
                state.Initialize();
                True(state.GetCandidateLayout() == 4, "old settings do not migrate");
                foreach (string old in new[] { "竖排候选", "隐藏候选", "候选窗显示编码" })
                {
                    True(!state.GetConfigText().Contains(old), "old key not exported: " + old);
                    True(!state.TrySetConfigValue(old, "是", out _, out _), "old key rejected: " + old);
                }
                var formatter = new CandidateTextFormatter();
                for (int mode = 1; mode <= 5; ++mode)
                {
                    True(state.TrySetConfigValue(CandidateLayouts.ConfigKey, mode.ToString(), out _, out _), "set layout");
                    state.ReloadConfig();
                    True(state.GetCandidateLayout() == mode, "layout persists");
                    True(CandidateLayouts.Next(mode) == mode % 5 + 1, "five-mode cycle");
                    var ui = new OverlayUiState { CandidateLayout = mode, CandidateVisible = true,
                        InputCode = "ab", Candidates = new[] { "A", "B" }, CandidateExpandDelayMs = 100 };
                    var view = formatter.BuildViewModel(ui, true, false);
                    True(view.Mode == (mode == 5 ? CandidateDisplayMode.CodeOnly : mode <= 2 ?
                        CandidateDisplayMode.CodeAndCandidates : CandidateDisplayMode.CandidatesOnly), "render mode");
                    using (var stream = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(OverlayUiState)).WriteObject(stream, ui);
                        string json = Encoding.UTF8.GetString(stream.ToArray());
                        True(json.Contains("\"CandidateLayout\":" + mode) && !json.Contains("VerticalCandidates") &&
                            !json.Contains("HideCandidateItems") && !json.Contains("ShowInputCodeInCandidateWindow"), "single wire field");
                    }
                }
                foreach (string invalid in new[] { "0", "6", "-1", "", "yes", "01", "1.0" })
                {
                    True(!state.TrySetConfigValue(CandidateLayouts.ConfigKey, invalid, out _, out _), "invalid setter rejected");
                    File.WriteAllText(Path.Combine(root, "config.txt"), "候选布局\t" + invalid + "\n");
                    state.ReloadConfig();
                    True(state.GetCandidateLayout() == 4, "invalid file defaults");
                }
                state.TryToggleHideCandidateItems(out bool hidden);
                True(hidden && state.GetCandidateLayout() == 5, "command enters code-only");
                state.TryToggleHideCandidateItems(out hidden);
                True(!hidden && state.GetCandidateLayout() == 4, "command returns to default");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
