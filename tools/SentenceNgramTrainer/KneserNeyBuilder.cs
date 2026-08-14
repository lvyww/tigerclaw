using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TigerClaw.SentenceNgramTrainer;

internal sealed class KneserNeyOptions
{
    public required string CountsDirectory { get; init; }
    public required string Output { get; init; }
    public int MinimumBigramExportCount { get; init; } = 2;
    public int MinimumTrigramExportCount { get; init; } = 2;

    public static KneserNeyOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("build-model参数必须使用 --name value 格式。");
            }
            values[args[index]] = args[index + 1];
        }
        string counts = Required(values, "--counts");
        string output = Required(values, "--output");
        var options = new KneserNeyOptions
        {
            CountsDirectory = Path.GetFullPath(counts),
            Output = Path.GetFullPath(output),
            MinimumBigramExportCount = Integer(values, "--min-bigram-export-count", 2),
            MinimumTrigramExportCount = Integer(values, "--min-trigram-export-count", 2),
        };
        if (!Directory.Exists(options.CountsDirectory))
        {
            throw new DirectoryNotFoundException("计数目录不存在: " + options.CountsDirectory);
        }
        if (options.MinimumBigramExportCount <= 0 || options.MinimumTrigramExportCount <= 0)
        {
            throw new ArgumentException("导出计数阈值必须为正数。");
        }
        if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any())
        {
            throw new IOException("模型输出目录必须不存在或为空: " + options.Output);
        }
        return options;
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
            : throw new ArgumentException("整数参数无效: " + name);
    }
}

internal readonly record struct CountOfCounts(long One, long Two, long Three, long Four)
{
    public CountOfCounts Add(int count)
    {
        return count switch
        {
            1 => this with { One = One + 1 },
            2 => this with { Two = Two + 1 },
            3 => this with { Three = Three + 1 },
            4 => this with { Four = Four + 1 },
            _ => this,
        };
    }
}

internal readonly record struct Discounts(double One, double Two, double ThreePlus)
{
    public double ForCount(int count)
    {
        return count <= 1 ? One : count == 2 ? Two : ThreePlus;
    }

    public static Discounts Estimate(CountOfCounts counts)
    {
        if (counts.One <= 0 || counts.Two <= 0)
        {
            return new Discounts(0.75, 0.75, 0.75);
        }
        double y = counts.One / (double)(counts.One + 2 * counts.Two);
        double d1 = 1 - 2 * y * counts.Two / counts.One;
        double d2 = counts.Three > 0
            ? 2 - 3 * y * counts.Three / counts.Two
            : d1;
        double d3 = counts.Three > 0 && counts.Four > 0
            ? 3 - 4 * y * counts.Four / counts.Three
            : d2;
        return new Discounts(
            Math.Clamp(d1, 0.01, 0.99),
            Math.Clamp(d2, 0.01, 1.99),
            Math.Clamp(d3, 0.01, 2.99));
    }
}

internal struct ContextAccumulator
{
    public ulong Key;
    public long Total;
    public double ExportedNumerator;

    public void Add(int count, int minimumExportCount, Discounts discounts)
    {
        Total += count;
        if (count >= minimumExportCount)
        {
            ExportedNumerator += count - discounts.ForCount(count);
        }
    }

    public double Lambda => Math.Clamp(1 - ExportedNumerator / Total, 0, 1);
}

internal static class KneserNeyBuilder
{
    private const string Magic = "TCSKNM01";
    private const int Mask = (1 << 21) - 1;

    public static int Run(string[] args)
    {
        KneserNeyOptions options = KneserNeyOptions.Parse(args);
        Directory.CreateDirectory(options.Output);
        string unigramsPath = Path.Combine(options.CountsDirectory, "unigrams.bin");
        string trigramsPath = Path.Combine(options.CountsDirectory, "trigrams.bin");
        if (!File.Exists(unigramsPath) || !File.Exists(trigramsPath))
        {
            throw new FileNotFoundException("计数目录缺少unigrams.bin或trigrams.bin。");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string rawContextsPath = Path.Combine(options.Output, "trigram-contexts.raw");
        (long trigramEntries, long trigramExportEntries, CountOfCounts trigramCounts) =
            ReadCountStatistics(trigramsPath, options.MinimumTrigramExportCount);
        Discounts trigramDiscounts = Discounts.Estimate(trigramCounts);
        var continuationBigrams = new Dictionary<ulong, int>(8_000_000);
        long scannedTrigramEntries = ScanTrigrams(
            trigramsPath,
            rawContextsPath,
            continuationBigrams,
            options.MinimumTrigramExportCount,
            trigramDiscounts);
        if (scannedTrigramEntries != trigramEntries)
        {
            throw new InvalidDataException("三元计数两次扫描的条目数不一致。");
        }
        Console.WriteLine(
            $"三元扫描 entries={trigramEntries:N0} export={trigramExportEntries:N0} " +
            $"continuation_bigrams={continuationBigrams.Count:N0} " +
            $"D=({trigramDiscounts.One:F4},{trigramDiscounts.Two:F4},{trigramDiscounts.ThreePlus:F4})");

        KeyValuePair<ulong, int>[] bigrams = continuationBigrams.ToArray();
        continuationBigrams.Clear();
        continuationBigrams.TrimExcess();
        Array.Sort(bigrams, static (left, right) => left.Key.CompareTo(right.Key));
        CountOfCounts bigramCounts = default;
        long bigramExportEntries = 0;
        var unigramContinuations = new Dictionary<int, long>();
        foreach ((ulong key, int count) in bigrams)
        {
            bigramCounts = bigramCounts.Add(count);
            if (count >= options.MinimumBigramExportCount)
            {
                bigramExportEntries++;
            }
            int target = (int)(key & Mask);
            unigramContinuations[target] = unigramContinuations.GetValueOrDefault(target) + 1;
        }
        Discounts bigramDiscounts = Discounts.Estimate(bigramCounts);
        Console.WriteLine(
            $"续接二元 entries={bigrams.Length:N0} export={bigramExportEntries:N0} " +
            $"D=({bigramDiscounts.One:F4},{bigramDiscounts.Two:F4},{bigramDiscounts.ThreePlus:F4})");

        string modelPath = Path.Combine(options.Output, "sentence-ngram-v2.bin");
        using (var stream = new FileStream(modelPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
        {
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(1);
            WriteUnigramProbabilities(writer, unigramsPath, unigramContinuations, bigrams.LongLength);
            WriteBigramProbabilities(
                writer,
                bigrams,
                bigramExportEntries,
                options.MinimumBigramExportCount,
                bigramDiscounts);
            WriteTrigramProbabilities(
                writer,
                trigramsPath,
                rawContextsPath,
                trigramExportEntries,
                options.MinimumTrigramExportCount,
                trigramDiscounts);
        }
        File.Delete(rawContextsPath);
        stopwatch.Stop();
        using Process process = Process.GetCurrentProcess();
        var metadata = new
        {
            version = 1,
            kind = "modified-kneser-ney-character-trigram",
            counts = options.CountsDirectory,
            elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
            peak_working_set_bytes = process.PeakWorkingSet64,
            minimum_bigram_export_count = options.MinimumBigramExportCount,
            minimum_trigram_export_count = options.MinimumTrigramExportCount,
            trigram_entries = trigramEntries,
            trigram_export_entries = trigramExportEntries,
            continuation_bigram_entries = bigrams.LongLength,
            bigram_export_entries = bigramExportEntries,
            trigram_count_of_counts = trigramCounts,
            bigram_count_of_counts = bigramCounts,
            trigram_discounts = trigramDiscounts,
            bigram_discounts = bigramDiscounts,
            model_bytes = new FileInfo(modelPath).Length,
        };
        File.WriteAllText(
            Path.Combine(options.Output, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Console.WriteLine(
            $"模型完成 elapsed={stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"size={new FileInfo(modelPath).Length / 1024.0 / 1024.0:F1}MiB");
        return 0;
    }

    private static long ScanTrigrams(
        string path,
        string contextsPath,
        Dictionary<ulong, int> continuationBigrams,
        int minimumExportCount,
        Discounts discounts)
    {
        using var reader = new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
            Encoding.UTF8,
            false);
        using var contextWriter = new BinaryWriter(
            new FileStream(contextsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20),
            Encoding.UTF8,
            false);
        long entries = reader.ReadInt64();
        contextWriter.Write((long)0);
        long contextEntries = 0;
        ContextAccumulator context = default;
        bool hasContext = false;
        for (long index = 0; index < entries; index++)
        {
            ulong key = reader.ReadUInt64();
            int count = reader.ReadInt32();
            ulong contextKey = key >> 21;
            if (!hasContext || context.Key != contextKey)
            {
                if (hasContext)
                {
                    WriteContext(contextWriter, context);
                    contextEntries++;
                }
                context = new ContextAccumulator { Key = contextKey };
                hasContext = true;
            }
            context.Add(count, minimumExportCount, discounts);
            int second = (int)((key >> 21) & Mask);
            int third = (int)(key & Mask);
            ulong continuationKey = Program.PackPair(second, third);
            continuationBigrams[continuationKey] =
                continuationBigrams.GetValueOrDefault(continuationKey) + 1;
        }
        if (hasContext)
        {
            WriteContext(contextWriter, context);
            contextEntries++;
        }
        contextWriter.BaseStream.Position = 0;
        contextWriter.Write(contextEntries);
        return entries;
    }

    private static void WriteContext(BinaryWriter writer, ContextAccumulator context)
    {
        writer.Write(context.Key);
        writer.Write(context.Total);
        writer.Write((float)context.Lambda);
    }

    private static void WriteUnigramProbabilities(
        BinaryWriter writer,
        string unigramsPath,
        Dictionary<int, long> continuations,
        long continuationTotal)
    {
        using var reader = new BinaryReader(File.OpenRead(unigramsPath), Encoding.UTF8, false);
        int rawCount = reader.ReadInt32();
        var tokens = new HashSet<int> { 0 };
        for (int index = 0; index < rawCount; index++)
        {
            tokens.Add(reader.ReadInt32());
            reader.ReadInt64();
        }
        int[] ordered = tokens.Order().ToArray();
        writer.Write(ordered.Length);
        double denominator = continuationTotal + 0.1;
        foreach (int token in ordered)
        {
            double numerator = continuations.GetValueOrDefault(token);
            if (token == 0)
            {
                numerator = 0.1;
            }
            writer.Write(token);
            writer.Write((float)(numerator / denominator));
        }
    }

    private static void WriteBigramProbabilities(
        BinaryWriter writer,
        KeyValuePair<ulong, int>[] entries,
        long exportEntries,
        int minimumExportCount,
        Discounts discounts)
    {
        writer.Write(exportEntries);
        var contexts = new List<(int Key, float Lambda)>();
        int position = 0;
        while (position < entries.Length)
        {
            int context = (int)(entries[position].Key >> 21);
            int end = position;
            long total = 0;
            double exportedNumerator = 0;
            while (end < entries.Length && (int)(entries[end].Key >> 21) == context)
            {
                int count = entries[end].Value;
                total += count;
                if (count >= minimumExportCount)
                {
                    exportedNumerator += count - discounts.ForCount(count);
                }
                end++;
            }
            double lambda = Math.Clamp(1 - exportedNumerator / total, 0, 1);
            contexts.Add((context, (float)lambda));
            for (int index = position; index < end; index++)
            {
                (ulong key, int count) = entries[index];
                if (count < minimumExportCount) continue;
                writer.Write(key);
                writer.Write((float)((count - discounts.ForCount(count)) / total));
            }
            position = end;
        }
        writer.Write(contexts.Count);
        foreach ((int key, float lambda) in contexts)
        {
            writer.Write(key);
            writer.Write(lambda);
        }
    }

    private static void WriteTrigramProbabilities(
        BinaryWriter writer,
        string trigramsPath,
        string contextsPath,
        long exportEntries,
        int minimumExportCount,
        Discounts discounts)
    {
        writer.Write(exportEntries);
        using (var reader = new BinaryReader(File.OpenRead(trigramsPath), Encoding.UTF8, false))
        using (var contexts = new BinaryReader(File.OpenRead(contextsPath), Encoding.UTF8, false))
        {
            long entries = reader.ReadInt64();
            long contextCount = contexts.ReadInt64();
            ContextAccumulator context = ReadContext(contexts);
            long contextIndex = 0;
            for (long index = 0; index < entries; index++)
            {
                ulong key = reader.ReadUInt64();
                int count = reader.ReadInt32();
                ulong contextKey = key >> 21;
                while (context.Key != contextKey && ++contextIndex < contextCount)
                {
                    context = ReadContext(contexts);
                }
                if (context.Key != contextKey)
                {
                    throw new InvalidDataException("三元上下文统计不匹配。");
                }
                if (count < minimumExportCount) continue;
                writer.Write(key);
                writer.Write((float)((count - discounts.ForCount(count)) / context.Total));
            }
        }
        using var contextReader = new BinaryReader(File.OpenRead(contextsPath), Encoding.UTF8, false);
        long contextsToWrite = contextReader.ReadInt64();
        writer.Write(contextsToWrite);
        for (long index = 0; index < contextsToWrite; index++)
        {
            ContextAccumulator context = ReadContext(contextReader);
            writer.Write(context.Key);
            writer.Write((float)context.ExportedNumerator);
        }
    }

    private static ContextAccumulator ReadContext(BinaryReader reader)
    {
        return new ContextAccumulator
        {
            Key = reader.ReadUInt64(),
            Total = reader.ReadInt64(),
            ExportedNumerator = reader.ReadSingle(),
        };
    }

    private static (long Entries, long ExportEntries, CountOfCounts Counts)
        ReadCountStatistics(string path, int minimumExportCount)
    {
        using var reader = new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
            Encoding.UTF8,
            false);
        long entries = reader.ReadInt64();
        long exportEntries = 0;
        CountOfCounts counts = default;
        for (long index = 0; index < entries; index++)
        {
            reader.ReadUInt64();
            int count = reader.ReadInt32();
            counts = counts.Add(count);
            if (count >= minimumExportCount)
            {
                exportEntries++;
            }
        }
        return (entries, exportEntries, counts);
    }
}
