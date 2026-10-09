using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunLearningFragmentTests()
        {
            learningChecks = 0;
            string root = Path.Combine(Path.GetTempPath(), "tigerclaw-fragment-learning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                LearningFragments(root);
                LearningFusionProtocol(root);
                Console.WriteLine(JsonSerializer.Serialize(new { test = "local_fragment_numeric_learning", status = "passed",
                    checks = learningChecks, physicalTsfTested = false }));
                return 0;
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        private static void LearningFragments(string root)
        {
            const string mode = "fragment-v1";
            SentenceLearningEvent Event(string code, string text, string context = "") =>
                new() { Mode = mode, Code = code, Text = text, Context = context, Time = 1720000000 };
            SentenceCandidate Candidate(string text, params (int Raw, int Text)[] points)
            {
                SentencePathBoundary boundary = null;
                foreach (var p in points)
                    boundary = new SentencePathBoundary { Previous = boundary, RawLength = p.Raw, TextLength = p.Text };
                return new SentenceCandidate { Text = text, Boundary = boundary, Source = SentenceCandidateSource.Composed };
            }
            List<SentenceLearningEvent> Events(string raw, SentenceCandidate before, SentenceCandidate selected,
                SentenceLearningSnapshot snapshot = null, int floor = 0, Func<string, bool> supplemental = null) =>
                SentenceLearning.SelectionEvents(raw, before, selected, floor, mode, snapshot, supplemental);

            var old = Candidate("滤掉", (3, 1), (5, 2));
            var selected = Candidate("淦掉", (3, 1), (5, 2));
            var bare = Events("KZJUY", old, selected);
            LearningCheck(bare.Count == 1 && bare[0].Code == "kzjuy" && bare[0].Text == "淦掉" &&
                bare[0].Context == "" && bare[0].RawStart == 0 && bare[0].RawEnd == 5,
                "standalone two-character choice learns one trusted phrase, not its individual character");
            var known = SentenceLearningSnapshot.Build(bare);
            old = Candidate("甲滤掉", (2, 1), (5, 2), (7, 3));
            selected = Candidate("甲淦掉", (2, 1), (5, 2), (7, 3));
            var prefix = Events("aakzjuy", old, selected, known);
            LearningCheck(prefix.Count == 1 && prefix[0].Code == "kzjuy" && prefix[0].Text == "淦掉" &&
                prefix[0].RawStart == 2 && prefix[0].RawEnd == 7 && prefix[0].Context == "甲",
                "known phrase can cross the one-character changed interval without binding the new prefix");
            var none = Events("aakzjuy", old, selected);
            LearningCheck(none.Count == 1 && none[0].Code == "kzj" && none[0].Text == "淦",
                "without known larger phrase use the original precise local diff");
            var supplement = Events("aakzjuy", old, selected, supplemental: text => text == "淦掉");
            LearningCheck(supplement.Count == 1 && supplement[0].Code == "kzjuy", "supplemental exact phrase is trusted evidence");
            var both = SentenceLearningSnapshot.Build(bare.Concat(new[] { Event("kzj", "淦") }));
            var longest = Events("aakzjuy", old, selected, both);
            LearningCheck(longest.Count == 1 && longest[0].Text == "淦掉", "whole phrase replaces overlapping character reward");
            LearningCheck(Events("aakzjuy", old, selected, known, 3).Count == 0, "locked boundary cannot be crossed");
            LearningCheck(Events("aakzjuy", selected, selected, known).Count == 0, "unchanged use creates no feedback");
            var suffix = Events("aakzjuycc", Candidate("甲滤掉丙", (2, 1), (5, 2), (7, 3), (9, 4)),
                Candidate("甲淦掉丙", (2, 1), (5, 2), (7, 3), (9, 4)), known);
            LearningCheck(suffix.Count == 1 && suffix[0].Code == "kzjuy", "unchanged suffix receives no new event");
            var disjoint = Events("aakzjuyccdd", Candidate("甲滤掉丙戊", (2, 1), (5, 2), (7, 3), (9, 4), (11, 5)),
                Candidate("甲淦掉丙丁", (2, 1), (5, 2), (7, 3), (9, 4), (11, 5)), known);
            LearningCheck(disjoint.Count == 2 && disjoint[0].Text == "淦掉" && disjoint[1].Text == "丁",
                "unrelated genuine diff survives phrase replacement");
            var ambiguous = SentenceLearningSnapshot.Build(new[] { Event("aabb", "甲乙"), Event("bbcc", "乙丙") });
            var fallback = Events("aabbccdd", Candidate("甲戊丙丁", (2, 1), (4, 2), (6, 3), (8, 4)),
                Candidate("甲乙丙丁", (2, 1), (4, 2), (6, 3), (8, 4)), ambiguous);
            LearningCheck(fallback.Count == 1 && fallback[0].Code == "bb" && fallback[0].Text == "乙",
                "overlapping equal longest fragments fall back without guesses");
            LearningCheck(Events("kzjuy", Candidate("滤掉", (3, 1), (4, 2)),
                Candidate("淦掉", (3, 1), (4, 2))).Count == 0, "incomplete boundary cannot become a phrase");
            LearningCheck(Events(new string('a', 129), Candidate("甲乙", (129, 2)),
                Candidate("淦掉", (129, 2))).Count == 0, "128-code maximum applies to new events");
            var unicode = Events("aabb", Candidate("\U00020000乙", (2, 2), (4, 3)),
                Candidate("\U00020000丙", (2, 2), (4, 3)));
            LearningCheck(unicode.Count == 1 && unicode[0].Text == "\U00020000丙",
                "bare two-character rule counts Unicode scalars");

            var dictionary = new Dictionary<string, List<string>>
            {
                ["kzj"] = new() { "滤", "淦" }, ["uy"] = new() { "掉" },
                ["aa"] = new() { "甲" }, ["bb"] = new() { "乙" }, ["cc"] = new() { "丙" }
            };
            foreach (int width in new[] { 1, 100 })
            {
                using var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(dictionary),
                    new ReviewCountingModel { Score = (_, _, target) => target == "淦" ? -5 : 0 },
                    beamWidth: width, rankPenalty: 0, isolationPenalty: SentenceIsolationPenalty.None,
                    allowDuplicateSingleCharacters: true, autoSelectMinCodeLength: 3);
                LearningCheck(decoder.Decode("aakzjuy").Candidates[0].Text == "甲滤掉", "fresh fixture baseline");
                decoder.SetLearning(known, mode);
                foreach (var pair in new[] { ("kzjuy", "淦掉"), ("aakzjuy", "甲淦掉"),
                    ("bbkzjuy", "乙淦掉"), ("aakzjuycc", "甲淦掉丙") })
                    LearningCheck(decoder.Decode(pair.Item1).Candidates[0].Text == pair.Item2,
                        "one ordinary local phrase score transfers with prefix/suffix and retained beam " + width + "/" + pair.Item1);
            }

            var directLexicon = SentenceLexiconIndex.Build(new Dictionary<string, List<string>> { ["aa"] = new() { "甲", "乙" } });
            foreach (int width in new[] { 1, 2, 100 })
            {
                using var decoder = new SentenceInputDecoder(directLexicon, NeutralSentenceLanguageModel.Instance,
                    beamWidth: width, isolationPenalty: SentenceIsolationPenalty.None, autoSelectMinCodeLength: 3);
                decoder.SetLearning(SentenceLearningSnapshot.Build(new[] { Event("aa", "乙") }), mode);
                foreach (int limit in new[] { 1, 2, 20 })
                    LearningCheck(decoder.Decode("aa", limit).Candidates[0].Text == "甲",
                        "Direct rank chain survives beam and display truncation " + width + "/" + limit);
                var chain = new[]
                {
                    new SentenceCandidate { Text = "C", Source = SentenceCandidateSource.Direct, DirectRank = 2, FinalScore = 10, LearningScore = 9 },
                    new SentenceCandidate { Text = "A", Source = SentenceCandidateSource.Composed, FinalScore = 5 },
                    new SentenceCandidate { Text = "B", Source = SentenceCandidateSource.Direct, DirectRank = 1, FinalScore = -20 }
                };
                decoder.ApplyFusionOrdering("aabb", chain);
                LearningCheck(string.Concat(chain.Select(c => c.Text)) == "BCA",
                    "numeric Direct preference carries earlier dictionary ranks across Composed");
            }
            var legacy = SentenceFusionPreference.CreateEvent(mode, "kzjuy", "淦掉", "滤掉", true, 5);
            var oldCorrection = Event("~e123", "E"); oldCorrection.Mode = "exact-correction-v1|" + mode;
            LearningCheck(SentenceLearningSnapshot.Build(new[] { legacy, oldCorrection }).IsEmpty,
                "both retired pair namespaces are ignored");
            var accumulator = new SentenceLearningSnapshot.Accumulator();
            LearningCheck(accumulator.Update(new[] { legacy, oldCorrection }, 1720000000).IsEmpty,
                "incremental replay also ignores old pairs");
            string path = Path.Combine(root, "legacy-window.txt");
            var rows = new StringBuilder("学习\t2024-07-03T09:46:40Z\t淦掉\tkzjuy\t\t1\tfragment-v1\tordinary\t\n");
            for (int i = 0; i < 10010; i++)
                rows.Append("学习\t2024-07-03T09:46:40Z\tD\t~f123\t\t1\t")
                    .Append(i % 2 == 0 ? "fusion-v1|" : "exact-correction-v1|")
                    .Append("fragment-v1\tlegacy-").Append(i).Append("\t\n");
            File.WriteAllText(path, rows.ToString(), new UTF8Encoding(false));
            byte[] beforeBytes = File.ReadAllBytes(path);
            var store = new SentenceLearningStore(path); store.Refresh();
            LearningCheck(store.Entries().Length == 1 && store.Snapshot.Score(mode, "kzjuy", "淦掉", "乙") == 6,
                "more than 10000 retired pairs do not evict ordinary active history");
            store.Confirm(new[] { legacy, oldCorrection });
            LearningCheck(beforeBytes.SequenceEqual(File.ReadAllBytes(path)), "reading old pairs and rejecting new pairs preserve existing bytes");
        }
    }
}
