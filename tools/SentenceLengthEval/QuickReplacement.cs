using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TigerClaw.Core;

// Offline only. Uses production resource construction and acceptance/ranking.
internal static class QuickReplacement
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Encode(object value) => JsonSerializer.Serialize(value, Json);
    static void Save(string path, object value) => File.WriteAllText(path, Encode(value));
    static object Identity(string path) { using var f = File.OpenRead(path); return new { path, bytes = f.Length, sha256 = Convert.ToHexString(SHA256.HashData(f)) }; }
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo Restart = typeof(InputMethodEngine).GetMethod("RestartSentenceInput", Private);
    static readonly FieldInfo Result = typeof(InputMethodEngine).GetField("_sentenceDecodeResult", Private);
    static readonly FieldInfo Decoder = typeof(InputMethodEngine).GetField("_sentenceInputDecoder", Private);
    static readonly MethodInfo Prefer = typeof(InputMethodEngine).GetMethod("ShouldPreferSentenceScoreOverLexiconRank", Private);
    static SentenceCandidate[] Decode(InputMethodEngine engine, string code)
    {
        // RestartSentenceInput clears all composition/context itself. Escape
        // would additionally switch to CnIdle and invalidate neural acceptance.
        ((SentenceInputDecoder)Decoder.GetValue(engine)).ResetDecodeCache();
        Restart.Invoke(engine, new object[] { code, false });
        return ((SentenceDecodeResult)Result.GetValue(engine)).Candidates;
    }
    static object[] Pool(SentenceCandidate[] candidates) => candidates.Select(c => (object)new
        { text = c.Text, score = c.BaseScore, rank = c.MaxLexiconRank, learning = c.LearningScore }).ToArray();

    static object Ranking(InputMethodEngine engine, SentenceCandidate[] candidates)
    {
        int count = Math.Min(5, candidates.Length);
        bool preferScore = (bool)Prefer.Invoke(engine, new object[] { candidates, count });
        int baseLength = InputMethodEngine.GetSentenceNeuralBaseTopLength(candidates, count, preferScore);
        int[] lengths = candidates.Take(count).Select(c => new StringInfo(c.Text ?? "").LengthInTextElements).ToArray();
        return new
        {
            rerank_count = count,
            prefer_score = preferScore,
            base_top_length = baseLength,
            candidate_lengths = lengths,
            policy = baseLength >= 2 && baseLength <= 6 ? "candidate-convex" : "shared-additive",
            alphas = lengths.Select(InputMethodEngine.GetSentenceNeuralAlpha).ToArray(),
            additive_weight = InputMethodEngine.GetSentenceNeuralWeight(baseLength),
            tie_break = preferScore ? "score descending, lexicon rank ascending, text Ordinal" : "lexicon rank ascending, score descending, text Ordinal"
        };
    }

    public static async Task Run(string[] args)
    {
        // REPO WORK MODE LABEL MODEL CASES; MODE freeze|score|perf
        string repo = Path.GetFullPath(args[0]), work = Path.GetFullPath(args[1]), mode = args[2], label = args[3];
        bool resume = args.Skip(6).Contains("--resume");
        if (mode != "freeze" && mode != "score" && mode != "perf") throw new ArgumentException("Unknown evaluation mode");
        string model = Path.GetFullPath(args[4]), casesPath = Path.GetFullPath(args[5]);
        string daily = Path.Combine(repo, "release_arm64");
        if (work.StartsWith(daily, StringComparison.OrdinalIgnoreCase)) throw new Exception("Daily runtime is not an output directory");
        Directory.CreateDirectory(work);
        string runtime = Path.Combine(work, "runtime");
        if (!Directory.Exists(runtime)) throw new Exception("Prepare frozen runtime first");
        var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(casesPath));
        string output = Path.Combine(work, mode + "-" + label + ".jsonl");
        if (File.Exists(output) && !resume) throw new Exception("Refusing to overwrite completed/partial run: " + output);
        int alreadyCompleted = 0;
        if (File.Exists(output))
        {
            foreach (string line in File.ReadLines(output))
            {
                using var saved = JsonDocument.Parse(line);
                if (alreadyCompleted >= cases.Length || saved.RootElement.GetProperty("item").GetRawText() != Encode(cases[alreadyCompleted]))
                    throw new Exception("Resume requires an exact, complete-line prefix of the frozen case list");
                alreadyCompleted++;
            }
        }
        var state = new CoreRuntimeState(runtime); state.Initialize();
        void Set(string key, string value) { if (!state.TrySetConfigValue(key, value, out _, out string reason)) throw new Exception(reason); }
        Set("码表存储位置", Path.Combine(runtime, "码表")); Set("当前码表", "虎整句");
        Set("整句Tab自学习", "否"); Set("整句自动提前上屏", "否");
        Set("自动启用整句模式", "是"); Set("整句神经重排", "是");
        if (!state.ReloadLexicon()) throw new Exception("Lexicon load failed");
        using var engine = new InputMethodEngine(state, sentenceDecodeSynchronously: true);
        engine.ProcessKey(65, 0, "down", false, false, false, false, false, true, 1, false);
        var inputs = Directory.GetFiles(runtime, "*", SearchOption.AllDirectories).Order().Select(Identity).ToArray();
        var manifest = new
        {
            args, inputs, cases = Identity(casesPath), core = Identity(typeof(InputMethodEngine).Assembly.Location),
            tool = Identity(typeof(QuickReplacement).Assembly.Location), model = mode == "freeze" ? null : Identity(model),
            high_freq_limit = state.GetSentenceOptimalCodeHighFreqLimit(), duplicate_single = state.GetSentenceAllowDuplicateSingleCharacters(),
            learning = false, early_commit = false, context = "reset each case", policy = "production ApplySentenceNeuralScores; no tuning",
            created_utc = DateTime.UtcNow
        };
        string manifestPath = Path.Combine(work, mode + "-" + label + "-manifest.json");
        if (File.Exists(output))
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(manifestPath));
            using var current = JsonDocument.Parse(Encode(manifest));
            foreach (string key in new[] { "inputs", "cases", "core", "tool", "model", "high_freq_limit", "duplicate_single", "learning", "early_commit", "context", "policy" })
                if (previous.RootElement.GetProperty(key).GetRawText() != current.RootElement.GetProperty(key).GetRawText())
                    throw new Exception("Resume fingerprint mismatch: " + key);
            Save(Path.Combine(work, $"{mode}-{label}-resume-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json"), new { alreadyCompleted, manifest });
        }
        else Save(manifestPath, manifest);
        using var sink = new StreamWriter(output, resume, new UTF8Encoding(false)) { AutoFlush = true };
        var clock = Stopwatch.StartNew();
        bool StopRequested() => File.Exists(Path.Combine(work, "STOP"));
        if (mode == "freeze")
        {
            int frozenCount = alreadyCompleted;
            foreach (var item in cases.Skip(alreadyCompleted))
            {
                if (StopRequested()) break;
                string code = item.GetProperty("code").GetString();
                var candidates = Decode(engine, code);
                if (candidates.Any(c => c.LearningScore != 0)) throw new Exception("Learning leaked");
                sink.WriteLine(Encode(new { item, pool = Pool(candidates), ranking = Ranking(engine, candidates) }));
                frozenCount++;
                if (frozenCount % 100 == 0) Console.WriteLine($"freeze {label}: {frozenCount}/{cases.Length} elapsed_s={clock.Elapsed.TotalSeconds:F1}");
            }
            if (frozenCount == cases.Length) Save(Path.Combine(work, $"{mode}-{label}-complete.json"), new { rows = frozenCount, completed_utc = DateTime.UtcNow });
            Console.WriteLine($"FREEZE {(frozenCount == cases.Length ? "DONE" : "STOPPED")} {frozenCount}/{cases.Length}"); return;
        }
        var frozen = File.ReadLines(Path.Combine(work, "freeze-A.jsonl")).Select(x => JsonDocument.Parse(x).RootElement.Clone())
            .ToDictionary(x => x.GetProperty("item").GetProperty("id").GetString());
        string host = Path.Combine(work, "TigerClaw.QwenQuickProbe.exe");
        if (!File.Exists(host)) File.Copy(Path.Combine(daily, "sentence", "TigerClaw.Sentence.exe"), host);
        await using var service = await Service.Start(host, model, Path.Combine(work, mode + "-" + label));
        if (mode == "score")
        {
            string[] probes = { "陲机", "龙族", "虎娘", "其父", "今天的天气很好" };
            var batch = await service.Score(probes, "compatibility", 1);
            var repeat = await service.Score(probes, "compatibility", 2);
            double repeatError = batch.scores.Zip(repeat.scores, (a,b) => Math.Abs(a-b)).Max(), singleError = 0;
            for (int i = 0; i < probes.Length; i++)
                singleError = Math.Max(singleError, Math.Abs(batch.scores[i] - (await service.Score(new[] { probes[i] }, "single", 3+i)).scores[0]));
            var reversed = await service.Score(probes.Reverse().ToArray(), "reverse", 9);
            double orderError = batch.scores.Zip(reversed.scores.Reverse(), (a,b) => Math.Abs(a-b)).Max();
            await service.CancelAndReconnect(probes);
            Save(Path.Combine(work, "compatibility-" + label + ".json"), new { repeatError, singleError, orderError,
                tolerance = 0.001, pass = Math.Max(repeatError, Math.Max(singleError, orderError)) <= 0.001,
                disconnected_request_recovered = true, probes, scores = batch.scores });
            if (Math.Max(repeatError, Math.Max(singleError, orderError)) > 0.001) throw new Exception("Scoring parity tolerance exceeded");
        }
        else if (mode == "perf")
        {
            for (int i = 0; i < 10; i++)
            {
                var p = frozen[cases[i % cases.Length].GetProperty("id").GetString()].GetProperty("pool");
                await service.Score(p.EnumerateArray().Take(5).Select(c => c.GetProperty("text").GetString()).ToArray(), "warmup", i);
            }
        }
        int completed = alreadyCompleted;
        foreach (var item in cases.Skip(alreadyCompleted))
        {
            if (StopRequested()) break;
            string id = item.GetProperty("id").GetString(), code = item.GetProperty("code").GetString();
            var original = frozen[id].GetProperty("pool");
            var candidates = mode == "score" ? Decode(engine, code) : null;
            if (candidates != null && Encode(Pool(candidates)) != original.GetRawText()) throw new Exception("Frozen pool changed: " + id);
            object ranking = candidates == null ? null : Ranking(engine, candidates);
            if (ranking != null && frozen[id].TryGetProperty("ranking", out var frozenRanking) && Encode(ranking) != frozenRanking.GetRawText())
                throw new Exception("Frozen ranking policy changed: " + id);
            string[] texts = original.EnumerateArray().Take(5).Select(c => c.GetProperty("text").GetString()).ToArray();
            double[] scores = Array.Empty<double>(); double elapsed = 0; string error = null;
            object[] final = candidates == null ? null : Pool(candidates);
            bool? staleRejected = null;
            if (texts.Length > 1)
            {
                try
                {
                    long generation = candidates == null ? completed + 100 : engine.GetDifferentialSnapshot(20).SentenceGeneration;
                    var response = await service.Score(texts, code, generation);
                    scores = response.scores; elapsed = response.ms;
                    if (candidates != null)
                    {
                        bool prefer = (bool)Prefer.Invoke(engine, new object[] { candidates, texts.Length });
                        int baseLength = InputMethodEngine.GetSentenceNeuralBaseTopLength(candidates, texts.Length, prefer);
                        var expected = candidates.Take(texts.Length).Select((c,j) => new SentenceCandidate { Text = c.Text,
                            MaxLexiconRank = c.MaxLexiconRank, FinalScore = InputMethodEngine.CombineSentenceNeuralCandidateScore(c.BaseScore, scores[j], baseLength,
                                new StringInfo(c.Text).LengthInTextElements) }).ToArray();
                        Array.Sort(expected, Comparer<SentenceCandidate>.Create(prefer ? SentenceCandidate.CompareByScoreThenLexiconRank : SentenceCandidate.CompareByLexiconRankThenScore));
                        if (!engine.ApplySentenceNeuralScores(generation, code, scores)) throw new Exception("Production rejected scores");
                        for (int j = 0; j < expected.Length; j++)
                            if (expected[j].Text != candidates[j].Text || expected[j].FinalScore != candidates[j].FinalScore) throw new Exception("Ranking parity failed");
                        if (!candidates.Skip(texts.Length).Select(c => c.Text).SequenceEqual(original.EnumerateArray().Skip(texts.Length).Select(c => c.GetProperty("text").GetString())))
                            throw new Exception("Tail changed");
                        final = candidates.Select(c => (object)new { text = c.Text, score = c.BaseScore, fused = c.FinalScore }).ToArray();
                        // New generation must reject the actual old response, without changing its new pool.
                        if (completed < 10)
                        {
                            var next = Decode(engine, code + "a"); string before = Encode(Pool(next));
                            staleRejected = !engine.ApplySentenceNeuralScores(generation, code, scores) && before == Encode(Pool(next));
                            if (staleRejected != true) throw new Exception("Stale result accepted");
                        }
                    }
                }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException)
                {
                    error = ex.ToString(); final = null;
                    await service.Reconnect();
                }
            }
            service.Child.Refresh();
            sink.WriteLine(Encode(new { item, scores, ranking, elapsed_ms = elapsed, error, final, staleRejected,
                private_bytes = service.Child.PrivateMemorySize64, working_set = service.Child.WorkingSet64 }));
            completed++;
            if (completed % 100 == 0) Console.WriteLine($"{mode} {label}: {completed}/{cases.Length} elapsed_s={clock.Elapsed.TotalSeconds:F1}");
        }
        await service.Shutdown();
        if (completed == cases.Length) Save(Path.Combine(work, $"{mode}-{label}-complete.json"), new { rows = completed, completed_utc = DateTime.UtcNow });
        Console.WriteLine($"{(completed == cases.Length ? "DONE" : "STOPPED")} {mode} {label} {completed} elapsed_s={clock.Elapsed.TotalSeconds:F1}");
    }

    sealed class Service : IAsyncDisposable
    {
        public Process Child;
        string pipeName, logPrefix;
        NamedPipeClientStream pipe;
        StreamReader reader;
        StreamWriter writer;
        long seq;
        Task<string> stderr;
        double startupMs;
        bool stopped;
        public static async Task<Service> Start(string host, string model, string logPrefix)
        {
            var s = new Service { pipeName = "TigerClaw.QwenQuick." + Guid.NewGuid().ToString("N"), logPrefix = logPrefix };
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--pipe", s.pipeName, "--model", model, "--parent-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
            var clock = Stopwatch.StartNew(); s.Child = Process.Start(start); s.stderr = s.Child.StandardError.ReadToEndAsync();
            try { await s.Reconnect(); s.startupMs = clock.Elapsed.TotalMilliseconds; return s; }
            catch { await s.DisposeAsync(); throw; }
        }
        public async Task Reconnect()
        {
            try { writer?.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            reader?.Dispose(); pipe?.Dispose();
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(120000);
            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
            long id = ++seq;
            await writer.WriteLineAsync(Encode(new { type = "hello", seq = id }));
            using var doc = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)) ?? throw new IOException("Host closed"));
            if (!doc.RootElement.GetProperty("success").GetBoolean() || doc.RootElement.GetProperty("seq").GetInt64() != id) throw new IOException("Hello failed");
        }
        public async Task<(double[] scores, double ms)> Score(string[] candidates, string code, long generation)
        {
            long id = ++seq; var clock = Stopwatch.StartNew();
            await writer.WriteLineAsync(Encode(new { type = "rerank", seq = id, generation, raw_code = code, candidates }));
            string response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)) ?? throw new IOException("Host closed");
            double elapsed = clock.Elapsed.TotalMilliseconds;
            using var doc = JsonDocument.Parse(response); var r = doc.RootElement;
            if (!r.GetProperty("success").GetBoolean()) throw new IOException(response);
            if (r.GetProperty("seq").GetInt64() != id || r.GetProperty("generation").GetInt64() != generation || r.GetProperty("raw_code").GetString() != code)
                throw new Exception("Response identity mismatch");
            double[] scores = r.GetProperty("scores").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (scores.Length != candidates.Length || scores.Any(x => !double.IsFinite(x))) throw new IOException("Invalid scores");
            return (scores, elapsed);
        }
        public async Task CancelAndReconnect(string[] candidates)
        {
            for (int i = 0; i < 3; i++)
            {
                await writer.WriteLineAsync(Encode(new { type = "rerank", seq = ++seq, generation = 90000+i, raw_code = "cancel", candidates }));
                await Task.Delay(5); pipe.Dispose(); await Reconnect();
                await Score(candidates, "new-after-cancel", 91000+i);
            }
        }
        public async Task Shutdown()
        {
            long id = ++seq;
            await writer.WriteLineAsync(Encode(new { type = "shutdown", seq = id }));
            // The native host flushes its response before disconnecting. Drain
            // the acknowledgement so FlushFileBuffers cannot wait on us.
            using var ack = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("Missing shutdown reply"));
            if (!ack.RootElement.GetProperty("success").GetBoolean() || ack.RootElement.GetProperty("seq").GetInt64() != id)
                throw new IOException("Invalid shutdown acknowledgement");
            pipe.Dispose();
            await Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            stopped = true;
            Save(logPrefix + "-lifecycle.json", new { startup_ms = startupMs, normal_exit = true, exit_code = Child.ExitCode });
        }
        public async ValueTask DisposeAsync()
        {
            pipe?.Dispose();
            if (!Child.HasExited) Child.Kill();
            await Child.WaitForExitAsync(); File.WriteAllText(logPrefix + "-host.log", await stderr);
            if (!stopped) Save(logPrefix + "-lifecycle.json", new { startup_ms = startupMs, normal_exit = false, exit_code = Child.ExitCode });
            Child.Dispose();
        }
    }
}
