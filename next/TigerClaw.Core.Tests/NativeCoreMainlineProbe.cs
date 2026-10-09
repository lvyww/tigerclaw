using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static int RunNativeCoreMainlineProbe(string input, string output)
        {
            using var writer = new StreamWriter(output);
            foreach (string line in File.ReadLines(input))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                string Text(JsonElement value, string name, string fallback = "") => value.TryGetProperty(name, out var v) ? v.GetString() : fallback;
                int Number(JsonElement value, string name, int fallback = 0) => value.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
                var events = root.TryGetProperty("events", out var inputEvents)
                    ? inputEvents.EnumerateArray().Select((e, i) => new SentenceLearningEvent {
                        Id = Text(e, "id", i.ToString()), Time = Number(e, "time", 1), Mode = Text(e, "mode", "test-v1"),
                        Code = Text(e, "code"), Text = Text(e, "text"), Context = Text(e, "context"), Levels = Number(e, "levels", 1)
                    }).ToArray() : Array.Empty<SentenceLearningEvent>();
                var snapshot = SentenceLearningSnapshot.Build(events);
                if (Text(root, "op") == "journal")
                {
                    var store = new SentenceLearningStore(Text(root, "path"));
                    store.Confirm(events); store.Refresh();
                    writer.WriteLine(JsonSerializer.Serialize(store.Entries().Select(e => new {
                        id=e.Id, time=e.Time, mode=e.Mode, code=e.Code, text=e.Text, context=e.Context, levels=e.Levels }).ToArray()));
                    continue;
                }
                if (Text(root, "op") == "learning")
                {
                    var values = root.GetProperty("queries").EnumerateArray().Select(q => {
                        string mode = Text(q, "mode", "test-v1"), code = Text(q, "code"), text = Text(q, "text"), context = Text(q, "context");
                        return new { score = snapshot.Score(mode, code, text, context), confidence = snapshot.ConfidenceScore(mode, code, text, context),
                            prefix = snapshot.PrefixScore(mode, code, text, context), hash = SentenceLearning.ConfigurationHash(text) };
                    }).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(values));
                    continue;
                }
                if (Text(root, "op") != "decode") throw new ArgumentException("Unknown mainline probe operation");
                var lexicon = root.GetProperty("lexicon").EnumerateArray().ToDictionary(
                    e => e[0].GetString(), e => e[1].EnumerateArray().Select(s => s.GetString()).ToList(), StringComparer.OrdinalIgnoreCase);
                var supplements = root.TryGetProperty("supplements", out var supp)
                    ? supp.EnumerateArray().Select(e => SentenceSupplementEntry.Create(e[0].GetString(), e[1].GetInt64())).ToArray()
                    : Array.Empty<SentenceSupplementEntry>();
                using var model = new SentenceFivegramModel(Text(root, "model"));
                using var decoder = new SentenceInputDecoder(SentenceLexiconIndex.Build(lexicon), model,
                    beamWidth: Number(root, "beam", 2000), isolationPenalty: root.TryGetProperty("isolation", out var iso) && iso.GetBoolean() ? SentenceIsolationPenalty.CreateDefault() : SentenceIsolationPenalty.None,
                    supplementMatcher: SentenceSupplementMatcher.Build(supplements), emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5, allowDuplicateSingleCharacters: true,
                    canonicalIsolationFactor: root.TryGetProperty("protectedFactor", out var factor) ? factor.GetDouble() : 1,
                    canonicalCodeReward: root.TryGetProperty("canonical", out var canonical) ? canonical.GetDouble() : 0,
                    lexicalPrior: SentenceLexicalPrior.LoadEmbedded(),
                    lexicalPriorWeight: root.TryGetProperty("lexical", out var lexical) ? lexical.GetDouble() : 0,
                    autoSelectMinCodeLength: Number(root, "auto_select_min_code_length", 3));
                decoder.PreserveTruncatedEarlyCommitEvidence = root.TryGetProperty("preserve", out var preserve) && preserve.GetBoolean();
                decoder.SetLearning(snapshot, Text(root, "mode", "test-v1"));
                SentenceLockedPrefix locked = null;
                var results = root.GetProperty("raws").EnumerateArray().Select(raw => {
                    if (locked != null && !raw.GetString().StartsWith(locked.RawCode, StringComparison.Ordinal)) locked = null;
                    var result = decoder.Decode(raw.GetString(), Number(root, "limit", 5), true, null, locked);
                    if (root.TryGetProperty("lockRaw", out var lockRaw) && lockRaw.GetString() == raw.GetString() && locked == null)
                    {
                        var candidate = result.Candidates[Number(root, "lockIndex", 1)];
                        locked = new SentenceLockedPrefix(raw.GetString(), candidate.Text, candidate.Boundary);
                    }
                    return new { raw = result.RawCode, candidates = result.Candidates.Select(c => new {
                        text = c.Text, score = c.FinalScore, mass = c.ConfidenceScore, learning = c.LearningScore, code = c.CodeScore,
                        source = (int)c.Source, directRank = c.DirectRank, lexical = c.LexicalScore, segmented = c.SegmentedCode
                    }).ToArray(), evidence = new {
                        truncated = result.EarlyCommitEvidence.ConfidenceTruncated,
                        proposal = result.EarlyCommitEvidence.Proposal ?? string.Empty,
                        low = result.EarlyCommitEvidence.NeutralLowConfidence,
                        merged = result.EarlyCommitEvidence.MergedIncompleteTail,
                        prefixes = result.EarlyCommitEvidence.Prefixes.OrderBy(p => p.Text, StringComparer.Ordinal).ThenBy(p => p.RawLength)
                            .Select(p => new {text=p.Text, raw=p.RawLength, share=p.Share, baseShare=p.BaseShare, closed=p.BoundaryClosed, boundaryShare=p.BoundaryShare}).ToArray()
                    } };
                }).ToArray();
                writer.WriteLine(JsonSerializer.Serialize(results));
            }
            return 0;
        }
    }
}
