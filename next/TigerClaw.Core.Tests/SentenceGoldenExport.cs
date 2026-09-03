using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private const string SentenceGoldenFormat = "tigerclaw.sentence.golden.v1";
        private const string SentenceGoldenFileName = "sentence_golden.v1.jsonl";

        private static int RunSentenceGoldenExport(string outputPath)
        {
            string path = string.IsNullOrEmpty(outputPath)
                ? GetCommittedSentenceGoldenPath()
                : Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(BuildSentenceGoldenDocument()));
            Console.WriteLine("wrote " + path);
            return 0;
        }

        private static void SentenceGoldenExportIsDeterministic()
        {
            string first = BuildSentenceGoldenDocument();
            string second = BuildSentenceGoldenDocument();
            Equal(first, second, nameof(SentenceGoldenExportIsDeterministic) + ".repeat");

            string committedPath = GetCommittedSentenceGoldenPath();
            True(File.Exists(committedPath), nameof(SentenceGoldenExportIsDeterministic) + ".committed_exists");
            string committed = File.ReadAllText(committedPath, new UTF8Encoding(false)).Replace("\r\n", "\n");
            Equal(first, committed, nameof(SentenceGoldenExportIsDeterministic) + ".matches_committed");
        }

        private static void SentenceGoldenCasesIncrementalMatchesFull()
        {
            foreach (GoldenCase item in CreateGoldenCases())
            {
                if (item.Kind != "decoder")
                {
                    continue;
                }

                SentenceInputDecoder decoder = CreateGoldenDecoder(item);
                string raw = string.Empty;
                foreach (GoldenAction action in item.Actions)
                {
                    raw = ApplyDecoderAction(raw, action);
                    decoder.ResetDecodeCache();
                    AssertSentenceResultsEqual(
                        decoder.Decode(raw, item.CandidateLimit, includeEarlyCommitEvidence: true),
                        decoder.DecodeFull(raw, item.CandidateLimit, includeEarlyCommitEvidence: true),
                        nameof(SentenceGoldenCasesIncrementalMatchesFull) + "." + item.Id + "." + raw);
                }
            }
        }

        private static string BuildSentenceGoldenDocument()
        {
            var lines = new List<string>
            {
                "{" +
                "\"record_type\":\"meta\"," +
                "\"format\":" + JsonString(SentenceGoldenFormat) + "," +
                "\"source\":\"TigerClaw.Core.Tests in-memory fixtures\"" +
                "}"
            };

            foreach (GoldenCase item in CreateGoldenCases())
            {
                lines.Add(FormatGoldenCase(item));
                if (item.Kind == "decoder")
                {
                    lines.AddRange(ExportDecoderSnapshots(item));
                }
                else
                {
                    lines.AddRange(ExportEngineSnapshots(item));
                }
            }

            return string.Join("\n", lines.ToArray()) + "\n";
        }

        private static string GetCommittedSentenceGoldenPath()
        {
            return Path.Combine(FindCoreTestsDirectory(), "golden", SentenceGoldenFileName);
        }

        private static string FindCoreTestsDirectory()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                string nested = Path.Combine(dir, "TigerClaw.Core.Tests");
                if (File.Exists(Path.Combine(nested, "TigerClaw.Core.Tests.csproj")))
                {
                    return nested;
                }

                string fromRoot = Path.Combine(dir, "next", "TigerClaw.Core.Tests");
                if (File.Exists(Path.Combine(fromRoot, "TigerClaw.Core.Tests.csproj")))
                {
                    return fromRoot;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }

            throw new InvalidOperationException("Cannot locate TigerClaw.Core.Tests directory.");
        }

        private static GoldenCase[] CreateGoldenCases()
        {
            var wordLexicon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ot"] = new List<string> { "是" },
                ["ue"] = new List<string> { "的" },
                ["tu"] = new List<string> { "我" },
                ["j"] = new List<string> { "人", "什么", "怎样" },
                ["jq"] = new List<string> { "件" },
                ["fi"] = new List<string> { "一", "一般", "一起" }
            };
            var supplementLexicon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ab"] = new List<string> { "齿" },
                ["cdef"] = new List<string> { "烧" },
                ["abc"] = new List<string> { "茧" },
                ["def"] = new List<string> { "师" }
            };
            var engineLexicon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丙" }
            };

            return new[]
            {
                new GoldenCase
                {
                    Id = "word-and-selection",
                    Kind = "decoder",
                    LanguageModel = "distinct",
                    BeamWidth = 20,
                    EmittedCharacterReward = 2.0,
                    CandidateLimit = 20,
                    Lexicon = wordLexicon,
                    Actions = ParseDecoderActions("o,t,j,2,backspace,;,backspace,',backspace,3")
                },
                new GoldenCase
                {
                    Id = "one-key-and-embedded",
                    Kind = "decoder",
                    LanguageModel = "distinct",
                    BeamWidth = 20,
                    EmittedCharacterReward = 2.0,
                    CandidateLimit = 20,
                    Lexicon = wordLexicon,
                    Actions = ParseDecoderActions("j,backspace,o,t,j,backspace,2")
                },
                new GoldenCase
                {
                    Id = "append-backspace-rebuild",
                    Kind = "decoder",
                    LanguageModel = "distinct",
                    BeamWidth = 20,
                    EmittedCharacterReward = 2.0,
                    CandidateLimit = 20,
                    Lexicon = wordLexicon,
                    Actions = ParseDecoderActions("u,e,o,t,t,u,backspace,backspace,backspace,backspace")
                },
                new GoldenCase
                {
                    Id = "supplement-overlap",
                    Kind = "decoder",
                    LanguageModel = "supplement_preference",
                    BeamWidth = 20,
                    EmittedCharacterReward = 2.0,
                    CandidateLimit = 20,
                    Lexicon = supplementLexicon,
                    Supplements = new[]
                    {
                        SentenceSupplementEntry.Create("茧师", 1000),
                        SentenceSupplementEntry.Create("师", 500)
                    },
                    Actions = ParseDecoderActions("a,b,c,d,e,f,backspace,backspace")
                },
                new GoldenCase
                {
                    Id = "engine-traverse-and-commit",
                    Kind = "engine",
                    LanguageModel = "neutral",
                    BeamWidth = 100,
                    EmittedCharacterReward = 0.0,
                    CandidateLimit = 5,
                    Lexicon = engineLexicon,
                    Actions = new[]
                    {
                        GoldenAction.Named("type", "abcd"),
                        GoldenAction.Named("down", string.Empty),
                        GoldenAction.Named("tab", string.Empty),
                        GoldenAction.Named("shift_tab", string.Empty),
                        GoldenAction.Named("space", string.Empty)
                    }
                },
                new GoldenCase
                {
                    Id = "engine-selection-and-backspace",
                    Kind = "engine",
                    LanguageModel = "neutral",
                    BeamWidth = 100,
                    EmittedCharacterReward = 0.0,
                    CandidateLimit = 5,
                    Lexicon = wordLexicon,
                    Actions = new[]
                    {
                        GoldenAction.Named("type", "fi"),
                        GoldenAction.Named("semicolon", string.Empty),
                        GoldenAction.Named("backspace", string.Empty),
                        GoldenAction.Named("quote", string.Empty),
                        GoldenAction.Named("escape", string.Empty)
                    }
                }
            };
        }

        private static GoldenAction[] ParseDecoderActions(string sequence)
        {
            string[] parts = sequence.Split(',');
            var actions = new GoldenAction[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i];
                if (string.Equals(token, "backspace", StringComparison.Ordinal) ||
                    string.Equals(token, "reset", StringComparison.Ordinal))
                {
                    actions[i] = GoldenAction.Named(token, string.Empty);
                }
                else
                {
                    actions[i] = GoldenAction.Named("append", token);
                }
            }

            return actions;
        }

        private static List<string> ExportDecoderSnapshots(GoldenCase item)
        {
            var lines = new List<string>();
            SentenceInputDecoder decoder = CreateGoldenDecoder(item);
            string raw = string.Empty;
            int step = 0;
            foreach (GoldenAction action in item.Actions)
            {
                step++;
                raw = ApplyDecoderAction(raw, action);
                SentenceDecodeResult incremental = decoder.Decode(
                    raw,
                    item.CandidateLimit,
                    includeEarlyCommitEvidence: true);
                SentenceDecodeResult full = decoder.DecodeFull(
                    raw,
                    item.CandidateLimit,
                    includeEarlyCommitEvidence: true);
                AssertSentenceResultsEqual(
                    incremental,
                    full,
                    "golden." + item.Id + ".step" + step);
                lines.Add(
                    "{" +
                    "\"record_type\":\"snapshot\"," +
                    "\"case_id\":" + JsonString(item.Id) + "," +
                    "\"step\":" + step.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"action\":" + JsonString(action.Name) + "," +
                    "\"key\":" + JsonString(action.Key) + "," +
                    "\"raw\":" + JsonString(raw) + "," +
                    "\"decode\":" + FormatDecodeResult(incremental) + "," +
                    "\"full_matches_incremental\":true" +
                    "}");
            }

            return lines;
        }

        private static List<string> ExportEngineSnapshots(GoldenCase item)
        {
            var lines = new List<string>();
            var state = new CoreRuntimeState();
            if (!state.TrySetConfigValue("自动启用整句模式", "是", out _, out string reason) ||
                !state.TrySetConfigValue("当前码表", "虎整句", out _, out reason))
            {
                throw new InvalidOperationException("golden." + item.Id + ": " + reason);
            }

            var engine = new InputMethodEngine(state, CreateGoldenDecoder(item));
            int step = 0;
            foreach (GoldenAction action in item.Actions)
            {
                step++;
                KeyEngineResult keyResult = ApplyEngineAction(engine, action);
                EngineUiSnapshot snapshot = engine.GetUiSnapshot(item.CandidateLimit);
                lines.Add(
                    "{" +
                    "\"record_type\":\"snapshot\"," +
                    "\"case_id\":" + JsonString(item.Id) + "," +
                    "\"step\":" + step.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"action\":" + JsonString(action.Name) + "," +
                    "\"key\":" + JsonString(action.Key) + "," +
                    "\"raw\":" + JsonString(snapshot.InputCode ?? string.Empty) + "," +
                    "\"selected_index\":" + snapshot.SelectedCandidateIndex.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"commit_text\":" + JsonString(keyResult == null ? string.Empty : (keyResult.TextToOutput ?? string.Empty)) + "," +
                    "\"input_code\":" + JsonString(snapshot.InputCode ?? string.Empty) + "," +
                    "\"active_input_code\":" + JsonString(snapshot.ActiveInputCode ?? string.Empty) + "," +
                    "\"candidates\":" + FormatStringArray(snapshot.Candidates) +
                    "}");
            }

            return lines;
        }

        private static string ApplyDecoderAction(string raw, GoldenAction action)
        {
            if (string.Equals(action.Name, "append", StringComparison.Ordinal))
            {
                return (raw ?? string.Empty) + action.Key;
            }

            if (string.Equals(action.Name, "backspace", StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(raw) ? string.Empty : raw.Substring(0, raw.Length - 1);
            }

            if (string.Equals(action.Name, "reset", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            throw new InvalidOperationException("Unsupported decoder action: " + action.Name);
        }

        private static KeyEngineResult ApplyEngineAction(InputMethodEngine engine, GoldenAction action)
        {
            switch (action.Name)
            {
                case "type":
                    TypeLetters(engine, action.Key);
                    return KeyEngineResult.Pass(true);
                case "down":
                    return Press(engine, 0x28);
                case "up":
                    return Press(engine, 0x26);
                case "tab":
                    return Press(engine, 0x09);
                case "shift_tab":
                    return Press(engine, 0x09, true);
                case "space":
                    return Press(engine, 0x20);
                case "enter":
                    return Press(engine, 0x0D);
                case "backspace":
                    return Press(engine, 0x08);
                case "escape":
                    return Press(engine, 0x1B);
                case "semicolon":
                    return Press(engine, 0xBA);
                case "quote":
                    return Press(engine, 0xDE);
                default:
                    throw new InvalidOperationException("Unsupported engine action: " + action.Name);
            }
        }

        private static SentenceInputDecoder CreateGoldenDecoder(GoldenCase item)
        {
            ISentenceLanguageModel model;
            if (string.Equals(item.LanguageModel, "neutral", StringComparison.Ordinal))
            {
                model = NeutralSentenceLanguageModel.Instance;
            }
            else if (string.Equals(item.LanguageModel, "supplement_preference", StringComparison.Ordinal))
            {
                model = new SupplementPreferenceLanguageModel();
            }
            else
            {
                model = new DistinctSentenceLanguageModel();
            }

            SentenceSupplementMatcher matcher = item.Supplements == null || item.Supplements.Length == 0
                ? SentenceSupplementMatcher.Empty
                : SentenceSupplementMatcher.Build(item.Supplements);
            return new SentenceInputDecoder(
                SentenceLexiconIndex.Build(item.Lexicon),
                model,
                beamWidth: item.BeamWidth,
                emittedCharacterReward: item.EmittedCharacterReward,
                supplementMatcher: matcher);
        }

        private static string FormatGoldenCase(GoldenCase item)
        {
            return "{" +
                "\"record_type\":\"case\"," +
                "\"case_id\":" + JsonString(item.Id) + "," +
                "\"kind\":" + JsonString(item.Kind) + "," +
                "\"language_model\":" + JsonString(item.LanguageModel) + "," +
                "\"beam_width\":" + item.BeamWidth.ToString(CultureInfo.InvariantCulture) + "," +
                "\"emitted_character_reward\":" + FormatScore(item.EmittedCharacterReward) + "," +
                "\"candidate_limit\":" + item.CandidateLimit.ToString(CultureInfo.InvariantCulture) + "," +
                "\"lexicon\":" + FormatLexicon(item.Lexicon) + "," +
                "\"supplements\":" + FormatSupplements(item.Supplements) +
                "}";
        }

        private static string FormatDecodeResult(SentenceDecodeResult result)
        {
            SentenceDecodeResult value = result ?? SentenceDecodeResult.Empty;
            SentenceEarlyCommitEvidence evidence = value.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            return "{" +
                "\"raw\":" + JsonString(value.RawCode ?? string.Empty) + "," +
                "\"expanded_states\":" + value.ExpandedStates.ToString(CultureInfo.InvariantCulture) + "," +
                "\"candidates\":" + FormatCandidates(value.Candidates) + "," +
                "\"early_commit\":{" +
                "\"proposal\":" + JsonString(evidence.Proposal ?? string.Empty) + "," +
                "\"raw_lengths\":" + FormatRawLengths(evidence.RawLengths) + "," +
                "\"confidence_truncated\":" + (evidence.ConfidenceTruncated ? "true" : "false") + "," +
                "\"ignore_neural_constraint\":" + (evidence.IgnoreNeuralConstraint ? "true" : "false") +
                "}" +
                "}";
        }

        private static string FormatCandidates(SentenceCandidate[] candidates)
        {
            SentenceCandidate[] values = candidates ?? Array.Empty<SentenceCandidate>();
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                SentenceCandidate candidate = values[i];
                FlattenBoundary(candidate.Boundary, out int[] textEdges, out int[] rawEdges);
                parts[i] =
                    "{" +
                    "\"text\":" + JsonString(candidate.Text ?? string.Empty) + "," +
                    "\"segmented_code\":" + JsonString(candidate.SegmentedCode ?? string.Empty) + "," +
                    "\"base_score\":" + FormatScore(candidate.BaseScore) + "," +
                    "\"final_score\":" + FormatScore(candidate.FinalScore) + "," +
                    "\"confidence_score\":" + FormatScore(candidate.ConfidenceScore) + "," +
                    "\"supplement_score\":" + FormatScore(candidate.SupplementScore) + "," +
                    "\"max_lexicon_rank\":" + candidate.MaxLexiconRank.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"text_edges\":" + FormatIntArray(textEdges) + "," +
                    "\"raw_edges\":" + FormatIntArray(rawEdges) +
                    "}";
            }

            return "[" + string.Join(",", parts) + "]";
        }

        private static void FlattenBoundary(SentencePathBoundary boundary, out int[] textEdges, out int[] rawEdges)
        {
            var stack = new List<SentencePathBoundary>();
            for (SentencePathBoundary current = boundary; current != null; current = current.Previous)
            {
                stack.Add(current);
            }

            stack.Reverse();
            textEdges = new int[stack.Count];
            rawEdges = new int[stack.Count];
            for (int i = 0; i < stack.Count; i++)
            {
                textEdges[i] = stack[i].TextLength;
                rawEdges[i] = stack[i].RawLength;
            }
        }

        private static string FormatLexicon(Dictionary<string, List<string>> lexicon)
        {
            var keys = new List<string>(lexicon.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new string[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                parts[i] = JsonString(keys[i]) + ":" + FormatStringArray(lexicon[keys[i]]);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static string FormatSupplements(SentenceSupplementEntry[] supplements)
        {
            if (supplements == null || supplements.Length == 0)
            {
                return "[]";
            }

            var parts = new string[supplements.Length];
            for (int i = 0; i < supplements.Length; i++)
            {
                parts[i] =
                    "{\"text\":" + JsonString(supplements[i].Text) +
                    ",\"weight\":" + supplements[i].Weight.ToString(CultureInfo.InvariantCulture) + "}";
            }

            return "[" + string.Join(",", parts) + "]";
        }

        private static string FormatRawLengths(Dictionary<string, int> rawLengths)
        {
            Dictionary<string, int> values = rawLengths ?? new Dictionary<string, int>(StringComparer.Ordinal);
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new string[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                parts[i] = JsonString(keys[i]) + ":" + values[keys[i]].ToString(CultureInfo.InvariantCulture);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static string FormatStringArray(IList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "[]";
            }

            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = JsonString(values[i] ?? string.Empty);
            }

            return "[" + string.Join(",", parts) + "]";
        }

        private static string FormatIntArray(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return "[]";
            }

            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            }

            return "[" + string.Join(",", parts) + "]";
        }

        private static string FormatScore(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static int RunSentenceKnProbe(string modelPath, string outputPath)
        {
            using (SentenceNgramModel model = SentenceNgramModel.Load(modelPath))
            {
                string[] tokens = { "\x02", "的", "是", "我", "那", "a", "\x03" };
                var lines = new List<string>();
                for (int i = 0; i < tokens.Length; i++)
                {
                    for (int j = 0; j < tokens.Length; j++)
                    {
                        for (int k = 0; k < tokens.Length; k++)
                        {
                            lines.Add(FormatKnProbeLine(model, tokens[i], tokens[j], tokens[k], true));
                        }
                    }
                }

                lines.Add(FormatKnProbeLine(model, "\x02", "\x02", "的", false));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllBytes(outputPath, Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray()) + "\n"));
                Console.WriteLine("wrote " + lines.Count + " kn probes to " + outputPath);
            }

            return 0;
        }

        private static string FormatKnProbeLine(
            SentenceNgramModel model,
            string prev2,
            string prev1,
            string target,
            bool includeUnigram)
        {
            double logp = model.LogProbability(prev2, prev1, target, includeUnigram);
            bool observed = model.HasObservedBigram(prev1, target);
            return "{" +
                "\"prev2\":" + ProbeToken(prev2) + "," +
                "\"prev1\":" + ProbeToken(prev1) + "," +
                "\"target\":" + ProbeToken(target) + "," +
                "\"include_unigram\":" + (includeUnigram ? "true" : "false") + "," +
                "\"logp\":" + FormatScore(logp) + "," +
                "\"prob\":" + FormatScore(Math.Exp(logp)) + "," +
                "\"observed_bigram\":" + (observed ? "true" : "false") +
                "}";
        }

        private static string ProbeToken(string value)
        {
            if (value == "\x02")
            {
                return "\"\\u0002\"";
            }

            if (value == "\x03")
            {
                return "\"\\u0003\"";
            }

            return JsonString(value);
        }

        private sealed class GoldenCase
        {
            public string Id;
            public string Kind;
            public string LanguageModel;
            public int BeamWidth;
            public double EmittedCharacterReward;
            public int CandidateLimit;
            public Dictionary<string, List<string>> Lexicon;
            public SentenceSupplementEntry[] Supplements = Array.Empty<SentenceSupplementEntry>();
            public GoldenAction[] Actions;
        }

        private sealed class GoldenAction
        {
            public string Name;
            public string Key;

            public static GoldenAction Named(string name, string key)
            {
                return new GoldenAction
                {
                    Name = name,
                    Key = key ?? string.Empty
                };
            }
        }
    }
}
