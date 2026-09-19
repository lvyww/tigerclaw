using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Same decoder/parameters, different file layout. No production IPC or UI.
        private static int RunMobileComparison(string legacy, string mobile, string codes,
            string queries, string cases, string output)
        {
            var loads = new List<double>();
            SentenceNgramModel Load(string path)
            {
                long start = Stopwatch.GetTimestamp(); var result = SentenceNgramModel.Load(path);
                loads.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); return result;
            }
            using var a = Load(legacy); using var b = Load(mobile);
            using (var sa = a.CreateQuerySession()) using (var sb = b.CreateQuerySession())
            {
                foreach (string line in File.ReadLines(queries))
                {
                    string[] p = line.Split('\t');
                    string x = char.ConvertFromUtf32(int.Parse(p[0])), y = char.ConvertFromUtf32(int.Parse(p[1])), z = char.ConvertFromUtf32(int.Parse(p[2]));
                    ReviewNumber(sa.LogProbability(x, y, z), sb.LogProbability(x, y, z), "production format score");
                    ReviewNumber(sa.LogProbability(x, y, z, false), sb.LogProbability(x, y, z, false), "production no-unigram score");
                    Review(sa.HasObservedBigram(y, z) == sb.HasObservedBigram(y, z), "production observation");
                }
            }
            Console.WriteLine("Production query parity passed");
            var table = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(codes))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] p = line.Split('\t');
                if (p.Length != 2) throw new InvalidDataException("Bad code row");
                if (!table.TryGetValue(p[1], out var list)) table[p[1]] = list = new();
                list.Add(p[0]);
            }
            string whitelistPath = Path.Combine(Path.GetDirectoryName(codes), "tiger_sentence.full_code_whitelist.txt");
            var whitelist = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(whitelistPath))
                if (line.Length > 0 && !line.StartsWith('#')) whitelist.Add(line.Trim());
            var index = SentenceLexiconIndex.Build(table, SentenceCharacterRanks.TakeTop(1500), whitelist);
            var prior = SentenceLexicalPrior.LoadEmbedded();
            SentenceInputDecoder Decoder(SentenceNgramModel model) => new(index, model,
                emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5, allowDuplicateSingleCharacters: true,
                canonicalCodeReward: 2, canonicalIsolationFactor: 0, canonicalIsolationMinCodeLength: 4,
                lexicalPrior: prior, lexicalPriorWeight: .1, lexicalCandidateLimit: 5);
            using var writer = new StreamWriter(output + ".tsv");
            writer.WriteLine("case\tpass\toperation\tlength\tlegacy_ms\tmobile_ms");
            var timings = new Dictionary<string, (List<double> Legacy, List<double> Mobile)>();
            int sample = 0;
            foreach (string line in File.ReadLines(cases))
            {
                string[] p = line.Split('\t'); string code = p[2];
                using var da = Decoder(a); using var db = Decoder(b);
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int step = 1; step < code.Length * 2; step++)
                    {
                        bool append = step <= code.Length;
                        int length = append ? step : code.Length * 2 - step;
                        string raw = code[..length];
                        SentenceDecodeResult ra = null, rb = null;
                        double ma = 0, mb = 0;
                        void Run(bool legacyFirst)
                        {
                            long start = Stopwatch.GetTimestamp();
                            var result = (legacyFirst ? da : db).Decode(raw, 20, true);
                            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (legacyFirst) { ra = result; ma = elapsed; } else { rb = result; mb = elapsed; }
                        }
                        bool first = (sample + pass + step) % 2 == 0;
                        Run(first); Run(!first);
                        ReviewSame(ra, rb);
                        string operation = append ? (step == 1 ? "first_key" : "append") : "backspace";
                        string group = $"{pass}:{operation}:{(length > 24 ? "long" : "short")}";
                        if (!timings.TryGetValue(group, out var values)) timings[group] = values = (new(), new());
                        values.Legacy.Add(ma); values.Mobile.Add(mb);
                        writer.WriteLine(FormattableString.Invariant($"{p[0]}\t{pass}\t{operation}\t{length}\t{ma:R}\t{mb:R}"));
                    }
                }
                sample++; writer.Flush(); Console.WriteLine($"Compared {sample}: {code.Length} keys");
            }
            object Stats(List<double> values)
            {
                values.Sort();
                return new { n = values.Count, p50 = values[(values.Count - 1) / 2],
                    p95 = values[(int)Math.Ceiling(values.Count * .95) - 1], max = values[^1] };
            }
            var report = new { model_load_ms = loads, cases = sample, snapshots = _reviewSnapshots, checks = _reviewChecks,
                timings = timings.ToDictionary(p => p.Key, p => new { legacy = Stats(p.Value.Legacy), mobile = Stats(p.Value.Mobile) }),
                mode = "Release managed, sequential alternating paired decode; OS file cache not flushed; no Qwen/learning/supplement/UI; includes early-commit evidence" };
            File.WriteAllText(output + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
    }
}
