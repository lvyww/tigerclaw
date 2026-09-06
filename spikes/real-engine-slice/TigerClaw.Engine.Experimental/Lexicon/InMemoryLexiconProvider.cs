namespace TigerClaw.Engine.Experimental.Lexicon;

public sealed class InMemoryLexiconProvider : ILexiconProvider
{
    private readonly Dictionary<string, List<LexiconEntry>> _entries;
    private readonly HashSet<string> _prefixes;

    public InMemoryLexiconProvider(IEnumerable<LexiconEntry> entries)
    {
        _entries = entries
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.Order).ToList(),
                StringComparer.OrdinalIgnoreCase);

        _prefixes = [];
        foreach (string code in _entries.Keys)
        {
            for (int length = 1; length <= code.Length; length++)
            {
                _prefixes.Add(code[..length]);
            }
        }
    }

    public IReadOnlyList<LexiconEntry> LookupExact(string code)
    {
        return _entries.TryGetValue(code, out List<LexiconEntry>? entries)
            ? entries
            : [];
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
}
