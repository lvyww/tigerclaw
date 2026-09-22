using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TigerClaw.Pinyin;

namespace TigerClaw.Core
{
    // Explicit offline acceptance entry. No singleton, registration, production
    // pipe/MMF, Overlay or Sentence sidecar is created by this command.
    internal static class FullPinyinProbe
    {
        internal static int Run(string directory, string output)
        {
            try
            {
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                var watch = Stopwatch.StartNew();
                using var resources = new PinyinResources(directory);
                using var decoder = new Decoder(resources.Lexicon, resources.Model, 200);
                writer.WriteStartObject(); writer.WriteNumber("load_ms", watch.Elapsed.TotalMilliseconds);
                writer.WriteNumber("lexicon_entries", resources.Lexicon.Count);
                writer.WriteBoolean("fivegram_loaded", resources.Reranker != null);
                writer.WriteString("rerank_load_error", resources.RerankError);
                writer.WriteStartArray("cases");
                foreach (string raw in new[] { "nihao", "yinhangka", "chongqing", "xi'an", "nih", "n", "nv", "nue", "yaoyouhuahaojiu", "yaoyouhuahenjiu" })
                {
                    watch.Restart(); var result = resources.Rank(decoder.Decode(raw, resources.CandidateLimit, completeLastSyllable: true));
                    writer.WriteStartObject(); writer.WriteString("raw", raw); writer.WriteNumber("ms", watch.Elapsed.TotalMilliseconds);
                    writer.WriteNumber("consumed", result.Consumed); writer.WriteString("tail", result.Tail);
                    writer.WriteStartArray("top10");
                    foreach (var candidate in result.Candidates.Take(10)) writer.WriteStringValue(candidate.Text);
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("expanded");
                foreach (var variant in new[] {
                    ("full", PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Aliases | PinyinSpellingOptions.Typos,
                        new[] { "nh", "nhao", "zhongg", "zhogn", "nve", "nver", "nv'er", "jintianxiawuquyinhangbanli", "aaaaaaaaaaaaaaaa" }),
                    ("xiaohe", PinyinSpellingOptions.DoublePinyin, new[] { "nihc", "vsgo", "ybhhka", "isqk" }) })
                {
                    using var expanded = new Decoder(resources.Lexicon, resources.Model, 200, spellingOptions: variant.Item2);
                    foreach (string raw in variant.Item3)
                    {
                        watch.Restart(); var result = resources.Rank(expanded.Decode(raw, resources.CandidateLimit, false, completeLastSyllable: true));
                        writer.WriteStartObject(); writer.WriteString("layout", variant.Item1); writer.WriteString("raw", raw);
                        writer.WriteNumber("ms", watch.Elapsed.TotalMilliseconds); writer.WriteNumber("expansions", result.Expansions);
                        writer.WriteStartArray("top10");
                        foreach (var candidate in result.Candidates.Take(10)) writer.WriteStringValue(candidate.Text);
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray(); writer.WriteString("rerank_score_error", resources.Reranker?.Error);
                writer.WriteEndObject(); writer.Flush(); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
    }
}
