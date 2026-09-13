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
        private static int ExportNativeCoreLexiconFixtures(string directory)
        {
            // New, dedicated fixture directory only; never overwrite existing data.
            if (Directory.Exists(directory)) throw new IOException("Fixture directory already exists");
            Directory.CreateDirectory(directory);
            var random = new Random(317905);
            var files = new List<string>();
            for (int test = 0; test < 40; test++)
            {
                var source = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < test * 13; i++)
                {
                    string code = "code" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    source[code] = Enumerable.Range(0, random.Next(8)).Select(n => new[]
                    {
                        "可", "可以", "\U00020000", "e\u0301", "a\0b", "\ud800", "", "显示\t上屏"
                    }[random.Next(8)]).ToList();
                }
                var compact = CompactLexicon.Build(source);
                if (test % 2 != 0)
                {
                    // Include images produced by the C# append/edit path, not
                    // only newly compiled layouts.
                    compact = compact.WithCandidates("a-new-code", new[] { "新", "\U00020000" });
                    source["a-new-code"] = new() { "新", "\U00020000" };
                }
                string name = "case-" + test.ToString("D2");
                using (var stream = File.Create(Path.Combine(directory, name + ".tclx"))) compact.WriteTo(stream);
                File.WriteAllLines(Path.Combine(directory, name + ".jsonl"), source.Select(pair => JsonSerializer.Serialize(new
                {
                    code = pair.Key.Select(c => (int)c).ToArray(),
                    candidates = pair.Value.Select(text => text.Select(c => (int)c).ToArray()).ToArray()
                })));
                files.Add(name);
            }
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(files));
            Console.WriteLine("Exported 40 C# TCLX parity fixtures");
            return 0;
        }
    }
}
