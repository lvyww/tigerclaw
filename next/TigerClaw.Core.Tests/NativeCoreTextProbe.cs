using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunNativeCoreTextProbe()
        {
            Func<string, string> Bind(string name) => typeof(CoreRuntimeState)
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                .CreateDelegate<Func<string, string>>();
            var comment = Bind("StripInlineComment");
            var decode = Bind("WrapDec");
            var token = Bind("ParseLexiconEntryToken");
            var normalize = Bind("NormalizeCode");
            var defaultCulture = CultureInfo.CurrentCulture;
            string sentenceModelPath = Environment.GetEnvironmentVariable("TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL");
            using var sentenceModel = string.IsNullOrEmpty(sentenceModelPath) ? null : SentenceNgramModel.Load(sentenceModelPath);
            int[] Units(string value) => value.Select(c => (int)c).ToArray();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("neural_candidates", out var neuralInput))
                {
                    var candidates = neuralInput.EnumerateArray().Select(value => new SentenceCandidate
                    {
                        Text = new string(value.GetProperty("text").EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray()),
                        BaseScore = value.GetProperty("base").GetDouble(), FinalScore = value.GetProperty("base").GetDouble(),
                        MaxLexiconRank = value.GetProperty("rank").GetInt32(),
                        Boundary = value.GetProperty("segmented").GetBoolean()
                            ? new SentencePathBoundary { Previous = new SentencePathBoundary() } : null
                    }).ToArray();
                    var original = candidates.ToArray();
                    int count = Math.Min(5, candidates.Length);
                    bool scoreFirst = document.RootElement.GetProperty("duplicates").GetBoolean() &&
                        candidates.Take(count).Any(c => c.Boundary?.Previous != null);
                    int baseLength = InputMethodEngine.GetSentenceNeuralBaseTopLength(candidates, count, scoreFirst);
                    for (int i = 0; i < count; ++i)
                        candidates[i].FinalScore = InputMethodEngine.CombineSentenceNeuralCandidateScore(candidates[i].BaseScore,
                            neuralInput[i].GetProperty("neural").GetDouble(), baseLength,
                            new StringInfo(candidates[i].Text).LengthInTextElements);
                    Array.Sort(candidates, 0, count, Comparer<SentenceCandidate>.Create(scoreFirst
                        ? SentenceCandidate.CompareByScoreThenLexiconRank : SentenceCandidate.CompareByLexiconRankThenScore));
                    Console.WriteLine(JsonSerializer.Serialize(candidates.Select(c => new { index = Array.IndexOf(original, c), score = c.FinalScore })));
                    continue;
                }
                if (document.RootElement.TryGetProperty("prefix_evidence", out var prefixInput))
                {
                    var candidates = prefixInput.EnumerateArray().Select(value =>
                    {
                        var candidate = new SentenceCandidate { Text = new string(value.GetProperty("text").EnumerateArray()
                            .Select(c => checked((char)c.GetInt32())).ToArray()), ConfidenceScore = value.GetProperty("mass").GetDouble() };
                        foreach (var boundary in value.GetProperty("boundaries").EnumerateArray())
                            candidate.Boundary = new SentencePathBoundary { Previous = candidate.Boundary,
                                TextLength = boundary[0].GetInt32(), RawLength = boundary[1].GetInt32() };
                        return candidate;
                    }).ToArray();
                    var evidence = (SentencePrefixEvidence[])typeof(SentenceInputDecoder).GetMethod("BuildPrefixEvidence",
                        BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { candidates });
                    Console.WriteLine(JsonSerializer.Serialize(evidence.Select(item => new { text = Units(item.Text), raw = item.RawLength,
                        share = item.Share, boundary_share = item.BoundaryShare, closed = item.BoundaryClosed })));
                    continue;
                }
                if (document.RootElement.TryGetProperty("supplement_dir", out var supplementDirectory))
                {
                    Console.WriteLine(JsonSerializer.Serialize(CoreRuntimeState.LoadSentenceSupplements(supplementDirectory.GetString())
                        .Select(entry => new { text = Units(entry.Text), weight = entry.Weight, reward = entry.Reward })));
                    continue;
                }
                if (document.RootElement.TryGetProperty("sentence_rank_queries", out var rankQueries))
                {
                    var values = rankQueries.EnumerateArray().Select(value => SentenceCharacterRanks.GetRank(
                        new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray()))).ToArray();
                    var tops = new[] { -1, 0, 1, 1500, 6000, 100000 }.Select(count =>
                        SentenceCharacterRanks.TakeTop(count).Select(SentenceCharacterRanks.GetRank).OrderBy(rank => rank).ToArray()).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new { ranks = values, tops }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("sentence_raw", out var sentenceRaw))
                {
                    var root = document.RootElement;
                    string ReadUnits(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var sentenceEntries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var row in root.GetProperty("entries").EnumerateArray())
                        sentenceEntries[ReadUnits(row[0])] = row[1].EnumerateArray().Select(ReadUnits).ToList();
                    var common = root.GetProperty("common").EnumerateArray().Select(ReadUnits).ToHashSet(StringComparer.Ordinal);
                    var whitelist = root.GetProperty("whitelist").EnumerateArray().Select(ReadUnits).ToHashSet(StringComparer.Ordinal);
                    var isolation = SentenceIsolationPenalty.None;
                    var supplements = SentenceSupplementMatcher.Empty;
                    if (root.TryGetProperty("supplements", out var supplementInput))
                        supplements = SentenceSupplementMatcher.Build(supplementInput.EnumerateArray().Select(entry =>
                            SentenceSupplementEntry.Create(ReadUnits(entry[0]), entry[1].GetInt64())));
                    if (root.TryGetProperty("isolation", out var isolationInput))
                        isolation = new SentenceIsolationPenalty { RankThreshold = isolationInput.GetProperty("threshold").GetInt32(),
                            Lambda = isolationInput.GetProperty("lambda").GetDouble(), UseLogRank = isolationInput.GetProperty("log").GetBoolean() };
                    var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(sentenceEntries, common, whitelist),
                        (ISentenceLanguageModel)sentenceModel ?? NeutralSentenceLanguageModel.Instance, beamWidth: root.GetProperty("beam").GetInt32(),
                        scoreSentenceBoundaries: !root.TryGetProperty("boundaries", out var boundaries) || boundaries.GetBoolean(),
                        rankPenalty: root.GetProperty("rank_penalty").GetDouble(), isolationPenalty: isolation,
                        emittedCharacterReward: root.GetProperty("reward").GetDouble(),
                        wholeInputSingleCharacterReward: root.GetProperty("single_reward").GetDouble(),
                        supplementMatcher: supplements,
                        allowDuplicateSingleCharacters: root.GetProperty("duplicates").GetBoolean());
                    if (root.TryGetProperty("path_queries", out var pathQueries))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(pathQueries.EnumerateArray().Select(query => decoder.HasCompleteCandidate(
                            ReadUnits(sentenceRaw), ReadUnits(query.GetProperty("required")),
                            query.GetProperty("excluded").ValueKind == JsonValueKind.Null ? null : ReadUnits(query.GetProperty("excluded")),
                            query.GetProperty("group").GetBoolean())).ToArray()));
                        continue;
                    }
                    bool incremental = root.TryGetProperty("sequence", out var sequence);
                    bool includeEvidence = root.TryGetProperty("evidence_required", out var evidenceRequired);
                    string requiredPrefix = includeEvidence ? ReadUnits(evidenceRequired) : null;
                    var raws = incremental ? sequence.EnumerateArray().ToArray() : new[] { sentenceRaw };
                    foreach (var raw in raws)
                    {
                        var result = incremental ? decoder.Decode(ReadUnits(raw), root.GetProperty("limit").GetInt32(), includeEvidence, requiredPrefix) :
                            decoder.DecodeFull(ReadUnits(raw), root.GetProperty("limit").GetInt32(), includeEvidence, requiredPrefix);
                        var early = result.EarlyCommitEvidence;
                        object evidence = includeEvidence ? new { prefixes = early.Prefixes.Select(prefix => new { text = Units(prefix.Text), raw = prefix.RawLength,
                            share = prefix.Share, boundary_share = prefix.BoundaryShare, closed = prefix.BoundaryClosed }),
                            lengths = early.RawLengths.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new object[] { Units(pair.Key), pair.Value }),
                            proposal = Units(early.Proposal), share = early.ProposalShare, neutral_tail = early.NeutralIncompleteTail,
                            merged_tail = early.MergedIncompleteTail, low = early.NeutralLowConfidence, truncated = early.ConfidenceTruncated } : null;
                        Console.WriteLine(JsonSerializer.Serialize(new { raw = Units(result.RawCode), expanded = result.ExpandedStates,
                            candidates = result.Candidates.Select(c => new { text = Units(c.Text), code = Units(c.SegmentedCode),
                                score = c.BaseScore, mass = c.ConfidenceScore, rank = c.MaxLexiconRank, supplement = c.SupplementScore }), evidence }));
                    }
                    continue;
                }
                if (document.RootElement.TryGetProperty("ngram_path", out var ngramPath))
                {
                    using var model = SentenceNgramModel.Load(ngramPath.GetString());
                    string ReadToken(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var results = new List<object>();
                    foreach (var query in document.RootElement.GetProperty("queries").EnumerateArray())
                    {
                        string a = ReadToken(query[0]), b = ReadToken(query[1]), c = ReadToken(query[2]);
                        results.Add(new object[] { model.LogProbability(a, b, c, true), model.LogProbability(a, b, c, false), model.HasObservedBigram(b, c) });
                    }
                    Console.WriteLine(JsonSerializer.Serialize(results));
                    continue;
                }
                if (document.RootElement.TryGetProperty("mixed_units", out var mixedUnits))
                {
                    string ReadUnits(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var root = document.RootElement;
                    var preferred = new Dictionary<int, string>();
                    foreach (var pair in root.GetProperty("preferred").EnumerateArray()) preferred[pair[0].GetInt32()] = ReadUnits(pair[1]);
                    var decoder = new FixedLengthMixedInputDecoder(code => code.Contains('x') || code.Contains('X') ? Array.Empty<string>() : new[] { code }, text => "[" + text + "]");
                    var result = decoder.Decode(new MixedInputDecodeRequest { RawCode = ReadUnits(mixedUnits), MaxCodeLength = root.GetProperty("maximum").GetInt32(), LexiconVersion = 1, PreferredCandidateTextByStart = preferred });
                    Console.WriteLine(JsonSerializer.Serialize(new { raw = Units(result.RawCode), prefix = Units(result.ResolvedPrefixText), active = Units(result.ActiveCode), surface = Units(result.SurfaceText),
                        segments = result.Segments.Select(segment => new[] { Units(segment.Code), Units(segment.CandidateText) }) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("mixed_decode", out var mixedRaw))
                {
                    var root = document.RootElement;
                    var preferred = new Dictionary<int, string>();
                    foreach (var pair in root.GetProperty("preferred").EnumerateArray()) preferred[pair[0].GetInt32()] = pair[1].GetString();
                    var decoder = new FixedLengthMixedInputDecoder(code => code.Contains('x') || code.Contains('X') ? Array.Empty<string>() : new[] { code.ToUpperInvariant() }, text => "[" + text + "]");
                    var result = decoder.Decode(new MixedInputDecodeRequest { RawCode = mixedRaw.GetString(), MaxCodeLength = root.GetProperty("maximum").GetInt32(), LexiconVersion = 1, PreferredCandidateTextByStart = preferred });
                    Console.WriteLine(JsonSerializer.Serialize(new { raw = result.RawCode, prefix = result.ResolvedPrefixText, active = result.ActiveCode, surface = result.SurfaceText,
                        segments = result.Segments.Select(segment => new[] { segment.Code, segment.CandidateText }) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("shortcut_parse", out var shortcutText))
                {
                    string shortcutValue = new string(shortcutText.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    bool valid = TigerClaw.Shared.ShortcutGesture.TryParse(shortcutValue, out var parsed);
                    Console.WriteLine(JsonSerializer.Serialize(valid ? parsed.ToConfigString() : null));
                    continue;
                }
                if (document.RootElement.TryGetProperty("shortcut_create", out var shortcut))
                {
                    int key = shortcut[0].GetInt32(), flags = shortcut[1].GetInt32();
                    bool valid = TigerClaw.Shared.ShortcutGesture.TryCreate(key, (flags & 1) != 0,
                        (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, out var gesture);
                    var matches = new List<bool>();
                    var conflicts = new List<int>();
                    if (valid)
                    {
                        for (int f = 0; f < 16; ++f)
                            matches.Add(gesture.Matches(key, (f & 4) != 0, (f & 1) != 0, (f & 2) != 0, (f & 8) != 0));
                        for (int f = 0; f < 4; ++f)
                            conflicts.Add((int)TigerClaw.Shared.ShortcutBindingRules.GetReservedConflict(gesture, (f & 1) != 0, (f & 2) != 0));
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { valid, matches, conflicts }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("currency_command", out var currencyCode))
                {
                    string result = (string)typeof(InputMethodEngine).GetMethod("ConvertToChineseCurrency", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { currencyCode.GetString() });
                    Console.WriteLine(JsonSerializer.Serialize(result));
                    continue;
                }
                if (document.RootElement.TryGetProperty("timer_command", out var timerCode))
                {
                    string code = timerCode.GetString();
                    var regex = (System.Text.RegularExpressions.Regex)typeof(InputMethodEngine).GetField("TimerRegex", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    int? delay = null;
                    // Actual .NET parsing/arithmetic; copied scheduling expression
                    // avoids creating a Timer or showing a popup in this probe.
                    if (regex.IsMatch(code))
                    {
                        string number = code.Substring(2).Replace(",", "").Replace(" ", "");
                        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) && !(minutes <= 0))
                        {
                            double due = minutes * 60d * 1000d;
                            delay = double.IsNaN(due) || double.IsInfinity(due) || due > int.MaxValue ? int.MaxValue : (int)due;
                        }
                    }
                    Console.WriteLine(JsonSerializer.Serialize(delay));
                    continue;
                }
                if (document.RootElement.TryGetProperty("upper_numeric", out var numericCode))
                {
                    string code = new string(numericCode.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
                    bool numeric = (bool)typeof(InputMethodEngine).GetMethod("IsTimerOrCnum", statics).Invoke(null, new object[] { code });
                    var timer = (System.Text.RegularExpressions.Regex)typeof(InputMethodEngine).GetField("TimerRegex", statics).GetValue(null);
                    var currency = (System.Text.RegularExpressions.Regex)typeof(InputMethodEngine).GetField("CnumRegex", statics).GetValue(null);
                    Console.WriteLine(JsonSerializer.Serialize(new { numeric, command = timer.IsMatch(code) ? 1 : currency.IsMatch(code) ? 2 : 0 }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("ordinary_trace", out _))
                {
                    Console.WriteLine(JsonSerializer.Serialize(NativeOrdinaryReference(document.RootElement)));
                    continue;
                }
                if (document.RootElement.TryGetProperty("selection_format", out var selectionFormat))
                {
                    var bindings = new Dictionary<int, List<int>>();
                    foreach (var item in selectionFormat.EnumerateArray())
                        bindings[item.GetProperty("number").GetInt32()] = item.GetProperty("keys").EnumerateArray().Select(key => key.GetInt32()).ToList();
                    const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
                    string Format(string method) => (string)typeof(InputMethodEngine).GetMethod(method, statics).Invoke(null, new object[] { bindings });
                    Console.WriteLine(JsonSerializer.Serialize(new { text = Units(Format("BuildSelectionKeyBindingsText")), file = Units(Format("BuildSelectionKeyFileText")) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("selection_lines", out var selectionLines))
                {
                    var rawLines = selectionLines.EnumerateArray().Select(raw => new string(raw.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray())).ToArray();
                    const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
                    var defaults = typeof(InputMethodEngine).GetMethod("BuildDefaultSelectionKeyBindings", statics).Invoke(null, null);
                    object[] arguments = { rawLines, defaults, null, null };
                    bool success = (bool)typeof(InputMethodEngine).GetMethod("TryParseCustomSelectionKeyConfigLines", statics).Invoke(null, arguments);
                    var bindings = (Dictionary<int, List<int>>)arguments[2];
                    Console.WriteLine(JsonSerializer.Serialize(new { success,
                        bindings = bindings.OrderBy(pair => pair.Key).Select(pair => new { number = pair.Key, keys = pair.Value }), error = Units((string)arguments[3]) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("punct_vk", out var punctVk))
                {
                    const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
                    const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
                    var runtime = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in new[] { ("KeyCnUseEnPunc", "english"), ("KeySlashDunhao", "slash") })
                        config[(string)typeof(CoreRuntimeState).GetField(item.Item1, statics).GetRawConstantValue()] = document.RootElement.GetProperty(item.Item2).GetBoolean() ? "true" : "false";
                    typeof(CoreRuntimeState).GetField("_lock", instance).SetValue(runtime, new object());
                    typeof(CoreRuntimeState).GetField("_config", instance).SetValue(runtime, config);
                    var engine = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputMethodEngine));
                    typeof(InputMethodEngine).GetField("_state", instance).SetValue(engine, runtime);
                    var armedField = typeof(InputMethodEngine).GetField("_dotAfterDigitArmed", instance);
                    armedField.SetValue(engine, document.RootElement.GetProperty("armed").GetBoolean());
                    string symbol = null;
                    if (document.RootElement.GetProperty("shift").GetBoolean())
                    {
                        var cn = (Dictionary<int, string>)typeof(InputMethodEngine).GetField("ShiftCnSymbols", statics).GetValue(null);
                        var en = (Dictionary<int, string>)typeof(InputMethodEngine).GetField("ShiftEnSymbols", statics).GetValue(null);
                        if (cn.TryGetValue(punctVk.GetInt32(), out symbol) && document.RootElement.GetProperty("english").GetBoolean() && en.TryGetValue(punctVk.GetInt32(), out var englishSymbol)) symbol = englishSymbol;
                    }
                    else
                    {
                        object[] arguments = { punctVk.GetInt32(), null };
                        typeof(InputMethodEngine).GetMethod("TryResolveCnSymbolOutput", instance).Invoke(engine, arguments);
                        symbol = (string)arguments[1];
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { text = symbol == null ? null : Units(symbol), armed = (bool)armedField.GetValue(engine) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("post_trace", out var postTrace))
                {
                    const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
                    var runtime = (CoreRuntimeState)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    typeof(CoreRuntimeState).GetField("_lock", fields).SetValue(runtime, new object());
                    typeof(CoreRuntimeState).GetField("_config", fields).SetValue(runtime, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    var engine = (InputMethodEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputMethodEngine));
                    void Set(string name, object value) => typeof(InputMethodEngine).GetField(name, fields).SetValue(engine, value);
                    Set("_lock", new object()); Set("_state", runtime); Set("_sendHistory", new Stack<string>());
                    Set("_repeatBuffer", "重复上屏"); Set("_leftSingleQuote", true); Set("_leftDoubleQuote", true);
                    Set("_random", new Random(1));
                    var quoteMethod = typeof(InputMethodEngine).GetMethod("EmitSmartQuote", fields);
                    var output = new List<object>();
                    foreach (var step in postTrace.EnumerateArray())
                    {
                        string text = new string(step.GetProperty("text").EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                        if (text == "{隐藏候选}") throw new ArgumentException("Persistence action excluded from offline probe");
                        int quote = step.GetProperty("quote").GetInt32();
                        if (quote != 0) text = (string)quoteMethod.Invoke(engine, new object[] { quote == 2 });
                        var result = step.GetProperty("handled").GetBoolean() ? KeyEngineResult.CreateHandled(true, text, "ab", true) : KeyEngineResult.Pass(true, text, "ab", true);
                        int flags = step.GetProperty("flags").GetInt32();
                        engine.PostProcessKey(step.GetProperty("vk").GetInt32(), step.GetProperty("down").GetBoolean() ? "down" : "up", result,
                            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);
                        output.Add(new { text = Units(result.TextToOutput ?? ""), buffer = Units(result.InputBuffer ?? ""), composing = result.IsComposing,
                            add = result.OpenAddCiWindow, history = Units(engine.GetLastCi(20)), count = engine.GetSendHistoryCount(),
                            repeat = Units((string)typeof(InputMethodEngine).GetField("_repeatBuffer", fields).GetValue(engine)),
                            @decimal = (bool)typeof(InputMethodEngine).GetField("_dotAfterDigitArmed", fields).GetValue(engine) });
                    }
                    Console.WriteLine(JsonSerializer.Serialize(output));
                    continue;
                }
                if (document.RootElement.TryGetProperty("guess_vk", out var guessVk))
                {
                    object[] arguments = { guessVk.GetInt32(), document.RootElement.GetProperty("shift").GetBoolean(), document.RootElement.GetProperty("caps").GetBoolean(), null };
                    bool found = (bool)typeof(InputMethodEngine).GetMethod("TryGuessPassThroughText", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments);
                    Console.WriteLine(JsonSerializer.Serialize(found ? Units((string)arguments[3]) : null));
                    continue;
                }
                if (document.RootElement.TryGetProperty("output_digit", out var digitText))
                {
                    string text = new string(digitText.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    int modifiers = document.RootElement.GetProperty("modifiers").GetInt32();
                    bool ending = (bool)typeof(InputMethodEngine).GetMethod("IsTextEndingWithDigit", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { text });
                    bool passDigit = (bool)typeof(InputMethodEngine).GetMethod("IsPassThroughDigitKey", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                        new object[] { document.RootElement.GetProperty("vk").GetInt32(), (modifiers & 1) != 0, (modifiers & 2) != 0, (modifiers & 4) != 0, (modifiers & 8) != 0 });
                    Console.WriteLine(JsonSerializer.Serialize(new[] { ending, passDigit }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("weekday", out var weekday))
                {
                    var culture = CultureInfo.GetCultureInfo(document.RootElement.GetProperty("locale").GetString());
                    Console.WriteLine(JsonSerializer.Serialize(Units(culture.DateTimeFormat.GetDayName((DayOfWeek)weekday.GetInt32()))));
                    continue;
                }
                if (document.RootElement.TryGetProperty("page_trace", out var pageTrace))
                {
                    const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
                    var runtime = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    typeof(CoreRuntimeState).GetField("_lock", fields).SetValue(runtime, new object());
                    typeof(CoreRuntimeState).GetField("_config", fields).SetValue(runtime, config);
                    string sizeKey = (string)typeof(CoreRuntimeState).GetField("KeyPageSize", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
                    string pageKeysKey = (string)typeof(CoreRuntimeState).GetField("KeyPageKeys", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
                    var engine = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InputMethodEngine));
                    typeof(InputMethodEngine).GetField("_state", fields).SetValue(engine, runtime);
                    var reset = typeof(InputMethodEngine).GetMethod("ResetCandidatePageTracker", fields);
                    var get = typeof(InputMethodEngine).GetMethod("GetCandidatePage", fields);
                    var move = typeof(InputMethodEngine).GetMethod("MoveCandidatePage", fields);
                    var indexField = typeof(InputMethodEngine).GetField("_candidatePageIndex", fields);
                    var modeType = typeof(InputMethodEngine).GetNestedType("CompositionState", BindingFlags.NonPublic);
                    reset.Invoke(engine, null);
                    var indices = new List<int>();
                    foreach (var step in pageTrace.EnumerateArray())
                    {
                        config[sizeKey] = step.GetProperty("size").GetInt32().ToString(CultureInfo.InvariantCulture);
                        string code = new string(step.GetProperty("code").EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                        object mode = Enum.ToObject(modeType, step.GetProperty("mode").GetInt32());
                        var candidates = Enumerable.Repeat("entry", step.GetProperty("total").GetInt32()).ToArray();
                        string op = step.GetProperty("op").GetString();
                        if (op == "reset") reset.Invoke(engine, null);
                        else if (op == "get") get.Invoke(engine, new object[] { code, mode, candidates, 0 });
                        else if (op == "move") move.Invoke(engine, new object[] { code, mode, candidates, step.GetProperty("delta").GetInt32() });
                        else if (op == "key")
                        {
                            config[pageKeysKey] = step.GetProperty("keys").GetString();
                            object[] keyArguments = { step.GetProperty("vk").GetInt32(), step.GetProperty("shift").GetBoolean() };
                            bool next = (bool)typeof(InputMethodEngine).GetMethod("IsNextPageKey", fields).Invoke(engine, keyArguments);
                            bool previous = (bool)typeof(InputMethodEngine).GetMethod("IsPrevPageKey", fields).Invoke(engine, keyArguments);
                            if (next || previous) move.Invoke(engine, new object[] { code, mode, candidates, next ? 1 : -1 });
                        }
                        else throw new ArgumentException("Unknown page operation");
                        indices.Add((int)indexField.GetValue(engine));
                    }
                    Console.WriteLine(JsonSerializer.Serialize(indices));
                    continue;
                }
                if (document.RootElement.TryGetProperty("config_serialize", out var serializeArgs))
                {
                    string ReadUnits(JsonElement raw) => new string(raw.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in serializeArgs.EnumerateArray()) config[ReadUnits(pair.GetProperty("key"))] = ReadUnits(pair.GetProperty("value"));
                    var configOutputLines = (List<string>)typeof(CoreRuntimeState).GetMethod("BuildConfigLines", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { config });
                    Console.WriteLine(JsonSerializer.Serialize(new UTF8Encoding(false).GetBytes(string.Join("\r\n", configOutputLines) + "\r\n").Select(b => (int)b)));
                    continue;
                }
                if (document.RootElement.TryGetProperty("recent_schemas", out var recentArgs))
                {
                    string ReadUnits(JsonElement raw) => new string(raw.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var runtime = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    var recent = new List<string>();
                    var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["最近码表对"] = ReadUnits(recentArgs.GetProperty("persisted")) };
                    typeof(CoreRuntimeState).GetField("_recentSchemas", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(runtime, recent);
                    typeof(CoreRuntimeState).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(runtime, config);
                    typeof(CoreRuntimeState).GetMethod("SeedRecentSchemasFromConfigNoLock", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(runtime, null);
                    foreach (var item in recentArgs.GetProperty("record").EnumerateArray())
                        typeof(CoreRuntimeState).GetMethod("RecordRecentSchemaNoLock", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(runtime, new object[] { ReadUnits(item) });
                    var schemas = recentArgs.GetProperty("schemas").EnumerateArray().Select(ReadUnits).ToArray();
                    string current = ReadUnits(recentArgs.GetProperty("current")).Trim();
                    string target = "";
                    if (schemas.Length >= 2)
                    {
                        target = recent.FirstOrDefault(s => !string.Equals(s, current, StringComparison.OrdinalIgnoreCase) && schemas.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)));
                        if (string.IsNullOrEmpty(target))
                        {
                            int index = Array.FindIndex(schemas, x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                            target = schemas[index < 0 ? 0 : (index + 1) % schemas.Length];
                        }
                        target = schemas.FirstOrDefault(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
                        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase)) target = "";
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { history = recent.Select(Units), serialized = Units(string.Join("|", recent)), target = Units(target ?? "") }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("runtime_paths", out var pathArgs))
                {
                    // Read-only counterpart of ResolveCodeRoot/ResolveCurrentMbDir:
                    // use .NET path/enum/ordinal APIs, omit live config writeback.
                    string configured = Bind("NormalizePathSetting")(pathArgs.GetProperty("root").GetString());
                    string root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(pathArgs.GetProperty("base").GetString(), configured));
                    string current = pathArgs.GetProperty("current").GetString();
                    string[] directories = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
                    string selected = directories.FirstOrDefault(d => !string.IsNullOrEmpty(current) && string.Equals(Path.GetFileName(d), current, StringComparison.OrdinalIgnoreCase));
                    bool fallback = false;
                    if (selected == null && directories.Length != 0)
                    {
                        selected = Path.Combine(root, directories.Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First());
                        fallback = true;
                    }
                    var names = directories.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(Units);
                    Console.WriteLine(JsonSerializer.Serialize(new { root = Units(root), directory = Units(selected ?? ""), name = Units(selected == null ? "" : Path.GetFileName(selected)), fallback, schemas = names }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("config_lines", out var configLines) || document.RootElement.TryGetProperty("config_file", out _))
                {
                    var defaults = (KeyValuePair<string, string>[])typeof(CoreRuntimeState).GetField("DefaultConfigPairs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    var merged = defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var findSep = typeof(CoreRuntimeState).GetMethod("FindSep", BindingFlags.NonPublic | BindingFlags.Static).CreateDelegate<Func<string, int>>();
                    var pathNormalize = Bind("NormalizePathSetting");
                    var parseBool = typeof(CoreRuntimeState).GetMethod("ParseBool", BindingFlags.NonPublic | BindingFlags.Static).CreateDelegate<Func<string, bool, bool>>();
                    // Mirrors the ReloadConfig merge loop; never invokes its
                    // config-write and autorun-registration side effects.
                    IEnumerable<string> sourceLines;
                    if (document.RootElement.TryGetProperty("config_file", out var configFile))
                    {
                        var encoding = (Encoding)typeof(CoreRuntimeState).GetMethod("DetectTextEncoding", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { configFile.GetString() });
                        sourceLines = File.ReadAllLines(configFile.GetString(), encoding);
                    }
                    else sourceLines = configLines.EnumerateArray().Select(raw => new string(raw.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray()));
                    foreach (string text in sourceLines)
                    {
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        string configLine = text.TrimStart().TrimEnd('\r', '\n');
                        if (configLine.Length == 0 || configLine[0] == '#') continue;
                        int separator = findSep(configLine);
                        if (separator <= 0) continue;
                        string key = configLine.Substring(0, separator).Trim();
                        if (!merged.ContainsKey(key)) continue;
                        merged[key] = configLine.Substring(separator+1).Trim();
                    }
                    merged["码表存储位置"] = pathNormalize(merged["码表存储位置"]);
                    Console.WriteLine(JsonSerializer.Serialize(merged.Select(pair => new
                    { key = Units(pair.Key), value = Units(pair.Value), bool_false = parseBool(pair.Value, false), bool_true = parseBool(pair.Value, true) })));
                    continue;
                }
                if (document.RootElement.TryGetProperty("pinyin_base", out var pinyinBase))
                {
                    var runtime = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    typeof(CoreRuntimeState).GetField("_exeDir", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(runtime, pinyinBase.GetString());
                    var map = (Dictionary<string, List<string>>)typeof(CoreRuntimeState).GetMethod("LoadPinyinLexicon", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(runtime, null);
                    var table = CompactLexicon.Build(map);
                    using var stream = new MemoryStream(); table.WriteTo(stream);
                    Console.WriteLine(JsonSerializer.Serialize(new { image = stream.ToArray().Select(b => (int)b) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("schema", out var schema))
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(document.RootElement.TryGetProperty("locale", out var cultureName) ? cultureName.GetString() : "zh-CN");
                    // BuildLexiconSnapshot is documented pure disk work: no constructor
                    // side effects, live fields, model service or production IPC.
                    var runtime = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoreRuntimeState));
                    var snapshot = typeof(CoreRuntimeState).GetMethod("BuildLexiconSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(runtime, new object[] { schema.GetString() });
                    var table = (CompactLexicon)snapshot.GetType().GetField("Lexicon").GetValue(snapshot);
                    var lookup = (Dictionary<string, string>)snapshot.GetType().GetField("ConstructCodeMap").GetValue(snapshot);
                    object Field(string field) => snapshot.GetType().GetField(field).GetValue(snapshot);
                    object Map(string field) => ((Dictionary<string, string>)Field(field)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new { text = Units(pair.Key), value = Units(pair.Value) }).ToArray();
                    int[][] Set(string field) => ((HashSet<string>)Field(field)).OrderBy(code => code, StringComparer.Ordinal).Select(Units).ToArray();
                    using var stream = new MemoryStream(); table.WriteTo(stream);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        image = stream.ToArray().Select(b => (int)b),
                        lookup = lookup.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { text = Units(pair.Key), code = Units(pair.Value) }),
                        comments = Map("CommentMap"), splits = Map("SplitMap"), full_codes = Map("FullCodeMap"),
                        unique = Set("Unique"), nonterminal = Set("NonTerminal"), auto_short = Set("AutoShortSymbol"),
                        flags = new[] { (bool)Field("ShortSymbolSemicolon"), (bool)Field("ShortSymbolSlash"), (bool)Field("ShortSymbolLBracket"), (bool)Field("ShortSymbolZ") }
                    }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("graphemes", out var graphemes))
                {
                    var text = new string(graphemes.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    Console.WriteLine(JsonSerializer.Serialize(StringInfo.ParseCombiningCharacters(text)));
                    continue;
                }
                if (document.RootElement.TryGetProperty("word", out var word))
                {
                    string Read(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var row in document.RootElement.GetProperty("lookup").EnumerateArray()) lookup[Read(row.GetProperty("text"))] = Read(row.GetProperty("code"));
                    string result = (string)typeof(CoreRuntimeState).GetMethod("ConstructCiFromLookup", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { Read(word), lookup });
                    Console.WriteLine(JsonSerializer.Serialize(Units(result)));
                    continue;
                }
                if (document.RootElement.TryGetProperty("directory", out var directory))
                {
                    CultureInfo.CurrentCulture = document.RootElement.TryGetProperty("locale", out var locale)
                        ? CultureInfo.GetCultureInfo(locale.GetString()) : defaultCulture;
                    Console.WriteLine(JsonSerializer.Serialize(CoreRuntimeState.GetOrderedLexiconFiles(directory.GetString())
                        .Select(path => Units(Path.GetFileName(path)))));
                    continue;
                }
                if (document.RootElement.TryGetProperty("normalize", out var normalizeText))
                {
                    var text = new string(normalizeText.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    Console.WriteLine(JsonSerializer.Serialize(new { normalized = Units(normalize(text)) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("rows", out var rawRows))
                {
                    string Read(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    // Reference orchestration mirrors Reload's codedRows map loop,
                    // using its actual NormalizeCode and .NET collection semantics.
                    var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rawRows.EnumerateArray().OrderByDescending(row => row.GetProperty("freq").GetInt32()))
                    {
                        var code = normalize(Read(row.GetProperty("code")));
                        if (code.Length == 0) continue;
                        if (!map.TryGetValue(code, out var values)) map[code] = values = new List<string>();
                        var text = Read(row.GetProperty("text"));
                        if (!values.Contains(text)) values.Add(text);
                    }
                    Console.WriteLine(JsonSerializer.Serialize(map.Select(entry => new
                    { code = Units(entry.Key), candidates = entry.Value.Select(Units) })));
                    continue;
                }
                if (document.RootElement.TryGetProperty("entries", out var entries))
                {
                    string Read(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var source = entries.EnumerateArray().Select(entry =>
                        new KeyValuePair<string, IReadOnlyList<string>>(Read(entry.GetProperty("code")),
                            entry.GetProperty("candidates").EnumerateArray().Select(Read).ToArray())).ToArray();
                    if (document.RootElement.TryGetProperty("construct_file", out var constructFile))
                    {
                        var map = source.ToDictionary(entry => entry.Key, entry => entry.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                        string path = constructFile.GetString();
                        var lookup = (Dictionary<string, string>)typeof(CoreRuntimeState).GetMethod("BuildConstructCodeMap", BindingFlags.NonPublic | BindingFlags.Static)
                            .Invoke(null, new object[] { path, File.Exists(path), map });
                        var uncoded = document.RootElement.GetProperty("uncoded").EnumerateArray()
                            .Select(row => (Read(row.GetProperty("text")), row.GetProperty("freq").GetInt32())).ToList();
                        var inferred = new List<(string code, string text, int freq)>();
                        typeof(CoreRuntimeState).GetMethod("AppendInferredNoCodeRows", BindingFlags.NonPublic | BindingFlags.Static)
                            .Invoke(null, new object[] { uncoded, lookup, inferred });
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            lookup = lookup.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { text = Units(pair.Key), code = Units(pair.Value) }),
                            inferred = inferred.Select(row => new { code = Units(row.code), text = Units(row.text), row.freq })
                        }));
                        continue;
                    }
                    if (document.RootElement.TryGetProperty("adjustments", out var adjustments))
                    {
                        var map = source.ToDictionary(entry => entry.Key, entry => entry.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                        foreach (var item in adjustments.EnumerateArray())
                        {
                            string adjustmentLine = Read(item).Trim();
                            var prefixes = new[] { "{添加}", "{置顶}", "{删除}", "{前移}" };
                            for (int i = 0; i < prefixes.Length; i++)
                            {
                                if (!adjustmentLine.StartsWith(prefixes[i])) continue;
                                string method = i < 2 ? "ApplyAddLike" : i == 2 ? "ApplyDelete" : "ApplyAdvance";
                                object[] parameters = i < 2 ? new object[] { adjustmentLine.Substring(prefixes[i].Length), map, i == 1 } :
                                    new object[] { adjustmentLine.Substring(prefixes[i].Length), map };
                                typeof(CoreRuntimeState).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, parameters);
                                break;
                            }
                        }
                        source = map.Select(entry => new KeyValuePair<string, IReadOnlyList<string>>(entry.Key, entry.Value)).ToArray();
                    }
                    if (document.RootElement.TryGetProperty("edit_files", out var editFiles))
                    {
                        var map = source.ToDictionary(entry => entry.Key, entry => entry.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                        foreach (var edit in editFiles.EnumerateArray())
                        {
                            bool custom = edit.TryGetProperty("custom", out var customValue) && customValue.GetBoolean();
                            typeof(CoreRuntimeState).GetMethod(custom ? "LoadCustom" : "LoadAdjust", BindingFlags.NonPublic | BindingFlags.Static)
                                .Invoke(null, new object[] { edit.GetProperty("path").GetString(), map });
                        }
                        source = map.Select(entry => new KeyValuePair<string, IReadOnlyList<string>>(entry.Key, entry.Value)).ToArray();
                    }
                    var compact = CompactLexicon.Build(source);
                    using var stream = new MemoryStream();
                    compact.WriteTo(stream);
                    Console.WriteLine(JsonSerializer.Serialize(new { image = stream.ToArray().Select(b => (int)b).ToArray() }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("bytes", out var bytes))
                {
                    byte[] data = bytes.EnumerateArray().Select(c => checked((byte)c.GetInt32())).ToArray();
                    // This selection is DetectTextEncoding's .NET 10 outcome:
                    // both its UTF-8 validation success and Encoding.Default fallback are UTF-8.
                    Encoding encoding = data.Length >= 2 && data[0] == 255 && data[1] == 254 ? Encoding.Unicode :
                        data.Length >= 2 && data[0] == 254 && data[1] == 255 ? Encoding.BigEndianUnicode : Encoding.UTF8;
                    using var stream = new MemoryStream(data);
                    using var reader = new StreamReader(stream, encoding, true);
                    Console.WriteLine(JsonSerializer.Serialize(new { decoded = Units(reader.ReadToEnd()) }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("file", out var file))
                {
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    var coded = new List<(string code, string text, int freq)>();
                    var uncoded = new List<(string text, int freq)>();
                    typeof(CoreRuntimeState).GetMethod("ParseMbFile", BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(string), coded.GetType(), uncoded.GetType() }, null)
                        .Invoke(null, new object[] { file.GetString(), coded, uncoded });
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        coded = coded.Select(row => new { code = Units(row.code), text = Units(row.text), row.freq }),
                        uncoded = uncoded.Select(row => new { text = Units(row.text), row.freq })
                    }));
                    continue;
                }
                if (document.RootElement.TryGetProperty("lines", out var lines))
                {
                    string Read(JsonElement value) => new string(value.EnumerateArray().Select(c => checked((char)c.GetInt32())).ToArray());
                    var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                    if (document.RootElement.TryGetProperty("positive", out var positive)) culture.NumberFormat.PositiveSign = Read(positive);
                    if (document.RootElement.TryGetProperty("negative", out var negative)) culture.NumberFormat.NegativeSign = Read(negative);
                    CultureInfo.CurrentCulture = culture;
                    var coded = new List<(string code, string text, int freq)>();
                    var uncoded = new List<(string text, int freq)>();
                    typeof(CoreRuntimeState).GetMethod("ParseMbLines", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { lines.EnumerateArray().Select(Read).ToArray(),
                            !document.RootElement.GetProperty("yaml").GetBoolean(), coded, uncoded });
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        coded = coded.Select(row => new { code = Units(row.code), text = Units(row.text), row.freq }),
                        uncoded = uncoded.Select(row => new { text = Units(row.text), row.freq })
                    }));
                    continue;
                }
                var value = new string(document.RootElement.GetProperty("text").EnumerateArray()
                    .Select(c => checked((char)c.GetInt32())).ToArray());
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    comment = Units(comment(value)), decoded = Units(decode(value)), token = Units(token(value))
                }));
            }
            return 0;
        }
    }
}
