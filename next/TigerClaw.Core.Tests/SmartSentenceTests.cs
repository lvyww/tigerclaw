using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static SentenceInputDecoder SmartDecoder(Dictionary<string, List<string>> entries, int maximum = 4,
            ISentenceLanguageModel model = null) => new SentenceInputDecoder(SentenceLexiconIndex.Build(entries),
                model ?? NeutralSentenceLanguageModel.Instance, smartMaxCodeLength: maximum);

        private static CoreRuntimeState SmartState(bool early = false)
        {
            var state = new CoreRuntimeState();
            state.TrySetConfigValue("当前码表", "虎智能", out _, out _);
            state.TrySetConfigValue("自动启用整句模式", "是", out _, out _);
            state.TrySetConfigValue("整句自动提前上屏", early ? "是" : "否", out _, out _);
            return state;
        }

        private static void TypeSmart(InputMethodEngine engine, string text)
        {
            foreach (char ch in text)
            {
                Press(engine, ch == ' ' ? 0x20 : ch == ';' ? 0xBA : ch == '\'' ? 0xDE : char.ToUpperInvariant(ch));
            }
        }

        private static void SmartSentenceFixedSegmentation()
        {
            True(SmartState().IsSmartSentenceInputActive(), "smart.activation");
            var disabled = SmartState();
            disabled.TrySetConfigValue("自动启用整句模式", "否", out _, out _);
            True(!disabled.IsSentenceInputActive(), "smart.master_off");
            var entries = new Dictionary<string, List<string>> {
                ["a"] = new List<string> { "甲" }, ["b"] = new List<string> { "乙" },
                ["ab"] = new List<string> { "丙", "丁", "戊" }, ["cd"] = new List<string> { "己" },
                ["abcd"] = new List<string> { "庚", "辛" }, ["ef"] = new List<string> { "壬" },
                ["abcdef"] = new List<string> { "不应出现" }
            };
            var decoder = SmartDecoder(entries);
            Equal("庚壬", decoder.Decode("abcdef").Candidates[0].Text, "smart.maximum");
            True(decoder.Decode("abcd").Candidates.All(c => c.Text != "丙己"), "smart.no_implicit_split");
            Equal("甲乙", decoder.Decode("a b").Candidates[0].Text, "smart.single_key_edges");
            Equal("丁己", decoder.Decode("ab2 cd").Candidates[0].Text, "smart.rank_space");
            Equal("辛壬", decoder.Decode("abcd2ef").Candidates[0].Text, "smart.full_rank");
            foreach (string invalid in new[] { "ab23", "ab  cd", "ab 2", "ab9", "abc", "ab;3", "abcdefg" })
            {
                True(decoder.Decode(invalid).Candidates.Length == 0, "smart.invalid:" + invalid);
                True(!decoder.HasValidSmartSegments(invalid), "smart.invalid_key_path:" + invalid);
            }
            True(!decoder.Decode("abcd", includeEarlyCommitEvidence: true).EarlyCommitEvidence.Prefixes.Any(), "smart.full_not_closed");
            True(decoder.Decode("abcd ", includeEarlyCommitEvidence: true).EarlyCommitEvidence.Prefixes.All(p => p.RawLength == 5), "smart.space_boundary");
            foreach (int maximum in new[] { 1, 4, 16 })
            {
                string code = new string('a', maximum * 2 + 1);
                True(SmartSentenceSegmentation.TryParse(code, maximum, out var segments), "smart.parse");
                True(segments.Count == 3 && segments[0].CodeEnd == maximum && !segments[2].Closed, "smart.unique_max:" + maximum);
            }
            var ranked = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "丁", "甲乙" },
                ["cd"] = new List<string> { "丁", "丙" }
            };
            var scored = SmartDecoder(ranked, model: new PrefersAfternoonOverCaveSentenceLanguageModel());
            Equal("丁", scored.Decode("ab").Candidates[0].Text, "smart.short_nonfirst_word_excluded");
            Equal("丁丙", scored.Decode("ab cd").Candidates[0].Text, "smart.short_word_segment_excluded");
            Equal("甲乙丙", scored.Decode("ab2 cd").Candidates[0].Text, "smart.short_word_explicit");
            Equal("甲乙丙", scored.Decode("ab;cd").Candidates[0].Text, "smart.short_word_semicolon");
            Equal("丁丙", scored.Decode("ab cd", includeEarlyCommitEvidence: true).Candidates[0].Text,
                "smart.short_word_evidence_filtered");
            var fullLength = SmartDecoder(ranked, maximum: 2, model: new PrefersAfternoonOverCaveSentenceLanguageModel());
            Equal("甲乙", fullLength.Decode("ab").Candidates[0].Text, "smart.full_nonfirst_word");
            Equal("甲乙丙", fullLength.Decode("abcd").Candidates[0].Text, "smart.full_word_overflow");
            var firstWord = SmartDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲乙", "丙", "戊己" }
            });
            True(firstWord.Decode("ab ").Candidates.Any(c => c.Text == "甲乙"), "smart.short_first_word");
            True(firstWord.Decode("ab ").Candidates.Any(c => c.Text == "丙"), "smart.short_nonfirst_character");
            True(firstWord.Decode("ab ").Candidates.All(c => c.Text != "戊己"), "smart.closed_short_word_excluded");
            Equal("戊己", firstWord.Decode("ab'").Candidates[0].Text, "smart.short_word_quote");
            True(firstWord.HasValidSmartSegments("ab3"), "smart.short_explicit_key_path");
            Equal("丁丙", scored.Decode("ab1 cd").Candidates[0].Text, "smart.explicit_overrides_model");
            ranked["z"] = Enumerable.Range(1, 10).Select(n => ((char)('甲' + n)).ToString()).ToList();
            Equal(ranked["z"][9], SmartDecoder(ranked).Decode("z0").Candidates[0].Text, "smart.zero_tenth");
            True(!SmartSentenceSegmentation.TryParse("ab;", 4, out _, 0), "smart.disabled_rank_parse");

            var lexicon = SentenceLexiconIndex.Build(entries);
            var rewardOff = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                emittedCharacterReward: 2, smartMaxCodeLength: 4);
            var rewardOn = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5, smartMaxCodeLength: 4);
            var plain = rewardOff.Decode("ab").Candidates[0];
            var rewarded = rewardOn.Decode("ab").Candidates[0];
            True(Math.Abs(rewarded.FinalScore - plain.FinalScore - 5) < 1e-9, "smart.single_reward");
            True(Math.Abs(rewarded.ConfidenceScore - plain.ConfidenceScore) < 1e-9, "smart.reward_not_mass");

            string longRaw = string.Join(" ", Enumerable.Repeat("ab", 40));
            var bounded = new SentenceInputDecoder(lexicon, NeutralSentenceLanguageModel.Instance,
                beamWidth: 48, smartMaxCodeLength: 4);
            var longResult = bounded.Decode(longRaw, includeEarlyCommitEvidence: true);
            True(longResult.Candidates.Length > 0 && longResult.ExpandedStates <= 40 * 48 * 3, "smart.bounded_long_search");
            True(bounded.Decode(longRaw, requiredTextPrefix: "戊戊").Candidates.All(c => c.Text.StartsWith("戊戊")),
                "smart.prefix_before_pruning");
        }

        private static void SmartSentenceClosedSegmentDisplay()
        {
            var entries = new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "丁", "甲", "甲乙" },
                ["cd"] = new List<string> { "丙" },
                ["abcd"] = new List<string> { "庚" }
            };
            using var engine = new InputMethodEngine(SmartState(),
                SmartDecoder(entries, model: new PrefersAfternoonOverCaveSentenceLanguageModel()));
            void Display(string expected, string label)
            {
                Equal(expected, engine.GetUiSnapshot(5).ActiveInputCode, "smart.display.ui." + label);
                engine.GetCompositionDisplayParts(out string prefix, out string code);
                Equal(expected, prefix + code, "smart.display.composition." + label);
            }
            TypeSmart(engine, "ab");
            Display("ab", "open");
            TypeSmart(engine, " ");
            Display("甲 ", "model_not_lexicon_first");
            TypeSmart(engine, "c");
            Display("甲 c", "incomplete_tail");
            Equal("甲 *", engine.GetSmartSentenceFormattedDisplay(code => new string('*', code.Length)),
                "smart.display.mask_only_raw");
            TypeSmart(engine, "d ");
            Display("甲 丙 ", "closed_words");
            Equal("ab cd ", engine.GetDifferentialSnapshot(5).RawInput, "smart.display.raw_unchanged");
            Press(engine, 0x08);
            Display("甲 cd", "backspace_separator");
            Press(engine, 0x08);
            Display("甲 c", "backspace_code");
            Press(engine, 0x08);
            Press(engine, 0x08);
            Display("ab", "reopen_segment");
            TypeSmart(engine, "cd");
            Display("abcd", "full_still_open");
            TypeSmart(engine, "c");
            Display("庚 c", "overflow_boundary");
            Press(engine, 0x1B);
            TypeSmart(engine, "ab1");
            Display("丁", "explicit_rank");
            Equal("ab1", Press(engine, 0x0D).TextToOutput, "smart.display.literal_exit");
            TypeSmart(engine, "ab ");
            Press(engine, 0x08);
            Press(engine, 0x08);
            TypeSmart(engine, "c ");
            Display("ac ", "edited_prefix_no_stale_text");
            Press(engine, 0x1B);
            TypeSmart(engine, "ab ");
            var snapshot = engine.GetDifferentialSnapshot(5);
            True(engine.ApplySentenceNeuralScores(snapshot.SentenceGeneration, snapshot.RawInput,
                new[] { -10000.0, 10000.0 }), "smart.display.neural_update");
            Display("丁 ", "reranked_best");
        }

        private static void SmartSentenceKeysAndExits()
        {
            var entries = new Dictionary<string, List<string>> {
                ["ab"] = new List<string> { "甲", "乙", "丙" }, ["cd"] = new List<string> { "丁" }
            };
            using (var engine = new InputMethodEngine(SmartState(), SmartDecoder(entries)))
            {
                TypeSmart(engine, "ab ");
                Equal("ab ", engine.GetDifferentialSnapshot(5).RawInput, "smart.space_retained");
                TypeSmart(engine, "cd");
                Equal(null, Press(engine, 0x20).TextToOutput, "smart.first_space_no_commit");
                Equal("甲丁", Press(engine, 0x20).TextToOutput, "smart.double_space");
                TypeSmart(engine, "ab2");
                Press(engine, 0x08);
                True(engine.GetUiSnapshot(5).Candidates.Length == 3, "smart.backspace_selector");
                TypeSmart(engine, "3 ");
                Press(engine, 0x08);
                Equal("ab3", engine.GetDifferentialSnapshot(5).RawInput, "smart.backspace_space");
                Equal("丙，", Press(engine, 0xBC).TextToOutput, "smart.punctuation");
                TypeSmart(engine, "ab23");
                Equal(null, Press(engine, 0xBC).TextToOutput, "smart.invalid_punctuation");
                TypeSmart(engine, "  ");
                Equal("ab23 ", engine.GetDifferentialSnapshot(5).RawInput, "smart.invalid_double_space");
                Equal("ab23 ", Press(engine, 0x0D).TextToOutput, "smart.raw_exit");
                TypeSmart(engine, "ab;cd");
                Equal("乙丁，", Press(engine, 0xBC).TextToOutput, "smart.semicolon");
                TypeSmart(engine, "ab'cd");
                Equal("丙丁，", Press(engine, 0xBC).TextToOutput, "smart.apostrophe");
                TypeSmart(engine, "ab");
                Press(engine, 0x62);
                Equal("乙，", Press(engine, 0xBC).TextToOutput, "smart.numpad");
                TypeSmart(engine, "ab");
                Press(engine, 0x28);
                Press(engine, 0x20);
                Equal("乙", Press(engine, 0x20).TextToOutput, "smart.double_space_manual_selection");
            }
            var state = SmartState();
            state.TrySetConfigValue("分号次选", "否", out _, out _);
            using (var engine = new InputMethodEngine(state, SmartDecoder(entries)))
            {
                TypeSmart(engine, "ab");
                Equal("甲；", Press(engine, 0xBA).TextToOutput, "smart.disabled_selector");
            }
            var migration = SmartState();
            using (var engine = new InputMethodEngine(migration, SmartDecoder(entries)))
            {
                TypeSmart(engine, "ab2 cd");
                engine.RefreshCompositionAfterSchemaSwitch();
                Equal("ab2 cd", engine.GetDifferentialSnapshot(5).RawInput, "smart.rebuild_preserves_raw");
                migration.TrySetConfigValue("当前码表", "普通字词", out _, out _);
                engine.RefreshCompositionAfterSchemaSwitch();
                Equal("ab2cd", engine.GetDifferentialSnapshot(5).RawInput, "smart.exit_removes_spaces");
                Equal("ab2cd", Press(engine, 0x0D).TextToOutput, "smart.migrated_literal");
            }
        }

        private static void SmartSentenceEarlyCommitConsumesSeparators()
        {
            var entries = new Dictionary<string, List<string>>();
            foreach (char ch in "abcdefghijk") entries[ch.ToString()] = new List<string> { ((char)('甲' + ch - 'a')).ToString() };
            foreach (var scenario in new[] { (Retain: 0, Separator: " "), (Retain: 6, Separator: " "), (Retain: 0, Separator: "1") })
            {
                int retain = scenario.Retain;
                var output = new StringBuilder();
                var state = SmartState(true);
                state.TrySetConfigValue("保留最少编码数量", retain.ToString(), out _, out _);
                using var engine = new InputMethodEngine(state, SmartDecoder(entries));
                foreach (char ch in string.Join(scenario.Separator, "abcdefghijk".Select(ch => ch.ToString())))
                {
                    var result = Press(engine, ch == ' ' ? 0x20 : char.ToUpperInvariant(ch));
                    output.Append(result.TextToOutput);
                    if (!string.IsNullOrEmpty(result.TextToOutput))
                    {
                        var snapshot = engine.GetDifferentialSnapshot(5);
                        string live = snapshot.RawInput.Substring(snapshot.SentenceCommittedRawLength);
                        True(!live.StartsWith(" "), "smart.early_consumes_space");
                        True(!live.StartsWith("1"), "smart.early_consumes_selector");
                        Equal(entries[live[0].ToString()][0], engine.GetUiSnapshot(5).ActiveInputCode.Substring(0, 1),
                            "smart.display.excludes_committed_prefix:" + live);
                        True(SmartSentenceSegmentation.CountLetters(live) >= Math.Max(3, retain), "smart.early_retains_letters");
                    }
                }
                True(output.Length > 0, "smart.early_occurs");
                output.Append(Press(engine, 0x20).TextToOutput);
                output.Append(Press(engine, 0x20).TextToOutput);
                Equal(string.Concat(entries.Values.Select(v => v[0])), output.ToString(), "smart.early_final");
            }
        }
    }
}
