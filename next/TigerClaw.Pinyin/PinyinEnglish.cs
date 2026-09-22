namespace TigerClaw.Pinyin;

internal sealed class PinyinEnglish
{
    private sealed class Node
    {
        internal readonly Dictionary<char, Node> Next = new();
        internal readonly List<(string Text, int Weight)> Words = new();
        internal (string Text, string Code, int Weight)[] Completion = [];
    }
    private readonly Node root = new();
    internal static readonly PinyinEnglish Empty = new([]);
    internal PinyinEnglish(IEnumerable<(string Text, string Code, int Weight)> rows)
    {
        foreach (var row in rows.OrderByDescending(x => x.Weight).ThenBy(x => x.Text.Length).ThenBy(x => x.Text, StringComparer.Ordinal))
        {
            if (row.Code.Length < 2 || row.Code.Length > 80 || row.Code.Any(c => !char.IsAsciiLetter(c))) continue;
            var node = root;
            foreach (char c in row.Code.ToLowerInvariant())
            {
                if (!node.Next.TryGetValue(c, out var next)) node.Next[c] = next = new();
                node = next;
                if (node.Completion.Length < 5) node.Completion = node.Completion.Append(row).ToArray();
            }
            if (!node.Words.Any(x => x.Text == row.Text)) node.Words.Add((row.Text, row.Weight));
        }
    }
    internal static PinyinEnglish Load(string directory)
    {
        var rows = new List<(string, string, int)>();
        foreach (string name in new[] { "en.dict.yaml", "en_ext.dict.yaml", "cn_en.txt" })
        {
            string file = Path.Combine(directory, "resources", "english", name);
            if (!File.Exists(file)) continue;
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith('#') || !line.Contains('\t')) continue;
                var fields = line.Split('\t');
                if (fields.Length >= 2) rows.Add((fields[0], fields[1], fields.Length >= 3 && int.TryParse(fields[2], out int n) ? n : 0));
            }
        }
        rows.Add(("WiFi", "wifi", 1000)); rows.Add(("Type-C", "typec", 1000));
        return new(rows);
    }
    internal bool HasExact(string code)
    {
        var node = root;
        foreach (char c in code.ToLowerInvariant()) if (!node.Next.TryGetValue(c, out node!)) return false;
        return node.Words.Count > 0;
    }
    internal IEnumerable<PinyinChoice> Candidates(string raw, Candidate? locked)
    {
        int start = locked?.Segments.LastOrDefault()?.End ?? 0;
        var node = root;
        var previous = locked?.Segments ?? [];
        for (int at = start; at < raw.Length && node.Next.TryGetValue(char.ToLowerInvariant(raw[at]), out node!); at++)
        {
            int end = at + 1;
            // The entire live input must equal the entry code or be its prefix.
            // Do not offer "chang" for "changyongzi" (the reverse relation).
            foreach (var word in end == raw.Length ? node.Words.Take(5) : [])
            {
                string text = Case(word.Text, raw[start..end]);
                var segments = previous.Append(new Segment(raw[start..end].ToLowerInvariant(), text, start, end, [])).ToArray();
                yield return new(new((locked?.Text ?? "") + text, 0, word.Weight, segments), text, end, end == raw.Length, Literal: true, Annotation: "英文 / 混输");
            }
            if (end == raw.Length && end - start >= 3)
                foreach (var word in node.Completion.Where(x => x.Code.Length > end - start))
                {
                    string text = Case(word.Text, raw[start..end]);
                    var segments = previous.Append(new Segment(raw[start..end].ToLowerInvariant(), text, start, end, [])).ToArray();
                    yield return new(new((locked?.Text ?? "") + text, 0, word.Weight, segments), text, end, true, Literal: true, Annotation: "英文补全");
                }
        }
    }
    private static string Case(string word, string raw)
    {
        if (word.Any(c => c > 127)) return word;
        if (raw.Length > 1 && char.IsUpper(raw[0]) && char.IsUpper(raw[1])) return word.ToUpperInvariant();
        if (raw.Length > 0 && char.IsUpper(raw[0])) return char.ToUpperInvariant(word[0]) + word[1..];
        return word;
    }
}
