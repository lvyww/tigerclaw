using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TigerClaw.SentenceNgramTrainer;

internal static partial class Program
{
    internal const int Bos = 2;
    internal const int Eos = 3;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "build-model", StringComparison.OrdinalIgnoreCase))
            {
                return KneserNeyBuilder.Run(args[1..]);
            }
            Options options = Options.Parse(args);
            var trainer = new Trainer(options);
            await trainer.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误: " + ex.Message);
            return 1;
        }
    }

    internal static ulong PackPair(int first, int second)
    {
        return ((ulong)(uint)first << 21) | (uint)second;
    }

    internal static ulong PackTriple(int first, int second, int third)
    {
        return ((ulong)(uint)first << 42) |
               ((ulong)(uint)second << 21) |
               (uint)third;
    }

    internal static void UnpackPair(ulong key, out int first, out int second)
    {
        const ulong Mask = (1UL << 21) - 1;
        first = (int)((key >> 21) & Mask);
        second = (int)(key & Mask);
    }

    internal static void UnpackTriple(
        ulong key,
        out int first,
        out int second,
        out int third)
    {
        const ulong Mask = (1UL << 21) - 1;
        first = (int)((key >> 42) & Mask);
        second = (int)((key >> 21) & Mask);
        third = (int)(key & Mask);
    }

    [GeneratedRegex(@"<[^>]{1,1000}>", RegexOptions.Compiled)]
    internal static partial Regex TagRegex();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    internal static partial Regex UrlRegex();
}

internal sealed class Options
{
    public required string CorpusRoot { get; init; }
    public string? ArticlesRoot { get; init; }
    public required string Output { get; init; }
    public int Workers { get; init; } = 16;
    public int QueueCapacity { get; init; } = 4096;
    public int EntriesPerRun { get; init; } = 1_000_000;
    public int MergeFanIn { get; init; } = 64;
    public int MaximumRecordsPerDataset { get; init; }
    public int SampleModulus { get; init; } = 1;
    public int WikiHoldoutModulus { get; init; } = 100;
    public int MaximumFieldCharacters { get; init; } = 5000;
    public int MinimumBigramCount { get; init; } = 2;
    public int MinimumTrigramCount { get; init; } = 2;
    public bool KeepRuns { get; init; }

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("未知参数: " + argument);
            }

            if (string.Equals(argument, "--keep-runs", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(argument);
                continue;
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException("参数缺少值: " + argument);
            }
            values[argument] = args[index];
        }

        string corpusRoot = Required(values, "--corpus-root");
        string output = Required(values, "--output");
        var result = new Options
        {
            CorpusRoot = Path.GetFullPath(corpusRoot),
            ArticlesRoot = values.TryGetValue("--articles-root", out string? articlesRoot) &&
                !string.IsNullOrWhiteSpace(articlesRoot)
                    ? Path.GetFullPath(articlesRoot)
                    : null,
            Output = Path.GetFullPath(output),
            Workers = Integer(values, "--workers", 16),
            QueueCapacity = Integer(values, "--queue-capacity", 4096),
            EntriesPerRun = Integer(values, "--entries-per-run", 1_000_000),
            MergeFanIn = Integer(values, "--merge-fan-in", 64),
            MaximumRecordsPerDataset = Integer(values, "--max-records-per-dataset", 0),
            SampleModulus = Integer(values, "--sample-modulus", 1),
            WikiHoldoutModulus = Integer(values, "--wiki-holdout-modulus", 100),
            MaximumFieldCharacters = Integer(values, "--max-field-characters", 5000),
            MinimumBigramCount = Integer(values, "--min-bigram-count", 2),
            MinimumTrigramCount = Integer(values, "--min-trigram-count", 2),
            KeepRuns = flags.Contains("--keep-runs"),
        };
        if (result.Workers <= 0 || result.QueueCapacity <= 0 ||
            result.EntriesPerRun < 1000 || result.MergeFanIn < 2 ||
            result.MaximumRecordsPerDataset < 0 || result.SampleModulus <= 0 ||
            result.WikiHoldoutModulus <= 1 ||
            result.MaximumFieldCharacters <= 0 || result.MinimumBigramCount <= 0 ||
            result.MinimumTrigramCount <= 0)
        {
            throw new ArgumentException("数值参数超出允许范围。");
        }
        if (!Directory.Exists(result.CorpusRoot))
        {
            throw new DirectoryNotFoundException("语料目录不存在: " + result.CorpusRoot);
        }
        if (result.ArticlesRoot is not null && !Directory.Exists(result.ArticlesRoot))
        {
            throw new DirectoryNotFoundException("文章目录不存在: " + result.ArticlesRoot);
        }
        if (Directory.Exists(result.Output) && Directory.EnumerateFileSystemEntries(result.Output).Any())
        {
            throw new IOException("输出目录必须不存在或为空: " + result.Output);
        }
        return result;
    }

    private static string Required(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("缺少参数: " + name);
    }

    private static int Integer(Dictionary<string, string> values, string name, int fallback)
    {
        if (!values.TryGetValue(name, out string? raw))
        {
            return fallback;
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException("整数参数无效: " + name + "=" + raw);
    }
}

internal sealed record SourceSpec(
    string Name,
    int Weight,
    string[] Fields,
    IReadOnlyList<string> Paths,
    bool IsPlainText = false);

internal sealed record RecordLine(SourceSpec Source, string Content);

internal sealed class DatasetStats
{
    public long Records;
    public long SampledRecords;
    public long InvalidRecords;
    public long Fields;
    public long Sequences;
    public long Characters;
}

internal sealed class WorkerResult
{
    public required Dictionary<int, long> Unigrams { get; init; }
    public required List<string> BigramRuns { get; init; }
    public required List<string> TrigramRuns { get; init; }
}

internal sealed class Trainer
{
    private readonly Options _options;
    private readonly string _runsDirectory;
    private readonly string _countsDirectory;
    private readonly ConcurrentDictionary<string, DatasetStats> _statistics = new();
    private long _queuedRecords;

    public Trainer(Options options)
    {
        _options = options;
        _runsDirectory = Path.Combine(options.Output, "runs");
        _countsDirectory = Path.Combine(options.Output, "counts");
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_runsDirectory);
        Directory.CreateDirectory(_countsDirectory);
        IReadOnlyList<SourceSpec> sources = ResolveSources();
        long inputBytes = sources.SelectMany(source => source.Paths)
            .Sum(path => new FileInfo(path).Length);
        foreach (SourceSpec source in sources)
        {
            _statistics[source.Name] = new DatasetStats();
        }

        var channel = Channel.CreateBounded<RecordLine>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        Stopwatch stopwatch = Stopwatch.StartNew();
        Console.WriteLine(
            $"开始计数 workers={_options.Workers} entries_per_run={_options.EntriesPerRun:N0} " +
            $"sample_modulus={_options.SampleModulus}");

        Task<WorkerResult>[] consumers = Enumerable.Range(0, _options.Workers)
            .Select(worker => ConsumeAsync(worker, channel.Reader))
            .ToArray();
        Task[] producers = sources
            .Select(source => ProduceAsync(source, channel.Writer))
            .ToArray();
        Exception? producerError = null;
        try
        {
            await Task.WhenAll(producers);
        }
        catch (Exception ex)
        {
            producerError = ex;
        }
        channel.Writer.TryComplete(producerError);
        WorkerResult[] workerResults = await Task.WhenAll(consumers);
        if (producerError is not null)
        {
            throw producerError;
        }

        var unigrams = new Dictionary<int, long>();
        var bigramRuns = new List<string>();
        var trigramRuns = new List<string>();
        foreach (WorkerResult result in workerResults)
        {
            foreach ((int token, long count) in result.Unigrams)
            {
                unigrams[token] = unigrams.GetValueOrDefault(token) + count;
            }
            bigramRuns.AddRange(result.BigramRuns);
            trigramRuns.AddRange(result.TrigramRuns);
        }

        Console.WriteLine(
            $"计数运行段 bigram={bigramRuns.Count:N0} trigram={trigramRuns.Count:N0}，开始归并。");
        string bigramCounts = Path.Combine(_countsDirectory, "bigrams.bin");
        string trigramCounts = Path.Combine(_countsDirectory, "trigrams.bin");
        MergeResult bigramMerge = ExternalMerger.Merge(
            bigramRuns,
            bigramCounts,
            _options.MergeFanIn,
            _options.MinimumBigramCount,
            _runsDirectory,
            "bigram",
            _options.KeepRuns);
        MergeResult trigramMerge = ExternalMerger.Merge(
            trigramRuns,
            trigramCounts,
            _options.MergeFanIn,
            _options.MinimumTrigramCount,
            _runsDirectory,
            "trigram",
            _options.KeepRuns);
        WriteUnigrams(unigrams, Path.Combine(_countsDirectory, "unigrams.bin"));

        stopwatch.Stop();
        using Process process = Process.GetCurrentProcess();
        var metadata = new
        {
            version = 2,
            created_utc = DateTimeOffset.UtcNow,
            corpus_root = _options.CorpusRoot,
            articles_root = _options.ArticlesRoot,
            workers = _options.Workers,
            queue_capacity = _options.QueueCapacity,
            entries_per_run = _options.EntriesPerRun,
            merge_fan_in = _options.MergeFanIn,
            max_records_per_dataset = _options.MaximumRecordsPerDataset,
            sample_modulus = _options.SampleModulus,
            wiki_holdout_modulus = _options.WikiHoldoutModulus,
            maximum_field_characters = _options.MaximumFieldCharacters,
            minimum_bigram_count = _options.MinimumBigramCount,
            minimum_trigram_count = _options.MinimumTrigramCount,
            elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
            input_bytes = inputBytes,
            peak_working_set_bytes = process.PeakWorkingSet64,
            unigram_entries = unigrams.Count,
            bigram_entries = bigramMerge.Entries,
            trigram_entries = trigramMerge.Entries,
            bigram_total = bigramMerge.Total,
            trigram_total = trigramMerge.Total,
            sources = sources.ToDictionary(
                source => source.Name,
                source => new
                {
                    source.Weight,
                    records = _statistics[source.Name].Records,
                    sampled_records = _statistics[source.Name].SampledRecords,
                    invalid_records = _statistics[source.Name].InvalidRecords,
                    fields = _statistics[source.Name].Fields,
                    sequences = _statistics[source.Name].Sequences,
                    characters = _statistics[source.Name].Characters,
                }),
        };
        File.WriteAllText(
            Path.Combine(_options.Output, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        if (!_options.KeepRuns)
        {
            foreach (string path in Directory.EnumerateFiles(_runsDirectory))
            {
                File.Delete(path);
            }
            Directory.Delete(_runsDirectory);
        }
        Console.WriteLine(
            $"完成 elapsed={stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"unigram={unigrams.Count:N0} bigram={bigramMerge.Entries:N0} " +
            $"trigram={trigramMerge.Entries:N0}");
    }

    private IReadOnlyList<SourceSpec> ResolveSources()
    {
        string root = _options.CorpusRoot;
        string wikiRoot = Path.Combine(root, "wiki_zh_2019");
        string[] wikiPaths = Directory.Exists(wikiRoot)
            ? Directory.EnumerateFiles(wikiRoot, "wiki_*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];
        var sources = new List<SourceSpec>
        {
            new SourceSpec("baike", 1, ["title", "desc", "answer"],
                [Path.Combine(root, "baike2018qa", "baike_qa_train.json")]),
            new SourceSpec("news", 1, ["title", "desc", "content"],
                [Path.Combine(root, "new2016zh", "news2016zh_train.json")]),
            new SourceSpec("webtext", 2, ["title", "desc", "content"],
                [Path.Combine(root, "webtext2019zh", "web_text_zh_train.json")]),
            new SourceSpec("wiki", 1, ["title", "text"], wikiPaths),
        };
        if (_options.ArticlesRoot is not null)
        {
            string[] articlePaths = Directory
                .EnumerateFiles(_options.ArticlesRoot, "*.txt", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            sources.Add(new SourceSpec("articles", 1, [], articlePaths, IsPlainText: true));
        }
        foreach (SourceSpec source in sources)
        {
            if (source.Paths.Count == 0 || source.Paths.Any(path => !File.Exists(path)))
            {
                throw new FileNotFoundException("缺少语料文件: " + source.Name);
            }
        }
        return sources;
    }

    private async Task ProduceAsync(SourceSpec source, ChannelWriter<RecordLine> writer)
    {
        DatasetStats stats = _statistics[source.Name];
        long sourceRecords = 0;
        foreach (string path in source.Paths)
        {
            if (source.IsPlainText)
            {
                if (_options.MaximumRecordsPerDataset > 0 &&
                    sourceRecords >= _options.MaximumRecordsPerDataset)
                {
                    return;
                }
                sourceRecords++;
                Interlocked.Increment(ref stats.Records);
                if ((sourceRecords - 1) % _options.SampleModulus != 0)
                {
                    continue;
                }
                Interlocked.Increment(ref stats.SampledRecords);
                string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
                await writer.WriteAsync(new RecordLine(source, text));
                ReportQueuedRecord();
                continue;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 1 << 20);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (_options.MaximumRecordsPerDataset > 0 &&
                    sourceRecords >= _options.MaximumRecordsPerDataset)
                {
                    return;
                }
                sourceRecords++;
                Interlocked.Increment(ref stats.Records);
                if ((sourceRecords - 1) % _options.SampleModulus != 0)
                {
                    continue;
                }
                if (source.Name == "wiki" &&
                    (sourceRecords - 1) % _options.WikiHoldoutModulus ==
                    _options.WikiHoldoutModulus - 1)
                {
                    continue;
                }
                Interlocked.Increment(ref stats.SampledRecords);
                await writer.WriteAsync(new RecordLine(source, line));
                ReportQueuedRecord();
            }
        }
    }

    private void ReportQueuedRecord()
    {
        long queued = Interlocked.Increment(ref _queuedRecords);
        if (queued % 100_000 == 0)
        {
            Console.WriteLine($"queued={queued:N0}");
        }
    }

    private async Task<WorkerResult> ConsumeAsync(
        int worker,
        ChannelReader<RecordLine> reader)
    {
        var counter = new LocalCounter(worker, _options.EntriesPerRun, _runsDirectory);
        await foreach (RecordLine record in reader.ReadAllAsync())
        {
            ProcessRecord(record, counter);
        }
        counter.Flush();
        return counter.Result;
    }

    private void ProcessRecord(RecordLine record, LocalCounter counter)
    {
        DatasetStats stats = _statistics[record.Source.Name];
        if (record.Source.IsPlainText)
        {
            ProcessField(record.Content, record.Source.Weight, stats, counter, truncate: false);
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(record.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Interlocked.Increment(ref stats.InvalidRecords);
                return;
            }
            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (string field in record.Source.Fields)
            {
                if (!document.RootElement.TryGetProperty(field, out JsonElement value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string text = value.GetString() ?? string.Empty;
                string normalizedField = text.Trim();
                if (normalizedField.Length == 0 || !seenFields.Add(normalizedField))
                {
                    continue;
                }
                ProcessField(text, record.Source.Weight, stats, counter, truncate: true);
            }
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref stats.InvalidRecords);
        }
    }

    private void ProcessField(
        string text,
        int weight,
        DatasetStats stats,
        LocalCounter counter,
        bool truncate)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Interlocked.Increment(ref stats.Fields);
        foreach (int[] sequence in NormalizeSequences(text, truncate))
        {
            counter.AddSequence(sequence, weight);
            Interlocked.Increment(ref stats.Sequences);
            Interlocked.Add(ref stats.Characters, sequence.Length);
        }
    }

    private IEnumerable<int[]> NormalizeSequences(string input, bool truncate)
    {
        string text = truncate && input.Length > _options.MaximumFieldCharacters
            ? input[.._options.MaximumFieldCharacters]
            : input;
        text = WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormKC);
        text = Program.TagRegex().Replace(text, " ");
        text = Program.UrlRegex().Replace(text, " ");
        var values = new List<int>();
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            bool isHan = value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF;
            if (isHan)
            {
                values.Add(value);
            }
            else if (values.Count > 0)
            {
                if (values.Count >= 2)
                {
                    yield return values.ToArray();
                }
                values.Clear();
            }
        }
        if (values.Count >= 2)
        {
            yield return values.ToArray();
        }
    }

    private static void WriteUnigrams(Dictionary<int, long> counts, string path)
    {
        KeyValuePair<int, long>[] entries = counts.OrderBy(value => value.Key).ToArray();
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8, false);
        writer.Write(entries.Length);
        foreach ((int token, long count) in entries)
        {
            writer.Write(token);
            writer.Write(count);
        }
    }
}

internal sealed class LocalCounter
{
    private readonly int _worker;
    private readonly int _entriesPerRun;
    private readonly string _directory;
    private readonly Dictionary<int, long> _unigrams = [];
    private readonly Dictionary<ulong, int> _bigrams = [];
    private readonly Dictionary<ulong, int> _trigrams = [];
    private readonly List<string> _bigramRuns = [];
    private readonly List<string> _trigramRuns = [];
    private int _run;

    public LocalCounter(int worker, int entriesPerRun, string directory)
    {
        _worker = worker;
        _entriesPerRun = entriesPerRun;
        _directory = directory;
    }

    public WorkerResult Result => new()
    {
        Unigrams = _unigrams,
        BigramRuns = _bigramRuns,
        TrigramRuns = _trigramRuns,
    };

    public void AddSequence(int[] sequence, int weight)
    {
        int previous2 = Program.Bos;
        int previous1 = Program.Bos;
        foreach (int target in sequence.Append(Program.Eos))
        {
            _unigrams[target] = _unigrams.GetValueOrDefault(target) + weight;
            Add(_bigrams, Program.PackPair(previous1, target), weight);
            Add(_trigrams, Program.PackTriple(previous2, previous1, target), weight);
            previous2 = previous1;
            previous1 = target;
        }
        if (_bigrams.Count + _trigrams.Count >= _entriesPerRun)
        {
            Flush();
        }
    }

    public void Flush()
    {
        if (_bigrams.Count == 0 && _trigrams.Count == 0)
        {
            return;
        }
        int run = _run++;
        if (_bigrams.Count > 0)
        {
            string path = Path.Combine(_directory, $"bigram-w{_worker:D2}-r{run:D5}.run");
            RunFile.Write(path, _bigrams);
            _bigramRuns.Add(path);
            _bigrams.Clear();
        }
        if (_trigrams.Count > 0)
        {
            string path = Path.Combine(_directory, $"trigram-w{_worker:D2}-r{run:D5}.run");
            RunFile.Write(path, _trigrams);
            _trigramRuns.Add(path);
            _trigrams.Clear();
        }
    }

    private static void Add(Dictionary<ulong, int> counts, ulong key, int weight)
    {
        counts[key] = checked(counts.GetValueOrDefault(key) + weight);
    }
}

internal static class RunFile
{
    public static void Write(string path, Dictionary<ulong, int> counts)
    {
        KeyValuePair<ulong, int>[] entries = counts.ToArray();
        Array.Sort(entries, static (left, right) => left.Key.CompareTo(right.Key));
        using var writer = new BinaryWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20),
            Encoding.UTF8,
            false);
        writer.Write(entries.Length);
        foreach ((ulong key, int count) in entries)
        {
            writer.Write(key);
            writer.Write(count);
        }
    }
}

internal sealed class RunReader : IDisposable
{
    private readonly BinaryReader _reader;
    private int _remaining;

    public RunReader(string path)
    {
        _reader = new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
            Encoding.UTF8,
            false);
        _remaining = _reader.ReadInt32();
        MoveNext();
    }

    public ulong Key { get; private set; }
    public int Count { get; private set; }
    public bool HasValue { get; private set; }

    public void MoveNext()
    {
        if (_remaining <= 0)
        {
            HasValue = false;
            return;
        }
        Key = _reader.ReadUInt64();
        Count = _reader.ReadInt32();
        _remaining--;
        HasValue = true;
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}

internal sealed record MergeResult(long Entries, long Total);

internal static class ExternalMerger
{
    public static MergeResult Merge(
        IReadOnlyList<string> input,
        string output,
        int fanIn,
        int minimumCount,
        string workDirectory,
        string prefix,
        bool keepInputRuns)
    {
        if (input.Count == 0)
        {
            using var empty = new BinaryWriter(File.Create(output));
            empty.Write((long)0);
            return new MergeResult(0, 0);
        }

        var current = input.ToList();
        var preserved = keepInputRuns
            ? new HashSet<string>(input, StringComparer.OrdinalIgnoreCase)
            : [];
        int pass = 0;
        while (current.Count > fanIn)
        {
            var next = new List<string>();
            for (int start = 0; start < current.Count; start += fanIn)
            {
                string path = Path.Combine(
                    workDirectory,
                    $"{prefix}-merge-p{pass:D2}-r{next.Count:D5}.run");
                MergeBatch(current.Skip(start).Take(fanIn).ToArray(), path, 1, false);
                next.Add(path);
            }
            foreach (string path in current)
            {
                if (!preserved.Contains(path))
                {
                    File.Delete(path);
                }
            }
            current = next;
            pass++;
        }
        MergeResult result = MergeBatch(current, output, minimumCount, true);
        foreach (string path in current)
        {
            if (!preserved.Contains(path))
            {
                File.Delete(path);
            }
        }
        return result;
    }

    private static MergeResult MergeBatch(
        IReadOnlyList<string> paths,
        string output,
        int minimumCount,
        bool useLongHeader)
    {
        var readers = paths.Select(path => new RunReader(path)).ToArray();
        try
        {
            var queue = new PriorityQueue<int, ulong>();
            for (int index = 0; index < readers.Length; index++)
            {
                if (readers[index].HasValue)
                {
                    queue.Enqueue(index, readers[index].Key);
                }
            }
            using var stream = new FileStream(
                output,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 20);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
            long headerPosition = stream.Position;
            if (useLongHeader)
            {
                writer.Write((long)0);
            }
            else
            {
                writer.Write(0);
            }
            long entries = 0;
            long total = 0;
            while (queue.Count > 0)
            {
                queue.TryDequeue(out int readerIndex, out ulong key);
                long count = 0;
                ConsumeReader(readers, queue, readerIndex, key, ref count);
                while (queue.TryPeek(out int sameIndex, out ulong sameKey) && sameKey == key)
                {
                    queue.Dequeue();
                    ConsumeReader(readers, queue, sameIndex, key, ref count);
                }
                if (count < minimumCount)
                {
                    continue;
                }
                writer.Write(key);
                writer.Write(checked((int)Math.Min(count, int.MaxValue)));
                entries++;
                total += count;
            }
            writer.Flush();
            stream.Position = headerPosition;
            if (useLongHeader)
            {
                writer.Write(entries);
            }
            else
            {
                writer.Write(checked((int)entries));
            }
            return new MergeResult(entries, total);
        }
        finally
        {
            foreach (RunReader reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void ConsumeReader(
        RunReader[] readers,
        PriorityQueue<int, ulong> queue,
        int index,
        ulong expectedKey,
        ref long count)
    {
        RunReader reader = readers[index];
        if (reader.Key != expectedKey)
        {
            throw new InvalidDataException("归并队列键不一致。");
        }
        count += reader.Count;
        reader.MoveNext();
        if (reader.HasValue)
        {
            queue.Enqueue(index, reader.Key);
        }
    }
}
