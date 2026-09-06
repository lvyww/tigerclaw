namespace TigerClaw.Engine.NativeAot;

/// <summary>Loads the Windows 拼音反查码表 text format: text, pinyin, frequency.</summary>
internal sealed class PinyinLexicon
{
    private readonly Dictionary<string, List<string>> _entries;

    private PinyinLexicon(Dictionary<string, List<string>> entries)
    {
        _entries = entries;
    }

    public bool IsAvailable => _entries.Count > 0;

    public IReadOnlyList<string> Lookup(string code)
    {
        return _entries.TryGetValue(code, out List<string>? values) ? values : [];
    }

    public static PinyinLexicon Load(string? path)
    {
        var rows = new List<(string Code, string Text, int Frequency, int Order)>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new PinyinLexicon(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        }

        int order = 0;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] fields = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            string text = fields[0];
            string code = fields[1].ToLowerInvariant();
            if (text.Length == 0 || code.Length == 0) continue;
            int frequency = fields.Length > 2 && int.TryParse(fields[2], out int parsed) ? parsed : 0;
            rows.Add((code, text, frequency, order++));
        }

        Dictionary<string, List<string>> entries = rows
            .OrderByDescending(row => row.Frequency)
            .ThenBy(row => row.Order)
            .GroupBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Text).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
        return new PinyinLexicon(entries);
    }
}
