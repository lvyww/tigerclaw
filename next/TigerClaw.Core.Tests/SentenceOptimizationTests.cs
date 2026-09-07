using System;
using System.Collections.Generic;
using System.Linq;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void SentenceEmptyCodeDoesNotTreatPrunedCandidateAsUnique()
        {
            var state = new CoreRuntimeState();
            EnableSentenceEarlyCommit(state);
            var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "丁" }
            }), NeutralSentenceLanguageModel.Instance, beamWidth: 1);
            using (var engine = new InputMethodEngine(state, decoder))
            {
                TypeLetters(engine, "abcd");
                True(engine.GetUiSnapshot(5).Candidates.Length == 1, "empty_unique.pruned_to_one");
                Equal(null, Press(engine, 0x45).TextToOutput, "empty_unique.must_not_commit");
                Equal("abcde", engine.GetDifferentialSnapshot(5).RawInput, "empty_unique.raw_retained");
            }
        }

        private static void SentencePathCheckMatchesFullDecode()
        {
            var decoder = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["a"] = new List<string> { "甲" },
                ["ab"] = new List<string> { "乙", "丙" },
                ["bc"] = new List<string> { "甲乙" },
                ["abc"] = new List<string> { "丁" }
            });
            var inputs = new List<string> { "", "aB", "ab2", "ab;", "ab'", "ab2bc", "abc0", "ab99", "zz" };
            var frontier = new List<string> { "" };
            for (int length = 1; length <= 6; length++)
            {
                frontier = frontier.SelectMany(raw => "abc".Select(letter => raw + letter)).ToList();
                inputs.AddRange(frontier);
            }
            foreach (string raw in inputs)
            {
                SentenceDecodeResult full = decoder.DecodeFull(raw, 1000);
                foreach (string prefix in new[] { "", "甲", "乙", "丙", "不存在" })
                {
                    bool expected = full.Candidates.Any(candidate => candidate.Text.StartsWith(prefix, StringComparison.Ordinal));
                    True(expected == decoder.HasCompleteCandidate(raw, prefix), "path_check.equivalent:" + raw + ":" + prefix);
                    foreach (string excluded in new[] { "甲", "甲乙", "丁" })
                    {
                        bool explicitRank = raw.Any(mark => char.IsDigit(mark) || mark == ';' || mark == '\'');
                        bool alternative = full.Candidates.Any(candidate =>
                            candidate.Text.StartsWith(prefix, StringComparison.Ordinal) &&
                            (explicitRank || candidate.MaxLexiconRank <= 1) && candidate.Text != excluded);
                        True(alternative == decoder.HasCompleteCandidate(raw, prefix, excluded, true),
                            "path_check.alternative:" + raw + ":" + prefix + ":" + excluded);
                    }
                }
            }
            var sameText = CreateSentenceDecoder(new Dictionary<string, List<string>>
            {
                ["ab"] = new List<string> { "甲" },
                ["cd"] = new List<string> { "乙" },
                ["abcd"] = new List<string> { "甲乙" }
            });
            True(!sameText.HasCompleteCandidate("abcd", "", "甲乙", true), "path_check.same_text_different_boundaries");
        }

        private static void SentenceManualSelectionRejectsLateRerank()
        {
            foreach (bool earlyCommit in new[] { false, true })
            foreach (int navigationKey in new[] { 0x28, 0x26, 0x09 })
            {
                var state = new CoreRuntimeState();
                EnableSentenceMode(state);
                state.TrySetConfigValue("整句自动提前上屏", earlyCommit ? "是" : "否", out _, out _);
                using (var engine = new InputMethodEngine(state, CreateSentenceDecoder(new Dictionary<string, List<string>>
                {
                    ["ab"] = new List<string> { "甲" },
                    ["cd"] = new List<string> { "乙" },
                    ["abcd"] = new List<string> { "丙" }
                })))
                {
                    TypeLetters(engine, "abcd");
                    Press(engine, navigationKey);
                    EngineUiSnapshot chosen = engine.GetUiSnapshot(5);
                    string selectedText = chosen.Candidates[chosen.SelectedCandidateIndex];
                    long generation = engine.GetDifferentialSnapshot(5).SentenceGeneration;
                    // Deliver the worker result after the key path has already published and
                    // navigated this generation. Model the delivery order without a timing race.
                    typeof(InputMethodEngine).GetMethod("ApplySentenceDecodeResult",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(engine, new object[] { generation, "abcd", state.LexiconVersion,
                            new SentenceDecodeResult { RawCode = "abcd", Candidates = Array.Empty<SentenceCandidate>() } });
                    True(chosen.Candidates.SequenceEqual(engine.GetUiSnapshot(5).Candidates),
                        "manual_selection.duplicate_beam_does_not_replace_candidates");
                    True(!engine.ApplySentenceNeuralScores(generation, "abcd", new[] { -100.0, 0.0 }),
                        "manual_selection.late_rerank_rejected");
                    EngineUiSnapshot after = engine.GetUiSnapshot(5);
                    True(chosen.Candidates.SequenceEqual(after.Candidates), "manual_selection.order_unchanged");
                    True(chosen.SelectedCandidateIndex == after.SelectedCandidateIndex, "manual_selection.index_unchanged");
                    Equal(selectedText, Press(engine, 0x20).TextToOutput, "manual_selection.commit");

                    TypeLetters(engine, "abcd");
                    generation = engine.GetDifferentialSnapshot(5).SentenceGeneration;
                    True(engine.ApplySentenceNeuralScores(generation, "abcd", new[] { -100.0, 0.0 }),
                        "manual_selection.next_composition_accepts_rerank");
                    Press(engine, navigationKey);
                    Press(engine, 0x08);
                    Press(engine, 0x44);
                    generation = engine.GetDifferentialSnapshot(5).SentenceGeneration;
                    True(engine.ApplySentenceNeuralScores(generation, "abcd", new[] { -100.0, 0.0 }),
                        "manual_selection.edit_reenables_rerank");
                }
            }
        }
    }
}
