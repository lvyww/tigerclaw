using System.Text;
using System.Text.Json;

namespace TigerClaw.Pinyin;

// Explicit pronunciations are authoritative. Do not infer a polyphonic word's
// reading from its surface text. A replacement is durable before publication.
internal static class PinyinUserWords
{
    internal const string FileName = "pinyin-user-words.json";
    internal sealed record Word(string Text, string[] Readings);
    internal static Word Validate(string text, string reading)
    {
        var chars = text.EnumerateRunes().Select(r => r.ToString()).ToArray();
        string normalized = reading.Trim().ToLowerInvariant().Replace("u:", "v").Replace('ü', 'v');
        var readings = normalized.Split(new[] { ' ', '\t', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (chars.Length == 0 || chars.Length > 16 || readings.Length != chars.Length ||
            text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '/' || c == '{' || c == '}'))
            throw new ArgumentException("词条须为 1–16 个字；每个字填写一个拼音，用空格分隔。");
        if (readings.Any(r => r.Length == 0 || r.Length > 6 || r.Any(c => c < 'a' || c > 'z')))
            throw new ArgumentException("请填写无声调全拼，ü 使用 v。");
        return new Word(text, readings);
    }
    internal static Word[] Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("全拼用户词文件过大");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.EnumerateArray().Select(row => Validate(row.GetProperty("text").GetString()!,
            string.Join(" ", row.GetProperty("readings").EnumerateArray().Select(x => x.GetString())))).Take(10001).ToArray();
    }
    internal static Lexicon Merge(Lexicon baseline, IEnumerable<Word> words)
    {
        var additions = words.ToArray();
        if (additions.Length == 0) return baseline;
        var syllables = baseline.Entries.Where(e => e.Characters.Length == 1).Select(e => Lexicon.RawReading(e.Tokens![0])).ToHashSet(StringComparer.Ordinal);
        var delta = new Dictionary<(string Text,string Code),Entry>();
        foreach (var word in additions)
        {
            var chars = word.Text.EnumerateRunes().Select(r => r.ToString()).ToArray();
            var rawReadings = word.Readings.Select(r => r == "nve" ? "nue" : r == "lve" ? "lue" : r).ToArray();
            if (rawReadings.Any(r => !syllables.Contains(r))) throw new ArgumentException("用户词包含词典不支持的拼音音节");
            string code = string.Concat(rawReadings);
            var tokens = chars.Select((c, i) => c + "/" + (rawReadings[i] == "nue" ? "nve" : rawReadings[i] == "lue" ? "lve" : rawReadings[i])).ToArray();
            int frequency = baseline.Edges(code,0).Where(e=>e.End==code.Length&&e.Entry.Text==word.Text).Select(e=>e.Entry.Frequency).DefaultIfEmpty(0).Max();
            delta[(word.Text,code)] = new(word.Text,code,Math.Max(1000,frequency),chars,tokens,9);
        }
        return new Lexicon(baseline,delta.Values);
    }
    internal static void Save(string directory, Word[] words)
    {
        if (words.Length > 10000) throw new InvalidDataException("全拼用户词最多 10000 条");
        string path = Path.Combine(directory, FileName), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartArray();
                    foreach (var word in words)
                    {
                        writer.WriteStartObject(); writer.WriteString("text", word.Text); writer.WriteStartArray("readings");
                        foreach (string reading in word.Readings) writer.WriteStringValue(reading);
                        writer.WriteEndArray(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.Flush();
                }
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
