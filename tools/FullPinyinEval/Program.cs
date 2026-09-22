using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullPinyinEval;
using TigerClaw.Core;
using Decoder = TigerClaw.Pinyin.Decoder;

internal sealed record Case(string Id, string Text, string Code, string Split, string Source);
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    internal static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    static void Manifest(string output, object value)
    {
        string text = Serialize(value), path = output + ".manifest.json";
        if (File.Exists(path) && File.ReadAllText(path) != text) throw new Exception("Resume fingerprint mismatch: " + path);
        if (File.Exists(output) && !File.Exists(path)) throw new Exception("Output without manifest: " + output);
        File.WriteAllText(path, text);
    }
    static HashSet<string> Seen(string output) => !File.Exists(output) ? new() : File.ReadLines(output).Select(l =>
    {
        using var d = JsonDocument.Parse(l);
        return d.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            ? null : d.RootElement.GetProperty("id").GetString();
    }).OfType<string>().ToHashSet();
    static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static async Task Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Modes: test | decode TABLE MODEL CASES OUTPUT BEAM JOBS words|singles dev|test|all | qwen INPUT OUTPUT HOST GGUF WORKER WORKERS | probe HOST GGUF | bench TABLE MODEL CASES OUTPUT BEAM");
        if (args[0] == "test") { Tests.Run(); return; }
        if (args[0] == "probe") { await QwenProbe(args[1], args[2]); return; }
        if (args[0] == "bench") { Benchmark(args); return; }
        if (args[0] == "decode")
        {
            string table = args[1], path = args[2], input = args[3], output = args[4];
            int beam = int.Parse(args[5]), jobs = int.Parse(args[6]); bool words = args[7] == "words";
            var cases = File.ReadLines(input).Select(l => JsonSerializer.Deserialize<Case>(l, Json)!).Where(c => args[8] == "all" || c.Split == args[8]).ToArray();
            Manifest(output, new { version = 1, table = Hash(table), model = Hash(path), cases = Hash(input), beam, jobs, words, split = args[8], executable = Hash(typeof(Program).Assembly.Location), core = Hash(typeof(SentenceNgramModel).Assembly.Location) });
            var seen = Seen(output); var lexicon = Lexicon.Load(table, words);
            using var writer = new StreamWriter(output, true, new UTF8Encoding(false)) { AutoFlush = true };
            object gate = new(); int count = seen.Count; var total = Stopwatch.StartNew();
            Parallel.ForEach(cases.Where(c => !seen.Contains(c.Id)), new ParallelOptions { MaxDegreeOfParallelism = jobs },
                // Independent views avoid SafeBuffer reference-count contention on every
                // mapped read. Windows still shares the physical read-only model pages.
                () => new Decoder(lexicon, SentenceNgramModel.Load(path), beam, true), (c, _, decoder) =>
                {
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "STOP"))) return decoder;
                    var watch = Stopwatch.StartNew(); var result = decoder.Decode(c.Code, 50, false); watch.Stop();
                    var row = new { c.Id, c.Text, c.Code, c.Split, c.Source, beam, words, ms = watch.Elapsed.TotalMilliseconds,
                        result.Consumed, result.Tail, result.Expansions, candidates = result.Candidates, rank = Array.FindIndex(result.Candidates, x => x.Text == c.Text) + 1 };
                    lock (gate)
                    {
                        writer.WriteLine(Serialize(row)); count++;
                        if (count % 100 == 0) Console.WriteLine($"decode beam={beam} words={words} {count}/{cases.Length} elapsed={total.Elapsed.TotalSeconds:F1}s");
                    }
                    return decoder;
                }, d => d.Dispose());
            Console.WriteLine($"decode complete={count == cases.Length} rows={count} elapsed={total.Elapsed.TotalSeconds:F1}s peak_bytes={Process.GetCurrentProcess().PeakWorkingSet64}");
            return;
        }
        if (args[0] == "qwen")
        {
            string input = args[1], output = args[2], host = args[3], gguf = args[4];
            int worker = int.Parse(args[5]), workers = int.Parse(args[6]);
            Manifest(output, new { version = 1, input = Hash(input), host = Hash(host), gguf = Hash(gguf), worker, workers, top = 10, executable = Hash(typeof(Program).Assembly.Location) });
            var seen = Seen(output); using var client = await QwenClient.Start(host, gguf);
            using var writer = new StreamWriter(output, true, new UTF8Encoding(false)) { AutoFlush = true };
            int count = 0, index = 0; var total = Stopwatch.StartNew();
            foreach (string line in File.ReadLines(input))
            {
                if (index++ % workers != worker) continue;
                if (File.Exists(Path.Combine(Path.GetDirectoryName(output)!, "STOP"))) break;
                using var document = JsonDocument.Parse(line); var item = document.RootElement;
                string id = item.GetProperty("id").GetString()!;
                if (seen.Contains(id)) continue;
                var candidates = item.GetProperty("candidates").EnumerateArray().Take(10).Select(x => x.GetProperty("text").GetString()!).ToArray();
                double[]? scores = null; string? error = null; var watch = Stopwatch.StartNew();
                try { scores = candidates.Length <= 1 ? Array.Empty<double>() : await client.Score(candidates); }
                catch (Exception ex) { error = ex.Message; }
                writer.WriteLine(Serialize(new { id, scores, error, ms = watch.Elapsed.TotalMilliseconds, count = candidates.Length }));
                count++;
                if (count % 25 == 0) Console.WriteLine($"qwen worker={worker} new={count} elapsed={total.Elapsed.TotalSeconds:F1}s");
                // A protocol/disconnect failure must not turn the remaining run into fake fallback results.
                if (error != null) throw new Exception("Qwen failed for " + id + ": " + error);
            }
            Console.WriteLine($"qwen done worker={worker} new={count} elapsed={total.Elapsed.TotalSeconds:F1}s peak_bytes={Process.GetCurrentProcess().PeakWorkingSet64}");
            return;
        }
        throw new ArgumentException("Unknown mode");
    }
    static void Benchmark(string[] args)
    {
        using var model = SentenceNgramModel.Load(args[2]); var lexicon = Lexicon.Load(args[1]);
        using var d = new Decoder(lexicon, model.CreateQuerySession(), int.Parse(args[5]), true);
        using var writer = new StreamWriter(args[4], false, new UTF8Encoding(false));
        var cases = File.ReadLines(args[3]).Select(l => JsonSerializer.Deserialize<Case>(l, Json)!).OrderBy(c => c.Id).Take(100);
        foreach (var c in cases)
        {
            d.Reset();
            for (int i = 1; i <= c.Code.Length; i++)
            {
                var watch = Stopwatch.StartNew(); var r = d.Decode(c.Code[..i]);
                writer.WriteLine(Serialize(new { c.Id, direction = "append", keys = i, ms = watch.Elapsed.TotalMilliseconds, r.Expansions }));
            }
            for (int i = c.Code.Length - 1; i >= 0; i--)
            {
                var watch = Stopwatch.StartNew(); var r = d.Decode(c.Code[..i]);
                writer.WriteLine(Serialize(new { c.Id, direction = "backspace", keys = i, ms = watch.Elapsed.TotalMilliseconds, r.Expansions }));
            }
        }
    }
    static async Task QwenProbe(string host, string gguf)
    {
        using var client = await QwenClient.Start(host, gguf);
        var texts = new[] { "你好", "您好", "你号", "拟好", "泥好", "天气很好", "今天下雨", "我们出发", "现在回家", "输入完成" };
        var reference = await client.Score(texts);
        foreach (int count in new[] { 1, 5, 10 })
        {
            var scores = await client.Score(texts.Take(count).ToArray());
            Tests.Check(scores.Length == count && scores.All(double.IsFinite), "qwen cardinality");
            for (int i = 0; i < count; i++) Tests.Check(Math.Abs(scores[i] - reference[i]) < 0.05, "qwen score/batch consistency");
        }
        var reversed = await client.Score(texts.Reverse().ToArray());
        for (int i = 0; i < 10; i++) Tests.Check(Math.Abs(reversed[9-i]-reference[i]) < 0.05, "qwen ordered scores");
        bool rejected = false; try { await client.Score(texts.Append("多余候选").ToArray()); } catch (InvalidDataException) { rejected = true; }
        Tests.Check(rejected, "qwen rejects eleven");
        Tests.Check((await client.Score(texts.Take(1).ToArray())).Length == 1, "qwen recovers after rejection");
        client.StopForTest();
        bool disconnected = false;
        try { await client.Score(texts); } catch (IOException) { disconnected = true; }
        Tests.Check(disconnected,"qwen process loss is an explicit failure");
        using var restarted = await QwenClient.Start(host, gguf);
        Tests.Check((await restarted.Score(texts)).Length == 10,"qwen fresh process recovery");
        Console.WriteLine("Qwen 1/5/10, ordering, over-limit, disconnect and recovery: PASS");
    }
}

internal sealed class QwenClient : IDisposable
{
    private readonly Process child;
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private int seq;
    private QwenClient(Process child, NamedPipeClientStream pipe)
    {
        this.child = child; this.pipe = pipe;
        reader = new(pipe, new UTF8Encoding(false), false, 65536, true);
        writer = new(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
    }
    internal static async Task<QwenClient> Start(string host, string gguf)
    {
        if (!Path.GetFileName(host).Contains("FullPinyinProbe", StringComparison.Ordinal)) throw new ArgumentException("An isolated FullPinyinProbe host is required");
        string name = "TigerClaw.FullPinyinEval." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--pipe", name, "--model", gguf, "--parent-pid", Environment.ProcessId.ToString() }) info.ArgumentList.Add(arg);
        var child = Process.Start(info)!;
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(120000); return new(child, pipe); }
        catch { pipe.Dispose(); if (!child.HasExited) child.Kill(true); child.Dispose(); throw; }
    }
    internal async Task<double[]> Score(string[] texts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        int request = ++seq;
        await writer.WriteLineAsync(Program.Serialize(new { type = "rerank", seq = request, generation = request, raw_code = "offline", candidates = texts }).AsMemory(), timeout.Token);
        string line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Scorer disconnected");
        using var doc = JsonDocument.Parse(line); var result = doc.RootElement;
        if (!result.GetProperty("success").GetBoolean()) throw new InvalidDataException(line);
        if (result.GetProperty("seq").GetInt32() != request || result.GetProperty("generation").GetInt32() != request || result.GetProperty("raw_code").GetString() != "offline") throw new InvalidDataException("Stale scorer response");
        double[] scores = result.GetProperty("scores").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (scores.Length != texts.Length || scores.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid scorer output");
        return scores;
    }
    internal void StopForTest() { child.Kill(true); child.WaitForExit(); }
    public void Dispose()
    {
        try { writer.Dispose(); }
        catch (IOException) { /* A crashed child must not prevent owned-process cleanup. */ }
        finally
        {
            reader.Dispose(); pipe.Dispose();
            if (!child.HasExited) child.Kill(true);
            child.Dispose();
        }
    }
}
