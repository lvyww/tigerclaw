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
    public int RescueMinimumBigramCount { get; init; }
    public int RescueMinimumTrigramCount { get; init; }
    public double RescueMinimumConditionalProbability { get; init; } = 1.0;
    public double RescueProbabilityWeight { get; init; } = 1.0;

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
            RescueMinimumBigramCount = Integer(values, "--rescue-min-bigram-count", 0),
            RescueMinimumTrigramCount = Integer(values, "--rescue-min-trigram-count", 0),
            RescueMinimumConditionalProbability = Double(
                values,
                "--rescue-min-conditional-probability",
                1.0),
            RescueProbabilityWeight = Double(values, "--rescue-probability-weight", 1.0),
        };
        if (!Directory.Exists(options.CountsDirectory))
        {
            throw new DirectoryNotFoundException("计数目录不存在: " + options.CountsDirectory);
        }
        if (options.MinimumBigramExportCount <= 0 || options.MinimumTrigramExportCount <= 0)
        {
            throw new ArgumentException("导出计数阈值必须为正数。");
        }
        if (options.RescueMinimumBigramCount < 0 || options.RescueMinimumTrigramCount < 0 ||
            options.RescueMinimumBigramCount > options.MinimumBigramExportCount ||
            options.RescueMinimumTrigramCount > options.MinimumTrigramExportCount)
        {
            throw new ArgumentException("条件救回计数必须为0或不大于对应导出阈值。");
        }
        if (options.RescueMinimumConditionalProbability <= 0.0 ||
            options.RescueMinimumConditionalProbability > 1.0)
        {
            throw new ArgumentException("条件救回概率必须在(0, 1]范围内。");
        }
        if (options.RescueProbabilityWeight <= 0.0 || options.RescueProbabilityWeight > 1.0)
        {
            throw new ArgumentException("条件救回概率权重必须在(0, 1]范围内。");
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

    private static double Double(Dictionary<string, string> values, string name, double fallback)
    {
        if (!values.TryGetValue(name, out string? raw))
        {
            return fallback;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException("浮点参数无效: " + name);
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
    public long ExportedEntries;

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
        (long trigramEntries, CountOfCounts trigramCounts) = ReadCountStatistics(trigramsPath);
        Discounts trigramDiscounts = Discounts.Estimate(trigramCounts);
        var continuationBigrams = new Dictionary<ulong, int>(8_000_000);
        (long scannedTrigramEntries, long trigramExportEntries) = ScanTrigrams(
            trigramsPath,
            rawContextsPath,
            continuationBigrams,
            options.MinimumTrigramExportCount,
            options.RescueMinimumTrigramCount,
            options.RescueMinimumConditionalProbability,
            options.RescueProbabilityWeight,
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
        var unigramContinuations = new Dictionary<int, long>();
        foreach ((ulong key, int count) in bigrams)
        {
            bigramCounts = bigramCounts.Add(count);
            int target = (int)(key & Mask);
            unigramContinuations[target] = unigramContinuations.GetValueOrDefault(target) + 1;
        }
        Discounts bigramDiscounts = Discounts.Estimate(bigramCounts);
        long bigramExportEntries = CountBigramExports(
            bigrams,
            options.MinimumBigramExportCount,
            options.RescueMinimumBigramCount,
            options.RescueMinimumConditionalProbability);
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
                options.RescueMinimumBigramCount,
                options.RescueMinimumConditionalProbability,
                options.RescueProbabilityWeight,
                bigramDiscounts);
            WriteTrigramProbabilities(
                writer,
                trigramsPath,
                rawContextsPath,
                trigramExportEntries,
                options.MinimumTrigramExportCount,
                options.RescueMinimumTrigramCount,
                options.RescueMinimumConditionalProbability,
                options.RescueProbabilityWeight,
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
            rescue_minimum_bigram_count = options.RescueMinimumBigramCount,
            rescue_minimum_trigram_count = options.RescueMinimumTrigramCount,
            rescue_minimum_conditional_probability = options.RescueMinimumConditionalProbability,
            rescue_probability_weight = options.RescueProbabilityWeight,
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

    private static (long Entries, long ExportEntries) ScanTrigrams(
        string path,
        string contextsPath,
        Dictionary<ulong, int> continuationBigrams,
        int minimumExportCount,
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability,
        double rescueProbabilityWeight,
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
        long exportEntries = 0;
        ContextAccumulator context = default;
        var rescueCounts = new List<int>();
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
                    CompleteContext(
                        ref context,
                        rescueCounts,
                        rescueMinimumConditionalProbability,
                        rescueProbabilityWeight,
                        discounts);
                    WriteContext(contextWriter, context);
                    exportEntries += context.ExportedEntries;
                    contextEntries++;
                }
                context = new ContextAccumulator { Key = contextKey };
                rescueCounts.Clear();
                hasContext = true;
            }
            context.Total += count;
            if (count >= minimumExportCount)
            {
                context.ExportedNumerator += count - discounts.ForCount(count);
                context.ExportedEntries++;
            }
            else if (rescueMinimumCount > 0 && count >= rescueMinimumCount)
            {
                rescueCounts.Add(count);
            }
            int second = (int)((key >> 21) & Mask);
            int third = (int)(key & Mask);
            ulong continuationKey = Program.PackPair(second, third);
            continuationBigrams[continuationKey] =
                continuationBigrams.GetValueOrDefault(continuationKey) + 1;
        }
        if (hasContext)
        {
            CompleteContext(
                ref context,
                rescueCounts,
                rescueMinimumConditionalProbability,
                rescueProbabilityWeight,
                discounts);
            WriteContext(contextWriter, context);
            exportEntries += context.ExportedEntries;
            contextEntries++;
        }
        contextWriter.BaseStream.Position = 0;
        contextWriter.Write(contextEntries);
        return (entries, exportEntries);
    }

    private static void CompleteContext(
        ref ContextAccumulator context,
        List<int> rescueCounts,
        double minimumConditionalProbability,
        double rescueProbabilityWeight,
        Discounts discounts)
    {
        foreach (int count in rescueCounts)
        {
            if (count / (double)context.Total < minimumConditionalProbability)
            {
                continue;
            }
            context.ExportedNumerator +=
                rescueProbabilityWeight * (count - discounts.ForCount(count));
            context.ExportedEntries++;
        }
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
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability,
        double rescueProbabilityWeight,
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
            while (end < entries.Length && (int)(entries[end].Key >> 21) == context)
            {
                total += entries[end].Value;
                end++;
            }
            double exportedNumerator = 0;
            for (int index = position; index < end; index++)
            {
                int count = entries[index].Value;
                if (ShouldExport(
                    count,
                    total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability))
                {
                    exportedNumerator += ExportedNumerator(
                        count,
                        total,
                        minimumExportCount,
                        rescueMinimumCount,
                        rescueMinimumConditionalProbability,
                        rescueProbabilityWeight,
                        discounts);
                }
            }
            double lambda = Math.Clamp(1 - exportedNumerator / total, 0, 1);
            contexts.Add((context, (float)lambda));
            for (int index = position; index < end; index++)
            {
                (ulong key, int count) = entries[index];
                if (!ShouldExport(
                    count,
                    total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability)) continue;
                writer.Write(key);
                writer.Write((float)(ExportedNumerator(
                    count,
                    total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability,
                    rescueProbabilityWeight,
                    discounts) / total));
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
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability,
        double rescueProbabilityWeight,
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
                if (!ShouldExport(
                    count,
                    context.Total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability)) continue;
                writer.Write(key);
                writer.Write((float)(ExportedNumerator(
                    count,
                    context.Total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability,
                    rescueProbabilityWeight,
                    discounts) / context.Total));
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

    private static long CountBigramExports(
        KeyValuePair<ulong, int>[] entries,
        int minimumExportCount,
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability)
    {
        long exports = 0;
        int position = 0;
        while (position < entries.Length)
        {
            int context = (int)(entries[position].Key >> 21);
            int end = position;
            long total = 0;
            while (end < entries.Length && (int)(entries[end].Key >> 21) == context)
            {
                total += entries[end].Value;
                end++;
            }
            for (int index = position; index < end; index++)
            {
                if (ShouldExport(
                    entries[index].Value,
                    total,
                    minimumExportCount,
                    rescueMinimumCount,
                    rescueMinimumConditionalProbability))
                {
                    exports++;
                }
            }
            position = end;
        }
        return exports;
    }

    private static bool ShouldExport(
        int count,
        long contextTotal,
        int minimumExportCount,
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability)
    {
        return count >= minimumExportCount ||
               (rescueMinimumCount > 0 &&
                count >= rescueMinimumCount &&
                count / (double)contextTotal >= rescueMinimumConditionalProbability);
    }

    private static double ExportedNumerator(
        int count,
        long contextTotal,
        int minimumExportCount,
        int rescueMinimumCount,
        double rescueMinimumConditionalProbability,
        double rescueProbabilityWeight,
        Discounts discounts)
    {
        double numerator = count - discounts.ForCount(count);
        if (count >= minimumExportCount)
        {
            return numerator;
        }
        return ShouldExport(
            count,
            contextTotal,
            minimumExportCount,
            rescueMinimumCount,
            rescueMinimumConditionalProbability)
            ? rescueProbabilityWeight * numerator
            : 0.0;
    }

    private static (long Entries, CountOfCounts Counts) ReadCountStatistics(string path)
    {
        using var reader = new BinaryReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
            Encoding.UTF8,
            false);
        long entries = reader.ReadInt64();
        CountOfCounts counts = default;
        for (long index = 0; index < entries; index++)
        {
            reader.ReadUInt64();
            int count = reader.ReadInt32();
            counts = counts.Add(count);
        }
        return (entries, counts);
    }
}
