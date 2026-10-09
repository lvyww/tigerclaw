using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TigerClaw.Core;
using TigerClaw.Dialog;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunAutoSelectMinCodeTests()
        {
            long before = _reviewChecks;
            const string key = "自动选重最低码数";
            var table = new Dictionary<string, List<string>>
            {
                ["aa"] = new() { "甲", "乙", "多字", "\U00020000" },
                ["bbb"] = new() { "甲", "乙", "多字", "\U00020000" },
                ["cccc"] = new() { "甲", "乙", "多字", "\U00020000" },
                ["zz"] = new() { "终" }
            };
            SentenceInputDecoder Make(int minimum) => new(SentenceLexiconIndex.Build(table),
                NeutralSentenceLanguageModel.Instance, beamWidth: 100,
                isolationPenalty: SentenceIsolationPenalty.None, allowDuplicateSingleCharacters: true,
                autoSelectMinCodeLength: minimum);
            foreach (int minimum in new[] { 0, 1, 2, 3, 4, 128 })
            {
                using var decoder = Make(minimum);
                using var full = Make(minimum);
                foreach (string code in new[] { "aa", "bbb", "cccc" })
                {
                    bool allowed = minimum > 0 && code.Length >= minimum;
                    bool Has(string raw, string text) => decoder.DecodeFull(raw).Candidates.Any(c => c.Text == text);
                    Review(Has(code, "乙"), "minimum: whole menu preserves second rank");
                    Review(Has(code, "多字"), "minimum: whole menu preserves words");
                    Review(Has(code + "zz", "甲终"), "minimum: first rank unchanged");
                    Review(Has(code + "zz", "乙终") == allowed, "minimum: local code length");
                    Review(Has("zz" + code + "zz", "终乙终") == allowed, "minimum: middle segment length");
                    Review(Has(code + "zz", "\U00020000终") == allowed, "minimum: Unicode single character");
                    Review(!Has(code + "zz", "多字终"), "minimum: no added implicit word permission");
                    Review(Has(code + "2zz", "乙终"), "minimum: numeric selector bypass");
                    Review(Has(code + ";zz", "乙终"), "minimum: semicolon selector bypass");
                    Review(Has(code + "'zz", "多字终"), "minimum: quote selector bypass");
                    Review(decoder.HasCompleteCandidate(code, "乙"), "minimum: full manual reachability");
                    Review(decoder.HasCompleteCandidate(code, "乙", groupEligibleOnly: true) == allowed,
                        "minimum: whole automatic eligibility");
                    Review(decoder.HasCompleteCandidate(code + "zz", "乙终") == allowed,
                        "minimum: exact reachability uses the same gate");
                    foreach (string raw in new[] { code, code + "z", code + "zz", code + "zzzz",
                        code + "zz", code, code + ";zz" })
                        ReviewSame(decoder.Decode(raw, 20, true), full.DecodeFull(raw, 20, true));
                }
                var selected = decoder.Decode("aa").Candidates.Single(c => c.Text == "乙");
                var locked = new SentenceLockedPrefix("aa", selected.Text, selected.Boundary);
                Review(decoder.Decode("aazz", 20, true, null, locked).Candidates.Any(c => c.Text == "乙终"),
                    "minimum: manually locked prefix remains valid");
            }
            using (var defaults = new SentenceInputDecoder(SentenceLexiconIndex.Build(table),
                NeutralSentenceLanguageModel.Instance, isolationPenalty: SentenceIsolationPenalty.None,
                allowDuplicateSingleCharacters: true))
            {
                Review(!defaults.Decode("aazz").Candidates.Any(c => c.Text == "乙终"), "minimum: decoder default 3");
                Review(defaults.Decode("bbbzz").Candidates.Any(c => c.Text == "乙终"), "minimum: default includes 3");
            }
            string directory = Path.Combine(Path.GetTempPath(), "TigerClaw.AutoSelectMin." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var state = new CoreRuntimeState(directory);
                Review(state.GetSentenceAutoSelectMinCodeLength() == 3, "minimum: state default");
                foreach (var item in new[] { ("0", 0), ("1", 1), ("2", 2), ("3", 3), ("4", 4),
                    ("128", 128), ("129", 128), ("-1", 0), ("", 3), ("invalid", 3), ("1.5", 3) })
                {
                    Review(state.TrySetConfigValue(key, item.Item1, out _, out _), "minimum: setting accepted");
                    Review(state.GetSentenceAutoSelectMinCodeLength() == item.Item2, "minimum: bounds/default");
                    Review(state.GetSentenceAllowDuplicateSingleCharacters() == (item.Item2 > 0), "minimum: derived enabled");
                }
                File.WriteAllText(Path.Combine(directory, "config.txt"), "允许单字重码组句\t否\n");
                Review(state.ReloadConfig(), "minimum: config reload");
                Review(state.GetSentenceAutoSelectMinCodeLength() == 3, "minimum: retired setting ignored");
                Review(!File.ReadAllText(Path.Combine(directory, "config.txt")).Contains("允许单字重码组句"),
                    "minimum: obsolete setting removed from persisted defaults");
                Review(state.TrySetConfigValue(key, "0", out _, out _) && state.ReloadConfig() &&
                    state.GetSentenceAutoSelectMinCodeLength() == 0, "minimum: zero survives reload");
                Review(state.TrySetConfigValue(key, "3", out _, out _), "minimum: restore threshold");
                using var engine = new InputMethodEngine(state, Make(3));
                var eligible = typeof(InputMethodEngine).GetMethod("GroupEligible", BindingFlags.Instance | BindingFlags.NonPublic);
                bool Eligible(int length, bool explicitRank) => (bool)eligible.Invoke(engine,
                    new object[] { "乙", 2, new SentencePathBoundary { TextLength = 1, RawLength = length, CodeLength = length }, explicitRank });
                Review(!Eligible(2, false) && Eligible(3, false), "minimum: empty-code automatic eligibility");
                Review(Eligible(2, true), "minimum: explicit eligibility remains valid");
            }
            finally { ReviewCleanupDirectory(directory); }
            Review(ConfigSettingOrder.GetRank(key) < ConfigSettingOrder.GetRank("高频字仅使用最优码组句"),
                "minimum: settings row in sentence category");
            Console.WriteLine(JsonSerializer.Serialize(new { test = "auto_select_min_code_length",
                status = "passed", checks = _reviewChecks - before, defaults = 3, minimum = 0, maximum = 128 }));
            return 0;
        }
    }
}
