using System.Text;

namespace TigerClaw.Pinyin;

internal sealed class PinyinTigerAux
{
    private readonly Dictionary<string, HashSet<string>> prefixes = new(StringComparer.Ordinal);
    internal PinyinTigerAux(IEnumerable<(string Text, string Code)> rows)
    {
        foreach (var row in rows)
        {
            if (row.Text.EnumerateRunes().Count() != 1 || row.Code.Length > 8 || row.Code.Any(c => c < 'a' || c > 'z')) continue;
            for (int length = 1; length <= row.Code.Length; length++)
            {
                string prefix = row.Code[..length];
                if (!prefixes.TryGetValue(prefix, out var set)) prefixes[prefix] = set = new(StringComparer.Ordinal);
                set.Add(row.Text);
            }
        }
    }
    internal IReadOnlySet<string>? Match(string? code) => string.IsNullOrEmpty(code) ? null : prefixes.GetValueOrDefault(code, []);
    internal static PinyinTigerAux Load(string directory)
    {
        string path = Path.Combine(directory, "resources", "tiger-codes.txt");
        return new(!File.Exists(path) ? [] : File.ReadLines(path).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Where(f => f.Length == 2).Select(f => (f[0], f[1])));
    }
}
