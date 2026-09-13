using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

// Diagnostics only: a separate runtime directory, unique scorer pipe per worker,
// and production Engine.ApplySentenceNeuralScores for the actual ranking policy.
string repo = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
int worker = int.Parse(args[2]), workers = int.Parse(args[3]);
bool replay = args.Length > 4 && args[4] == "--replay";
string replayPrefix = args.Length > 5 ? args[5] : "calibration";
int maxCases = args.Length > 4 && !replay ? int.Parse(args[4]) : int.MaxValue;
Directory.CreateDirectory(output);
string release = Path.Combine(repo, "release_arm64");
string ngramPath = Path.Combine(release, "Models", "sentence-ngram-v2.bin");
string qwenPath = Path.Combine(release, "sentence", "Models", "sentence-qwen-q8.gguf");
// Use a distinct process name as well as a distinct pipe. Daily runtime launch
// checks and publish scripts may inspect/terminate TigerClaw.Sentence by name.
string hostPath = Path.Combine(output, $"TigerClaw.QwenLengthProbe{worker}.exe");
if (!File.Exists(hostPath))
    File.Copy(Path.Combine(release, "sentence", "TigerClaw.Sentence.exe"), hostPath);
object Fingerprint(string path)
{
    using var file = File.OpenRead(path);
    string hash = Convert.ToHexString(SHA256.HashData(file));
    file.Position = 0;
    byte[] magic = new byte[8];
    file.ReadExactly(magic);
    return new { path, bytes = file.Length, sha256 = hash, magic_hex = Convert.ToHexString(magic) };
}
var state = new CoreRuntimeState(Path.Combine(output, "state-" + worker));
Directory.CreateDirectory(Path.Combine(output, "state-" + worker));
void Set(string key, string value)
{
    if (!state.TrySetConfigValue(key, value, out _, out string reason)) throw new Exception(key + ": " + reason);
}
Set("码表存储位置", Path.Combine(release, "码表"));
Set("当前码表", "虎整句");
Set("自动启用整句模式", "是");
Set("整句自动提前上屏", "否");
Set("整句神经重排", "是");
Set("允许单字重码组句", "是");
Set("高频字仅使用最优码组句", "1500");
foreach (var line in File.ReadLines(Path.Combine(release, "config.txt")))
{
    var parts = line.Split('\t');
    if (parts.Length >= 2 && parts[0] == "整句允许全码组句白名单") Set(parts[0], parts[1]);
}
if (!state.ReloadLexicon()) throw new Exception("Lexicon load failed");
var source = state.GetSentenceLexiconSnapshot();
if (source.Count < 1000) throw new Exception("Unexpected lexicon size: " + source.Count);
var lexicon = SentenceLexiconIndex.Build(source, SentenceCharacterRanks.TakeTop(1500),
    CoreRuntimeState.ParseCharacterSet(state.GetSentenceFullCodeWhitelistText()));
var supplement = SentenceSupplementMatcher.Build(state.GetSentenceSupplementSnapshot());
using var model = SentenceNgramModel.Load(ngramPath);
var decoder = new SentenceInputDecoder(lexicon, model, emittedCharacterReward: 2,
    wholeInputSingleCharacterReward: 5, supplementMatcher: supplement, allowDuplicateSingleCharacters: true);
using var engine = new InputMethodEngine(state, decoder);
engine.ProcessKey(65, 0, "down", false, false, false, false, false, true, 1, false);
var restart = typeof(InputMethodEngine).GetMethod("RestartSentenceInput", BindingFlags.Instance | BindingFlags.NonPublic);
var decodedField = typeof(InputMethodEngine).GetField("_sentenceDecodeResult", BindingFlags.Instance | BindingFlags.NonPublic);
var preferenceMethod = typeof(InputMethodEngine).GetMethod("ShouldPreferSentenceScoreOverLexiconRank", BindingFlags.Instance | BindingFlags.NonPublic);
var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(output, "cases.json")));
if (replay)
{
    var expected = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(output, replayPrefix + "-predictions.json")));
    int checkedCount = 0;
    foreach (string line in File.ReadLines(Path.Combine(output, $"rows-{worker}.jsonl")))
    {
        using var document = JsonDocument.Parse(line);
        var row = document.RootElement;
        int id = row.GetProperty("id").GetInt32();
        string code = row.GetProperty("code").GetString();
        decoder.ResetDecodeCache();
        restart.Invoke(engine, new object[] { code, false });
        var candidates = ((SentenceDecodeResult)decodedField.GetValue(engine)).Candidates;
        var saved = row.GetProperty("pool").EnumerateArray().ToArray();
        if (candidates.Length != saved.Length) throw new Exception($"Pool size changed: {id}");
        for (int j = 0; j < saved.Length; j++)
            if (candidates[j].Text != saved[j].GetProperty("text").GetString() ||
                Math.Abs(candidates[j].BaseScore - saved[j].GetProperty("score").GetDouble()) > 1e-9 ||
                candidates[j].MaxLexiconRank != saved[j].GetProperty("rank").GetInt32())
                throw new Exception($"Base decode changed: {id}/{j}");
        double[] scores = row.GetProperty("qwen_scores").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (scores.Length > 0)
        {
            var snapshot = engine.GetDifferentialSnapshot(20);
            if (!engine.ApplySentenceNeuralScores(snapshot.SentenceGeneration, code, scores))
                throw new Exception($"Replay rejected: {id}");
        }
        if (saved.Length > 0 && candidates[0].Text != saved[expected[id]].GetProperty("text").GetString())
            throw new Exception($"Production/offline prediction differs: {id}");
        checkedCount++;
        if (checkedCount % 5000 == 0) Console.WriteLine($"REPLAY worker={worker} checked={checkedCount}");
    }
    if (checkedCount != 25000) throw new Exception("Incomplete replay");
    File.WriteAllText(Path.Combine(output, $"{replayPrefix}-replay-{worker}.json"), JsonSerializer.Serialize(new
    {
        checkedCount, core = Fingerprint(typeof(InputMethodEngine).Assembly.Location),
        predictions = Fingerprint(Path.Combine(output, replayPrefix + "-predictions.json")), completed_utc = DateTime.UtcNow
    }));
    Console.WriteLine($"REPLAY PASS worker={worker} checked={checkedCount}");
    return;
}
var seen = new HashSet<int>();
string resultPath = Path.Combine(output, $"rows-{worker}.jsonl");
if (File.Exists(resultPath))
    foreach (var line in File.ReadLines(resultPath))
    {
        using var saved = JsonDocument.Parse(line);
        if (!saved.RootElement.TryGetProperty("scoring_policy", out var policy) ||
            policy.GetString() != "candidate-convex-v1")
            throw new Exception("Different scoring policy: use a fresh output directory, or --replay convex.");
        seen.Add(saved.RootElement.GetProperty("id").GetInt32());
    }
// Never append scores from a different build/model/case set on resume.
var manifestText = JsonSerializer.Serialize(new
{
    worker, workers, processor_count = Environment.ProcessorCount, created_utc = DateTime.UtcNow,
    ngram = Fingerprint(ngramPath), qwen = Fingerprint(qwenPath), host = Fingerprint(hostPath),
    core = Fingerprint(typeof(InputMethodEngine).Assembly.Location), cases = Fingerprint(Path.Combine(output, "cases.json")),
    schema = "虎整句", high_freq_limit = 1500, duplicate_single = true,
    whitelist = state.GetSentenceFullCodeWhitelistText(), beam = 2000, reward = 2.0, whole_single_reward = 5.0,
    lexicon_codes = source.Count, lexicon_files = Directory.GetFiles(Path.Combine(release, "码表", "虎整句"), "*.txt").Select(Fingerprint).ToArray(),
    policy = "candidate-convex-v1; paired legacy and shared-calibration comparisons; no early commit"
}, new JsonSerializerOptions { WriteIndented = true });
string manifestPath = Path.Combine(output, $"manifest-{worker}.json");
if (seen.Count > 0)
{
    using var previous = JsonDocument.Parse(File.ReadAllText(manifestPath));
    using var current = JsonDocument.Parse(manifestText);
    foreach (string key in new[] { "worker", "workers", "ngram", "qwen", "host", "core", "cases", "whitelist", "lexicon_files", "policy" })
        if (previous.RootElement.GetProperty(key).GetRawText() != current.RootElement.GetProperty(key).GetRawText())
            throw new Exception("Resume fingerprint/settings mismatch: " + key);
}
string pipeName = "TigerClaw.LengthEval." + Environment.ProcessId;
var startInfo = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
foreach (var arg in new[] { "--pipe", pipeName, "--model", qwenPath, "--parent-pid", Environment.ProcessId.ToString() }) startInfo.ArgumentList.Add(arg);
using var child = Process.Start(startInfo);
var watch = Stopwatch.StartNew();
try
{
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(120000);
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
    if (seen.Count == 0) File.WriteAllText(manifestPath, manifestText);
    using var sink = new StreamWriter(resultPath, true, new UTF8Encoding(false)) { AutoFlush = true };
    int completed = 0, called = 0;
    for (int id = worker; id < cases.Length && completed < maxCases; id += workers)
    {
        if (seen.Contains(id)) continue;
        if (File.Exists(Path.Combine(output, "STOP"))) break;
        var item = cases[id];
        string text = item.GetProperty("text").GetString(), code = item.GetProperty("code").GetString();
        decoder.ResetDecodeCache();
        restart.Invoke(engine, new object[] { code, false });
        var result = (SentenceDecodeResult)decodedField.GetValue(engine);
        var candidates = result.Candidates;
        int baseRank = Array.FindIndex(candidates, c => c.Text == text) + 1;
        string baseTop = candidates.FirstOrDefault()?.Text;
        string legacyTop = baseTop, sharedTop = baseTop;
        bool prefer = false;
        var pool = candidates.Select(c => new { text = c.Text, score = c.BaseScore, rank = c.MaxLexiconRank }).ToArray();
        double[] scores = Array.Empty<double>();
        double weight = 0;
        double[] alphas = Array.Empty<double>();
        string provider = "single-or-empty-no-ranking-change";
        if (candidates.Length > 1)
        {
            int count = Math.Min(5, candidates.Length);
            prefer = (bool)preferenceMethod.Invoke(engine, new object[] { candidates, count });
            int baseLength = InputMethodEngine.GetSentenceNeuralBaseTopLength(candidates, count, prefer);
            if (baseLength >= 2 && baseLength <= 6)
                alphas = candidates.Take(count).Select(c => InputMethodEngine.GetSentenceNeuralAlpha(
                    new StringInfo(c.Text ?? "").LengthInTextElements)).ToArray();
            else
                weight = InputMethodEngine.GetSentenceNeuralWeight(baseLength);
            var snapshot = engine.GetDifferentialSnapshot(20);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "rerank", seq = id + 1,
                generation = snapshot.SentenceGeneration, raw_code = code,
                candidates = candidates.Take(5).Select(c => c.Text).ToArray() }));
            string response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120));
            if (response == null) throw new IOException("Scorer closed its pipe; no fallback result was recorded.");
            using var json = JsonDocument.Parse(response);
            if (!json.RootElement.GetProperty("success").GetBoolean()) throw new Exception(response);
            provider = json.RootElement.GetProperty("provider").GetString();
            if (provider != "llama.cpp-cpu-q8") throw new Exception("Unexpected provider: " + provider);
            scores = json.RootElement.GetProperty("scores").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (scores.Length != count || scores.Any(s => !double.IsFinite(s))) throw new Exception("Invalid scorer values");
            string RankWithWeight(double lambda)
            {
                var ranked = candidates.Take(count).Select((c, j) => new SentenceCandidate
                {
                    Text = c.Text, MaxLexiconRank = c.MaxLexiconRank,
                    FinalScore = c.BaseScore + lambda * scores[j]
                }).ToArray();
                Array.Sort(ranked, Comparer<SentenceCandidate>.Create(prefer
                    ? SentenceCandidate.CompareByScoreThenLexiconRank : SentenceCandidate.CompareByLexiconRankThenScore));
                return ranked[0].Text;
            }
            double legacyWeight = baseLength > 0 && baseLength <= 2 ? .30 : .84;
            double sharedWeight = baseLength switch { 2 => .10, 3 => .35, 4 => .45, 5 => .25, 6 => .55, _ => legacyWeight };
            legacyTop = RankWithWeight(legacyWeight);
            sharedTop = RankWithWeight(sharedWeight);
            if (!engine.ApplySentenceNeuralScores(snapshot.SentenceGeneration, code, scores)) throw new Exception("Qwen result rejected");
            called++;
        }
        int afterRank = Array.FindIndex(candidates, c => c.Text == text) + 1;
        sink.WriteLine(JsonSerializer.Serialize(new { id, text, code, length = item.GetProperty("length").GetInt32(),
            source = item.GetProperty("source").GetString(), base_rank = baseRank, qwen_rank = afterRank,
            base_top = baseTop, qwen_top = candidates.FirstOrDefault()?.Text, weight, alphas,
            legacy_top = legacyTop, shared_top = sharedTop, prefer_score = prefer,
            scoring_policy = "candidate-convex-v1", provider, pool, qwen_scores = scores }));
        completed++;
        if (completed % 100 == 0) Console.WriteLine($"worker={worker} completed={completed} resumed={seen.Count} qwen_calls={called} elapsed_s={watch.Elapsed.TotalSeconds:F1}");
    }
    await writer.WriteLineAsync("{\"type\":\"shutdown\",\"seq\":999999}");
    Console.WriteLine($"DONE worker={worker} completed={completed} elapsed_s={watch.Elapsed.TotalSeconds:F1}");
}
finally
{
    if (!child.WaitForExit(3000)) child.Kill();
}
