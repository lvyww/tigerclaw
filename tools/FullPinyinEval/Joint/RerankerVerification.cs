using System.Diagnostics;
using System.Text.Json;
using TigerClaw.Pinyin;

internal static class RerankerVerification
{
    // verify-reranker trigram fivegram frozen-baseline frozen-fivegram output
    internal static void Run(string[] args)
    {
        using var lm = new TigerClaw.Pinyin.Kenlm(args[1]);
        using var reranker = new PinyinReranker(args[2]);
        bool rejected = false;
        try { using var invalid = new PinyinReranker(args[1]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Fivegram loader accepted trigram");
        rejected = false;
        try { using var invalid = new TigerClaw.Pinyin.Kenlm(args[2]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Trigram loader accepted fivegram");
        using var expected = File.ReadLines(args[4]).GetEnumerator();
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        int rows = 0, correct = 0, comparisons = 0; double maxDelta = 0;
        var milliseconds = new List<double>();
        foreach (var line in File.ReadLines(args[3]))
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!expected.MoveNext()) throw new Exception("Missing expected row");
            using var reference = JsonDocument.Parse(expected.Current);
            var e = reference.RootElement;
            if (r.GetProperty("id").GetString() != e.GetProperty("id").GetString()) throw new Exception("ID mismatch");
            var candidates = JsonSerializer.Deserialize<Candidate[]>(r.GetProperty("candidates").GetRawText(), json)!;
            var original = new DecodeResult(candidates, r.GetProperty("consumed").GetInt32(), "", 0);
            var watch = Stopwatch.StartNew(); var ranked = reranker.Rank(original);
            milliseconds.Add(watch.Elapsed.TotalMilliseconds);
            if (reranker.Error != null) throw new Exception(reranker.Error);
            var saved = e.GetProperty("candidates").EnumerateArray().ToArray();
            if (saved.Length != ranked.Candidates.Length) throw new Exception("Candidate count mismatch");
            for (int i = 0; i < saved.Length; i++)
            {
                var delta = Math.Abs(ranked.Candidates[i].Score - saved[i].GetProperty("score").GetDouble());
                maxDelta = Math.Max(maxDelta, delta); comparisons++;
                if (ranked.Candidates[i].Text != saved[i].GetProperty("text").GetString() || delta > 1e-9)
                    throw new Exception($"Score/order mismatch row={rows} rank={i} delta={delta}");
            }
            if (ranked.Candidates.FirstOrDefault()?.Text == r.GetProperty("text").GetString()) correct++;
            rows++;
        }
        if (expected.MoveNext()) throw new Exception("Extra expected row");
        milliseconds.Sort();
        using var output = new FileStream(args[5], FileMode.CreateNew);
        JsonSerializer.Serialize(output, new { rows, correct, comparisons, maxDelta,
            p50_ms = milliseconds[milliseconds.Count / 2], p95_ms = milliseconds[(int)(milliseconds.Count * .95)],
            loaderOrderChecks = true, note = "Managed production rerank only; excludes search, loading, UI" }, new JsonSerializerOptions { WriteIndented = true });
    }
}
