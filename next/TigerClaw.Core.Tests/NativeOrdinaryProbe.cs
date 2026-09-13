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
        private static object NativeOrdinaryReference(JsonElement input)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            string root = Path.Combine(Path.GetTempPath(), "tiger-ordinary-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var runtime = new CoreRuntimeState(root); // no Initialize, models, or production files
                var table = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in input.GetProperty("entries").EnumerateArray())
                    table[entry.GetProperty("code").GetString()] = entry.GetProperty("texts").EnumerateArray().Select(t => t.GetString()).ToList();
                typeof(CoreRuntimeState).GetField("_lexicon", fields).SetValue(runtime, CompactLexicon.Build(table));
                typeof(CoreRuntimeState).GetField("_pinyinLexicon", fields).SetValue(runtime, CompactLexicon.Build(table));
                object[] meta = { table, null, null };
                typeof(CoreRuntimeState).GetMethods(statics).Single(m => m.Name == "RebuildMeta" && m.GetParameters()[0].ParameterType == typeof(Dictionary<string, List<string>>)).Invoke(null, meta);
                typeof(CoreRuntimeState).GetField("_unique", fields).SetValue(runtime, meta[1]);
                typeof(CoreRuntimeState).GetField("_nonTerminal", fields).SetValue(runtime, meta[2]);
                object[] shortMeta = { table, false, false, false, false, null };
                typeof(CoreRuntimeState).GetMethods(statics).Single(m => m.Name == "RebuildShortSymbolMeta" && m.GetParameters()[0].ParameterType == typeof(Dictionary<string, List<string>>)).Invoke(null, shortMeta);
                string[] shortFields = { "_shortSymbolSemicolon", "_shortSymbolSlash", "_shortSymbolLBracket", "_shortSymbolZ", "_autoShortSymbol" };
                for (int i = 0; i < shortFields.Length; ++i)
                    typeof(CoreRuntimeState).GetField(shortFields[i], fields).SetValue(runtime, shortMeta[i + 1]);
                var config = (Dictionary<string, string>)typeof(CoreRuntimeState).GetField("_config", fields).GetValue(runtime);
                foreach (var pair in new[] { ("KeyMaxCodeLen", "max"), ("KeyPageSize", "size"), ("KeyMaxAuto", "auto"), ("KeyClearOnNoCode", "clear"), ("KeyEnterClear", "enter"), ("KeyTabClear", "tab") })
                    config[(string)typeof(CoreRuntimeState).GetField(pair.Item1, statics).GetRawConstantValue()] = input.GetProperty(pair.Item2).ToString();
                foreach (var pair in new[] { ("KeyCnUseEnPunc", "english"), ("KeySlashDunhao", "slash"), ("KeySemicolon2", "second"), ("KeyQuote3", "third") })
                    if (input.TryGetProperty(pair.Item2, out var setting))
                        config[(string)typeof(CoreRuntimeState).GetField(pair.Item1, statics).GetRawConstantValue()] = setting.ToString();
                using var engine = new InputMethodEngine(runtime);
                if (input.TryGetProperty("mixed", out var mixedSetting))
                    config[(string)typeof(CoreRuntimeState).GetField("KeyUnlimitedMixedChineseEnglishInput", statics).GetRawConstantValue()] = mixedSetting.ToString();
                var buffer = (StringBuilder)typeof(InputMethodEngine).GetField("_inputBuffer", fields).GetValue(engine);
                var mixedBuffer = (StringBuilder)typeof(InputMethodEngine).GetField("_mixedRawBuffer", fields).GetValue(engine);
                var mode = typeof(InputMethodEngine).GetField("_compositionState", fields);
                bool chineseTrace = input.TryGetProperty("chinese", out var chineseSetting) && chineseSetting.GetBoolean();
                if (chineseTrace) mode.SetValue(engine, Enum.ToObject(mode.FieldType, 1));
                var pageMethod = typeof(InputMethodEngine).GetMethod("GetCandidatePage", fields);
                var trace = new List<object>();
                bool uppercaseTrace = input.TryGetProperty("uppercase", out var uppercaseSetting) && uppercaseSetting.GetBoolean();
                bool pinyinTrace = input.TryGetProperty("pinyin", out var pinyinSetting) && pinyinSetting.GetBoolean();
                foreach (var key in input.GetProperty("ordinary_trace").EnumerateArray())
                {
                    int vk = key.ValueKind == JsonValueKind.Object ? key.GetProperty("vk").GetInt32() : key.GetInt32();
                    bool shift = key.ValueKind == JsonValueKind.Object && key.GetProperty("shift").GetBoolean();
                    if (chineseTrace)
                    {
                        string branch = Convert.ToInt32(mode.GetValue(engine)) switch
                        {
                            0 => null,
                            1 => "ProcessCnIdleKeyDown", 2 => "ProcessCnComposingKeyDown",
                            3 => "ProcessCnUpperCaseKeyDown", 4 => "ProcessCnPinyinKeyDown",
                            _ => throw new InvalidOperationException("Unexpected Chinese mode")
                        };
                        bool languageChange = key.ValueKind == JsonValueKind.Object && key.TryGetProperty("language", out _);
                        bool schemaChange = key.ValueKind == JsonValueKind.Object && key.TryGetProperty("schema_max", out _);
                        bool physical = input.TryGetProperty("physical", out var physicalValue) && physicalValue.GetBoolean();
                        bool eventUp = physical && key.TryGetProperty("up", out var upValue) && upValue.GetBoolean();
                        bool eventCtrl = physical && key.TryGetProperty("ctrl", out var ctrlValue) && ctrlValue.GetBoolean();
                        KeyEngineResult cnResult;
                        if (schemaChange)
                        {
                            config[(string)typeof(CoreRuntimeState).GetField("KeyMaxCodeLen", statics).GetRawConstantValue()] = key.GetProperty("schema_max").ToString();
                            config[(string)typeof(CoreRuntimeState).GetField("KeyUnlimitedMixedChineseEnglishInput", statics).GetRawConstantValue()] = key.GetProperty("schema_mixed").ToString();
                            engine.RefreshCompositionAfterSchemaSwitch();
                            cnResult = KeyEngineResult.CreateHandled(engine.IsChinese, "", "", false);
                        }
                        else if (physical) cnResult = engine.ProcessKey(vk, 0, eventUp ? "up" : "down", shift, eventCtrl, false, false, false, false, 1, false);
                        else if (languageChange)
                        {
                            engine.SetChinese(key.GetProperty("language").GetBoolean(), out string languageCommit);
                            cnResult = KeyEngineResult.CreateHandled(engine.IsChinese, languageCommit, "", false);
                        }
                        else if (vk == 0x14) cnResult = engine.ProcessKey(vk, 0, "down", shift, false, false, false, false, false, 1, false);
                        else cnResult = branch == null ? KeyEngineResult.Pass(false) : (KeyEngineResult)typeof(InputMethodEngine).GetMethod(branch, fields).Invoke(engine, new object[] { vk, shift });
                        if (!languageChange && !schemaChange && input.TryGetProperty("postprocess", out var postprocess) && postprocess.GetBoolean())
                            engine.PostProcessKey(vk, eventUp ? "up" : "down", cnResult, shift, eventCtrl, false, false, false);
                        string cnRaw = buffer.ToString();
                        int cnMode = Convert.ToInt32(mode.GetValue(engine));
                        var cnPage = new List<string>();
                        if (cnMode == 2 || cnMode == 4)
                        {
                            object candidates = cnMode == 4 ? runtime.GetPinyinCandidates(cnRaw.Substring(1)) : runtime.GetCandidateView(cnRaw);
                            object[] args = { cnRaw, mode.GetValue(engine), candidates, 0 };
                            cnPage = (List<string>)pageMethod.Invoke(engine, args) ?? new List<string>();
                        }
                        trace.Add(new { handled = cnResult.Handled, text = cnResult.TextToOutput ?? "", raw = cnRaw, full = mixedBuffer.Length > 0 ? mixedBuffer.ToString() : cnRaw,
                            composing = cnResult.IsComposing, buffer = cnResult.InputBuffer ?? "", page = cnPage, mode = cnMode });
                        continue;
                    }
                    if (pinyinTrace)
                    {
                        bool start = buffer.Length == 0;
                        var pyResult = (KeyEngineResult)typeof(InputMethodEngine).GetMethod(start ? "ProcessCnIdleKeyDown" : "ProcessCnPinyinKeyDown", fields)
                            .Invoke(engine, new object[] { start ? 192 : vk, start ? false : shift });
                        string pyRaw = buffer.ToString();
                        var candidates = runtime.GetPinyinCandidates(pyRaw.Length > 0 ? pyRaw.Substring(1) : "");
                        object[] args = { pyRaw, mode.GetValue(engine), candidates, 0 };
                        var pyPage = (List<string>)pageMethod.Invoke(engine, args);
                        trace.Add(new { handled = pyResult.Handled, text = pyResult.TextToOutput ?? "", raw = pyRaw,
                            composing = pyResult.IsComposing, buffer = pyResult.InputBuffer ?? "", page = pyPage ?? new List<string>() });
                        continue;
                    }
                    if (uppercaseTrace)
                    {
                        bool start = buffer.Length == 0;
                        var upperResult = (KeyEngineResult)typeof(InputMethodEngine).GetMethod(start ? "ProcessCnIdleKeyDown" : "ProcessCnUpperCaseKeyDown", fields)
                            .Invoke(engine, new object[] { start ? (input.TryGetProperty("upper_start", out var startKey) ? startKey.GetInt32() : 65) : vk, start ? true : shift });
                        trace.Add(new { handled = upperResult.Handled, text = upperResult.TextToOutput ?? "", raw = buffer.ToString(),
                            composing = upperResult.IsComposing, buffer = upperResult.InputBuffer ?? "", page = new List<string>() });
                        continue;
                    }
                    if (buffer.Length == 0)
                    {
                        bool shortStart = (vk == 186 && (bool)shortMeta[1]) || (vk == 191 && (bool)shortMeta[2]) || (vk == 219 && (bool)shortMeta[3]);
                        if ((vk < 65 || vk > 90) && !shortStart) vk = 65;
                        shift = false;
                    }
                    string method = buffer.Length == 0 ? "ProcessCnIdleKeyDown" : "ProcessCnComposingKeyDown";
                    var result = (KeyEngineResult)typeof(InputMethodEngine).GetMethod(method, fields).Invoke(engine, new object[] { vk, shift });
                    string raw = buffer.ToString();
                    object[] pageArgs = { raw, mode.GetValue(engine), runtime.GetCandidateView(raw), 0 };
                    var page = (List<string>)pageMethod.Invoke(engine, pageArgs);
                    trace.Add(new { handled = result.Handled, text = result.TextToOutput ?? "", raw,
                        composing = result.IsComposing, buffer = result.InputBuffer ?? "", page = page ?? new List<string>() });
                }
                return trace;
            }
            finally { Directory.Delete(root, true); } // only the GUID-owned fixture
        }
    }
}
