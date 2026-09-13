using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        // Read-only: uses the production parser, never Initialize/ReloadConfig or
        // a live Core process. Accept one source file; no directory writes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Dictionary<string, List<string>> LoadBenchLexicon(string path)
        {
            var rows = new List<(string code, string text, int freq)>();
            typeof(CoreRuntimeState).GetMethod("ParseMbFile", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(string), rows.GetType() }, null).Invoke(null, new object[] { path, rows });
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OrderByDescending(row => row.freq))
            {
                string code = row.code.Trim().ToLowerInvariant();
                if (code.Length == 0) continue;
                if (!map.TryGetValue(code, out var values)) map.Add(code, values = new());
                if (!values.Contains(row.text)) values.Add(row.text);
            }
            return map;
        }

        private static int RunCompactLexiconBench(string path)
        {
            _ = CompactLexicon.Empty; // Exclude one-time static setup from measurement.
            long before = GC.GetTotalMemory(true);
            var baseline = LoadBenchLexicon(path);
            long dictionaryBytes = GC.GetTotalMemory(true) - before;
            var watch = Stopwatch.StartNew();
            var compact = CompactLexicon.Build(baseline);
            double buildMs = watch.Elapsed.TotalMilliseconds;
            foreach (var pair in baseline)
                if (!compact.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values))
                    throw new Exception("Compact/baseline candidate mismatch");
            if (!baseline.Keys.SequenceEqual(compact.Keys)) throw new Exception("Code order mismatch");
            string[] keys = baseline.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).ToArray();
            long checksum = 0;
            double Query(bool binary)
            {
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 200000 && keys.Length > 0; i++)
                {
                    string key = keys[i % keys.Length];
                    if (binary) checksum += compact[key][0].Length;
                    else checksum += baseline[key][0].Length;
                }
                return timer.Elapsed.TotalMilliseconds;
            }
            Query(false); Query(true);
            double dictionaryMs = Query(false), compactMs = Query(true);
            var edit = Stopwatch.StartNew();
            var edited = compact.WithCandidates(keys[0], baseline[keys[0]].AsEnumerable().Reverse().ToArray());
            double editMs = edit.Elapsed.TotalMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = Path.GetFileName(path), codes = baseline.Count,
                candidates = baseline.Values.Sum(values => (long)values.Count),
                dictionaryManagedBytesApprox = dictionaryBytes, compactBinaryBytes = compact.BinarySize,
                compactBuildMs = buildMs, queries = 200000, dictionaryQueryMs = dictionaryMs,
                compactQueryMs = compactMs, compactEditMs = editMs, checksum, allEntriesAndCodeOrderMatch = true
            }));
            GC.KeepAlive(baseline);
            GC.KeepAlive(compact);
            GC.KeepAlive(edited);
            return 0;
        }
    }
}
