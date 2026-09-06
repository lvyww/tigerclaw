namespace TigerClaw.Engine.NativeAot;

internal sealed class RimeLexicon
{
    private static readonly string[] CompanionTableFileNames = ["快符.txt", "常用符号.txt"];
    private readonly Dictionary<string, List<string>> _entries;
    private readonly Dictionary<string, List<string>> _sentenceEntries;
    private readonly HashSet<string> _prefixes;

    private RimeLexicon(
        Dictionary<string, List<string>> entries,
        Dictionary<string, List<string>> sentenceEntries,
        int entryCount)
    {
        _entries = entries;
        _sentenceEntries = sentenceEntries;
        EntryCount = entryCount;
        _prefixes = [];
        foreach (string code in entries.Keys)
        {
            for (int length = 1; length <= code.Length; length++)
            {
                _prefixes.Add(code[..length]);
            }
        }
    }

    public int EntryCount { get; }

    public IDictionary<string, List<string>> SentenceSource => _sentenceEntries;

    public static RimeLexicon Load(string path, string? userDictionaryPath = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Rime lexicon file was not found.", path);
        }

        var rankedEntries = new Dictionary<string, List<LexiconEntry>>(StringComparer.OrdinalIgnoreCase);
        var sentenceRankedEntries = new Dictionary<string, List<LexiconEntry>>(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        int entryOrder = 0;
        LoadEntries(path, rankedEntries, ref entryCount, ref entryOrder, new HashSet<string>(StringComparer.OrdinalIgnoreCase), preferStem: false);
        int sentenceEntryCount = 0;
        int sentenceEntryOrder = 0;
        LoadEntries(path, sentenceRankedEntries, ref sentenceEntryCount, ref sentenceEntryOrder, new HashSet<string>(StringComparer.OrdinalIgnoreCase), preferStem: true);
        foreach (string companionPath in CompanionTablePaths(path))
        {
            LoadEntries(companionPath, rankedEntries, ref entryCount, ref entryOrder, new HashSet<string>(StringComparer.OrdinalIgnoreCase), preferStem: false);
            LoadEntries(companionPath, sentenceRankedEntries, ref sentenceEntryCount, ref sentenceEntryOrder, new HashSet<string>(StringComparer.OrdinalIgnoreCase), preferStem: true);
        }

        Dictionary<string, List<string>> entries = BuildStringEntries(rankedEntries);
        Dictionary<string, List<string>> sentenceEntries = BuildStringEntries(sentenceRankedEntries);

        foreach ((string code, string text) in LoadUserEntries(userDictionaryPath))
        {
            InsertUserEntry(entries, code, text);
            InsertUserEntry(sentenceEntries, code, text);
            entryCount++;
        }

        foreach (CandidateAdjustment adjustment in LoadCandidateAdjustments(userDictionaryPath))
        {
            if (entries.TryGetValue(adjustment.Code, out List<string>? candidates))
            {
                int index = candidates.FindIndex(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                ApplyAdjustment(candidates, index, adjustment.Operation);
            }
            if (sentenceEntries.TryGetValue(adjustment.Code, out List<string>? sentenceCandidates))
            {
                int sentenceIndex = sentenceCandidates.FindIndex(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                ApplyAdjustment(sentenceCandidates, sentenceIndex, adjustment.Operation);
            }
        }

        foreach (WindowsUserAdjustment adjustment in LoadWindowsUserAdjustments(userDictionaryPath))
        {
            ApplyWindowsAdjustment(entries, adjustment);
            ApplyWindowsAdjustment(sentenceEntries, adjustment);
            if (adjustment.Operation == WindowsUserAdjustmentOperation.Add)
            {
                entryCount++;
            }
        }

        // Apply this last as user dictionaries and Windows-compatible
        // adjustments can also add entries to the sentence source.
        RemoveSentenceActionEntries(sentenceEntries);

        return new RimeLexicon(entries, sentenceEntries, entryCount);
    }

    private static void InsertUserEntry(Dictionary<string, List<string>> entries, string code, string text)
    {
        if (!entries.TryGetValue(code, out List<string>? candidates))
        {
            candidates = [];
            entries.Add(code, candidates);
        }
        candidates.RemoveAll(candidate => string.Equals(candidate, text, StringComparison.Ordinal));
        candidates.Insert(0, text);
    }

    private static Dictionary<string, List<string>> BuildStringEntries(
        Dictionary<string, List<LexiconEntry>> rankedEntries) =>
        rankedEntries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderBy(entry => entry.Rank)
                .ThenByDescending(entry => entry.Weight)
                .ThenBy(entry => entry.Order)
                .Select(entry => entry.Text)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

    private static void RemoveSentenceActionEntries(Dictionary<string, List<string>> entries)
    {
        // 快符 tables intentionally share the ordinary lexicon so that [码 can
        // expand them at commit time. Their {动作名} placeholders, however, are
        // not text and must never become sentence-decoder edges. Apart from
        // showing up literally, an explicit ; rank could otherwise select one
        // in the middle of a sentence (for example 我{重复上屏}在).
        foreach (string code in entries.Keys.ToArray())
        {
            List<string> candidates = entries[code];
            candidates.RemoveAll(IsSentenceActionEntry);
            if (candidates.Count == 0)
            {
                entries.Remove(code);
            }
        }
    }

    private static bool IsSentenceActionEntry(string text) =>
        text.Length >= 2 &&
        text[0] == '{' &&
        text[^1] == '}';

    private static void ApplyAdjustment(
        List<string> candidates,
        int index,
        CandidateAdjustmentOperation operation)
    {
        switch (operation)
        {
            case CandidateAdjustmentOperation.Top when index > 0:
                string top = candidates[index];
                candidates.RemoveAt(index);
                candidates.Insert(0, top);
                break;
            case CandidateAdjustmentOperation.Advance when index > 0:
                (candidates[index - 1], candidates[index]) = (candidates[index], candidates[index - 1]);
                break;
            case CandidateAdjustmentOperation.Delete when index >= 0:
                candidates.RemoveAt(index);
                break;
        }
    }

    private static void ApplyWindowsAdjustment(
        Dictionary<string, List<string>> entries,
        WindowsUserAdjustment adjustment)
    {
        switch (adjustment.Operation)
        {
            case WindowsUserAdjustmentOperation.Add:
                if (!entries.TryGetValue(adjustment.Code, out List<string>? addedCandidates))
                {
                    addedCandidates = [];
                    entries.Add(adjustment.Code, addedCandidates);
                }
                addedCandidates.RemoveAll(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                addedCandidates.Add(adjustment.Text);
                break;
            case WindowsUserAdjustmentOperation.Top when entries.TryGetValue(adjustment.Code, out List<string>? topCandidates):
                int topIndex = topCandidates.FindIndex(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                if (topIndex > 0)
                {
                    string top = topCandidates[topIndex];
                    topCandidates.RemoveAt(topIndex);
                    topCandidates.Insert(0, top);
                }
                break;
            case WindowsUserAdjustmentOperation.Advance when entries.TryGetValue(adjustment.Code, out List<string>? advanceCandidates):
                int advanceIndex = advanceCandidates.FindIndex(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                if (advanceIndex > 0)
                {
                    (advanceCandidates[advanceIndex - 1], advanceCandidates[advanceIndex]) =
                        (advanceCandidates[advanceIndex], advanceCandidates[advanceIndex - 1]);
                }
                break;
            case WindowsUserAdjustmentOperation.Delete when entries.TryGetValue(adjustment.Code, out List<string>? deleteCandidates):
                deleteCandidates.RemoveAll(candidate => string.Equals(candidate, adjustment.Text, StringComparison.Ordinal));
                break;
        }
    }

    private static void LoadEntries(
        string path,
        Dictionary<string, List<LexiconEntry>> entries,
        ref int entryCount,
        ref int entryOrder,
        HashSet<string> loadedPaths,
        bool preferStem)
    {
        string fullPath = Path.GetFullPath(path);
        if (!loadedPaths.Add(fullPath))
        {
            return;
        }

        bool rimeDictionary = fullPath.EndsWith(".dict.yaml", StringComparison.OrdinalIgnoreCase);
        bool inBody = !rimeDictionary;
        var columns = new List<string>();
        var importedTables = new List<string>();
        bool readingColumns = false;
        bool readingImports = false;

        foreach (string rawLine in File.ReadLines(fullPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!inBody)
            {
                if (line == "...")
                {
                    inBody = true;
                    readingColumns = false;
                    readingImports = false;
                    continue;
                }

                if (line == "columns:")
                {
                    readingColumns = true;
                    readingImports = false;
                    continue;
                }

                if (line == "import_tables:")
                {
                    readingImports = true;
                    readingColumns = false;
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    string value = line[2..].Trim();
                    if (readingColumns)
                    {
                        columns.Add(value);
                    }
                    else if (readingImports && !string.IsNullOrWhiteSpace(value))
                    {
                        importedTables.Add(value);
                    }
                    continue;
                }

                if (!char.IsWhiteSpace(rawLine.FirstOrDefault()))
                {
                    readingColumns = false;
                    readingImports = false;
                }
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int textIndex = 0;
            int codeIndex = 1;
            int weightIndex = -1;
            int stemIndex = -1;
            if (rimeDictionary && columns.Count > 0)
            {
                textIndex = columns.FindIndex(column => string.Equals(column, "text", StringComparison.OrdinalIgnoreCase));
                codeIndex = columns.FindIndex(column => string.Equals(column, "code", StringComparison.OrdinalIgnoreCase));
                weightIndex = columns.FindIndex(column => string.Equals(column, "weight", StringComparison.OrdinalIgnoreCase));
                stemIndex = columns.FindIndex(column => string.Equals(column, "stem", StringComparison.OrdinalIgnoreCase));
                if (textIndex < 0 || codeIndex < 0)
                {
                    continue;
                }
            }

            if (parts.Length <= Math.Max(textIndex, codeIndex))
            {
                continue;
            }

            string text = parts[textIndex];
            string code = parts[codeIndex];
            if (preferStem && rimeDictionary && stemIndex >= 0 && parts.Length > stemIndex && !string.IsNullOrWhiteSpace(parts[stemIndex]))
            {
                // Tiger's Rime dictionary stores a short code for ordinary
                // lookup and its full input code in `stem`. Sentence
                // segmentation needs the latter (for example, 的 is `un`).
                code = parts[stemIndex];
            }
            long weight = 0;
            if (weightIndex >= 0 && parts.Length > weightIndex)
            {
                _ = long.TryParse(parts[weightIndex], out weight);
            }

            if (!rimeDictionary)
            {
                if (parts.Length >= 3 && long.TryParse(parts[1], out weight))
                {
                    code = parts[2];
                }
                else if (parts.Length >= 3 && long.TryParse(parts[2], out weight))
                {
                    code = parts[1];
                }
            }

            int rank = 1;
            if (TryParseSelectionSuffix(code, out string baseCode, out int selectedRank))
            {
                code = baseCode;
                rank = selectedRank;
            }

            if (!entries.TryGetValue(code, out List<LexiconEntry>? candidates))
            {
                candidates = [];
                entries.Add(code, candidates);
            }
            int order = entryOrder++;
            candidates.Add(new LexiconEntry(rank, weight, order, text));
            entryCount++;
        }

        foreach (string table in importedTables)
        {
            string importedPath = Path.Combine(Path.GetDirectoryName(fullPath)!, table + ".dict.yaml");
            if (!File.Exists(importedPath))
            {
                throw new InvalidDataException($"Imported Rime table was not found: {importedPath}");
            }
            LoadEntries(importedPath, entries, ref entryCount, ref entryOrder, loadedPaths, preferStem);
        }
    }

    private static IEnumerable<string> CompanionTablePaths(string primaryPath)
    {
        string? directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            yield break;
        }
        foreach (string filename in CompanionTableFileNames)
        {
            string path = Path.Combine(directory, filename);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    public IReadOnlyList<string> LookupExact(string code)
    {
        return _entries.TryGetValue(code, out List<string>? values) ? values : [];
    }

    public bool HasPrefix(string code)
    {
        return !string.IsNullOrEmpty(code) && _prefixes.Contains(code);
    }

    public bool IsUniqueTerminalCode(string code)
    {
        return LookupExact(code).Count == 1 &&
               !_entries.Keys.Any(candidate =>
                   candidate.Length > code.Length &&
                   candidate.StartsWith(code, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSelectionSuffix(string code, out string baseCode, out int rank)
    {
        baseCode = code;
        rank = 0;
        if (code.Length < 2)
        {
            return false;
        }

        char suffix = code[^1];
        if (suffix is >= '2' and <= '9')
        {
            baseCode = code[..^1];
            rank = suffix - '0';
            return true;
        }

        if (suffix == ';')
        {
            baseCode = code[..^1];
            rank = 2;
            return true;
        }

        if (suffix == '\'')
        {
            baseCode = code[..^1];
            rank = 3;
            return true;
        }

        return false;
    }

    private static IEnumerable<(string Code, string Text)> LoadUserEntries(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            yield break;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split('\t', StringSplitOptions.None);
            if (parts.Length != 2)
            {
                continue;
            }

            string text = parts[0].Trim();
            string code = parts[1].Trim();
            if (text.Length == 0 || code.Length == 0 || !code.All(IsSupportedUserCodeCharacter))
            {
                continue;
            }

            yield return (code, text);
        }
    }

    private static bool IsSupportedUserCodeCharacter(char value) =>
        char.IsLetter(value) || value is ';' or '/' or '[';

    private static IEnumerable<CandidateAdjustment> LoadCandidateAdjustments(string? userDictionaryPath)
    {
        if (string.IsNullOrWhiteSpace(userDictionaryPath))
        {
            yield break;
        }

        string? directory = Path.GetDirectoryName(userDictionaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            yield break;
        }

        string path = Path.Combine(directory, "candidate-adjustments.tsv");
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            string[] parts = rawLine.Split('\t', 3, StringSplitOptions.None);
            if (parts.Length != 3 || !TryCandidateAdjustmentOperation(parts[0], out CandidateAdjustmentOperation operation))
            {
                continue;
            }

            string code = parts[1].Trim().ToLowerInvariant();
            string text = parts[2].Trim();
            if (code.Length == 0 || text.Length == 0)
            {
                continue;
            }
            yield return new CandidateAdjustment(operation, code, text);
        }
    }

    private static bool TryCandidateAdjustmentOperation(string value, out CandidateAdjustmentOperation operation)
    {
        operation = value switch
        {
            "top" => CandidateAdjustmentOperation.Top,
            "advance" => CandidateAdjustmentOperation.Advance,
            "delete" => CandidateAdjustmentOperation.Delete,
            _ => default,
        };
        return value is "top" or "advance" or "delete";
    }

    private enum CandidateAdjustmentOperation
    {
        Top,
        Advance,
        Delete,
    }

    private readonly record struct CandidateAdjustment(CandidateAdjustmentOperation Operation, string Code, string Text);

    private static IEnumerable<WindowsUserAdjustment> LoadWindowsUserAdjustments(string? userDictionaryPath)
    {
        if (string.IsNullOrWhiteSpace(userDictionaryPath))
        {
            yield break;
        }

        string? directory = Path.GetDirectoryName(userDictionaryPath);
        if (string.IsNullOrEmpty(directory))
        {
            yield break;
        }

        string path = Path.GetFileName(userDictionaryPath) == "用户调整.txt"
            ? userDictionaryPath
            : Path.Combine(directory, "用户调整.txt");
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (!TryWindowsUserAdjustmentOperation(line, out WindowsUserAdjustmentOperation operation, out string payload))
            {
                continue;
            }

            int separator = payload.IndexOfAny(['\t', ' ', ',']);
            if (separator <= 0 || separator == payload.Length - 1)
            {
                continue;
            }

            string code = payload[..separator].Trim().ToLowerInvariant();
            string text = payload[(separator + 1)..].Trim();
            if (code.Length == 0 || text.Length == 0 || !code.All(IsSupportedUserCodeCharacter))
            {
                continue;
            }
            yield return new WindowsUserAdjustment(operation, code, text);
        }
    }

    private static bool TryWindowsUserAdjustmentOperation(
        string line,
        out WindowsUserAdjustmentOperation operation,
        out string payload)
    {
        foreach ((string prefix, WindowsUserAdjustmentOperation candidate) in new[]
        {
            ("{添加}", WindowsUserAdjustmentOperation.Add),
            ("{置顶}", WindowsUserAdjustmentOperation.Top),
            ("{前移}", WindowsUserAdjustmentOperation.Advance),
            ("{删除}", WindowsUserAdjustmentOperation.Delete),
        })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                operation = candidate;
                payload = line[prefix.Length..];
                return true;
            }
        }

        operation = default;
        payload = string.Empty;
        return false;
    }

    private enum WindowsUserAdjustmentOperation
    {
        Add,
        Top,
        Advance,
        Delete,
    }

    private readonly record struct WindowsUserAdjustment(WindowsUserAdjustmentOperation Operation, string Code, string Text);
    private readonly record struct LexiconEntry(int Rank, long Weight, int Order, string Text);
}
