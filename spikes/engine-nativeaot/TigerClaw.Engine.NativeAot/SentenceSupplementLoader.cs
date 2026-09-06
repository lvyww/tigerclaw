using System.Globalization;
using TigerClaw.Core;

namespace TigerClaw.Engine.NativeAot;

internal static class SentenceSupplementLoader
{
    public static SentenceSupplementMatcher LoadForLexicon(string lexiconPath)
    {
        string? directory = Path.GetDirectoryName(lexiconPath);
        if (string.IsNullOrEmpty(directory))
        {
            return SentenceSupplementMatcher.Empty;
        }

        string path = Path.Combine(directory, "补充语料.txt");
        if (!File.Exists(path))
        {
            return SentenceSupplementMatcher.Empty;
        }

        var entries = new List<SentenceSupplementEntry>();
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            long weight = parts.Length > 1 && long.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : 1000;
            entries.Add(SentenceSupplementEntry.Create(parts[0], weight));
        }
        return SentenceSupplementMatcher.Build(entries);
    }
}
