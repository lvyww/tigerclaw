using System;
using System.Collections.Generic;
using System.Linq;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void SentenceWholeCodeRewardUsesConfiguredFrequencyScope()
        {
            var source = new Dictionary<string, List<string>>
            {
                ["x"] = new() { "丁" }, ["efgh"] = new() { "丁" }, ["xygh"] = new() { "丁" },
                ["ef"] = new() { "戊" }, ["gh"] = new() { "己" },
                ["ab"] = new() { "甲" }, ["cd"] = new() { "乙" }, ["abcd"] = new() { "词语", "丙" },
                ["z"] = new() { "\U00020000" }, ["zzzz"] = new() { "\U00020000" }
            };
            foreach (var scope in new[]
            {
                (Name: "disabled", Common: new HashSet<string>(), White: new HashSet<string>(), Reward: 5.0),
                (Name: "restricted", Common: new HashSet<string> { "丁", "\U00020000" }, White: new HashSet<string>(), Reward: 0.0),
                (Name: "whitelisted", Common: new HashSet<string> { "丁", "\U00020000" }, White: new HashSet<string> { "丁", "\U00020000" }, Reward: 5.0),
                (Name: "outside", Common: new HashSet<string> { "戊" }, White: new HashSet<string>(), Reward: 5.0)
            })
            {
                var lexicon = SentenceLexiconIndex.Build(source, scope.Common, scope.White);
                using var plain = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                    isolationPenalty: SentenceIsolationPenalty.None, emittedCharacterReward: 2, allowDuplicateSingleCharacters: true);
                using var rewarded = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                    isolationPenalty: SentenceIsolationPenalty.None, emittedCharacterReward: 2,
                    wholeInputSingleCharacterReward: 5, allowDuplicateSingleCharacters: true);
                void CheckReward(string raw, string text, double expected)
                {
                    var a = plain.DecodeFull(raw).Candidates.Single(c => c.Text == text);
                    var b = rewarded.DecodeFull(raw).Candidates.Single(c => c.Text == text);
                    var name = nameof(SentenceWholeCodeRewardUsesConfiguredFrequencyScope) + "." + scope.Name + "." + raw + "." + text;
                    True(Math.Abs(b.FinalScore - a.FinalScore - expected) < 1e-9, name + ".score");
                    True(Math.Abs(b.ConfidenceScore - a.ConfidenceScore) < 1e-9, name + ".mass");
                    True(b.CodeScore == 0, name + ".no_primary_reward");
                }
                CheckReward("x", "丁", 5);
                CheckReward("efgh", "丁", scope.Reward);
                CheckReward("zzzz", "\U00020000", scope.Reward);
                CheckReward("efgh1", "丁", 0);
                CheckReward("abcd", "词语", 0);
                CheckReward("abcd;", "丙", 0);
                CheckReward("efghab", "丁甲", 0);
                True((lexicon.GetCandidates("xygh") == null) == (scope.Name == "restricted"), scope.Name + ".filter_preserved");
                rewarded.Decode("efgh");
                AssertSentenceResultsEqual(rewarded.Decode("efghab"), rewarded.DecodeFull("efghab"), scope.Name + ".append");
                AssertSentenceResultsEqual(rewarded.Decode("efgh"), rewarded.DecodeFull("efgh"), scope.Name + ".backspace");
            }
            Console.WriteLine("Whole-input single-character reward frequency, whitelist, selectors, Unicode, mass and cache checks passed.");
        }
    }
}
