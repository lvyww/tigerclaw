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
                foreach (int layout in new[] { 3, 4 })
                {
                    var indexed = new OverlayUiState { CandidateLayout = layout, CandidateVisible = true,
                        ShowCandidateIndex = true, Candidates = new[] { "词", "𠀀" },
                        CandidateAnnotations = new[] { "ci", "" }, SelectedCandidateIndex = 1 };
                    var view = formatter.BuildViewModel(indexed, true, true);
                    string expected = layout == 3 ? "词〔ci〕 𠀀²" : "词〔ci〕\n𠀀²";
                    True(view.DisplayText == expected, "first index omitted, other superscripts follow word");
                    True(view.DisplayText.Substring(view.SelectionStart, view.SelectionLength) == "𠀀²",
                        "Unicode word and superscript share highlight");
                    True(view.AnnotationRanges.Count == 1 && view.DisplayText.Substring(
                        view.AnnotationRanges[0].Start, view.AnnotationRanges[0].Length) == "〔ci〕", "explicit annotation span");
                    var reversed = formatter.BuildViewModel(indexed, true, true, true);
                    True(reversed.DisplayText.Substring(reversed.AnnotationRanges[0].Start,
                        reversed.AnnotationRanges[0].Length) == "〔ci〕", "annotation span follows reversed row");
                    indexed.Candidates = new string[10];
                    for (int i = 0; i < 10; ++i) indexed.Candidates[i] = "词";
                    True(formatter.BuildViewModel(indexed, true, false).DisplayText.EndsWith("词¹⁰"),
                        "multi-digit superscript");
                    indexed.ShowCandidateIndex = false;
                    True(!formatter.BuildViewModel(indexed, true, false).DisplayText.Contains("¹"), "index switch off");
                }
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
