namespace TigerClaw.Engine.Experimental.Lexicon;

public sealed class RimeLexiconProvider : ILexiconProvider
{
    private readonly InMemoryLexiconProvider _inner;

    private RimeLexiconProvider(InMemoryLexiconProvider inner, int entryCount)
    {
        _inner = inner;
        EntryCount = entryCount;
    }

    public int EntryCount { get; }

    public static RimeLexiconProvider Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Rime lexicon file was not found.", path);
        }

        var entriesByCode = new Dictionary<string, SortedDictionary<int, LexiconEntry>>(StringComparer.OrdinalIgnoreCase);
        bool inBody = false;
        int order = 0;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!inBody)
            {
                inBody = line == "...";
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string code = parts[1];
            int rank = 1;
            if (TryParseSelectionSuffix(code, out string baseCode, out int selectedRank))
            {
                code = baseCode;
                rank = selectedRank;
            }

            if (!entriesByCode.TryGetValue(code, out SortedDictionary<int, LexiconEntry>? ranked))
            {
                ranked = [];
                entriesByCode.Add(code, ranked);
            }

            if (!ranked.TryGetValue(rank, out LexiconEntry? existing))
            {
                ranked.Add(rank, new LexiconEntry(code, parts[0], order++));
            }
            else if (!string.Equals(existing.Text, parts[0], StringComparison.Ordinal))
            {
                // A malformed export must not silently replace a first-ranked value.
                throw new InvalidDataException($"Conflicting TigerClaw candidates for {code} rank {rank}.");
            }
        }

        List<LexiconEntry> entries = entriesByCode.Values
            .SelectMany(ranked => ranked.Values)
            .ToList();
        return new RimeLexiconProvider(new InMemoryLexiconProvider(entries), entries.Count);
    }

    public IReadOnlyList<LexiconEntry> LookupExact(string code)
    {
        return _inner.LookupExact(code);
    }

    public bool HasPrefix(string code) => _inner.HasPrefix(code);

    public bool IsUniqueTerminalCode(string code) => _inner.IsUniqueTerminalCode(code);

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
}
