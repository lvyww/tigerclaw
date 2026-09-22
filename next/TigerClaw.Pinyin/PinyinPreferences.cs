using System.Text.Json;

namespace TigerClaw.Pinyin;

// Fixed phrases and explicit ranking are independent of the probabilistic
// lexicon and correction journal. Only a successful atomic save is published.
internal sealed class PinyinPreferences
{
    internal const string FileName = "pinyin-preferences.json";
    internal sealed record Item(string Code, string Text);
    internal Item[] Phrases { get; }
    internal Item[] Pins { get; }
    private readonly Dictionary<string, Item[]> phrases;
    private readonly Dictionary<string, Item> pins;
    internal static readonly PinyinPreferences Empty = new([], []);
    internal PinyinPreferences(Item[] phrases, Item[] pins)
    {
        if (phrases.Length > 10000 || pins.Length > 10000) throw new ArgumentException("短语和置顶各最多 10000 条");
        foreach (var item in phrases.Concat(pins)) Validate(item.Code, item.Text);
        Phrases = phrases.Distinct().ToArray(); Pins = pins.GroupBy(x => x.Code).Select(g => g.Last()).ToArray();
        this.phrases = Phrases.GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        this.pins = Pins.ToDictionary(x => x.Code, StringComparer.Ordinal);
    }
    internal static Item Validate(string code, string text)
    {
        code = code.Trim().ToLowerInvariant();
        if (code.Length == 0 || code.Length > 128 || code.Any(c => (c < 'a' || c > 'z') && c != '\''))
            throw new ArgumentException("短码须为 1–128 个英文字母或音节分隔符");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("内容须为 1–4096 个字符，可包含换行");
        return new(code, text);
    }
    internal Item[] GetPhrases(string code) => phrases.GetValueOrDefault(code, []);
    internal bool IsPinned(string code, string text) => pins.TryGetValue(code, out var pin) && pin.Text == text;
    internal Item[] MatchingPins(string raw)
    {
        var result = new List<Item>();
        for (int i = 1; i <= Math.Min(128, raw.Length); i++)
            if (pins.TryGetValue(raw[..i], out var item)) result.Add(item);
        return result.TakeLast(16).ToArray();
    }
    internal PinyinPreferences Edit(string action, string code, string text)
    {
        var item = Validate(code, text);
        return action switch
        {
            "phrase_add" => new(Phrases.Where(x => x != item).Append(item).ToArray(), Pins),
            "phrase_delete" => new(Phrases.Where(x => x != item).ToArray(), Pins),
            "pin" => new(Phrases, Pins.Where(x => x.Code != item.Code).Append(item).ToArray()),
            "unpin" => new(Phrases, Pins.Where(x => x != item).ToArray()),
            _ => throw new ArgumentException("未知的全拼管理操作")
        };
    }
    internal static PinyinPreferences Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return Empty;
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("全拼偏好文件过大");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("不支持的全拼偏好版本");
        Item[] Read(string key) => doc.RootElement.GetProperty(key).EnumerateArray().Select(x =>
            Validate(x.GetProperty("code").GetString()!, x.GetProperty("text").GetString()!)).Take(10001).ToArray();
        return new(Read("phrases"), Read("pins"));
    }
    internal void Save(string directory)
    {
        string path = Path.Combine(directory, FileName), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    json.WriteStartObject(); json.WriteNumber("version", 1);
                    foreach (var list in new[] { ("phrases", Phrases), ("pins", Pins) })
                    {
                        json.WriteStartArray(list.Item1);
                        foreach (var item in list.Item2)
                        { json.WriteStartObject(); json.WriteString("code", item.Code); json.WriteString("text", item.Text); json.WriteEndObject(); }
                        json.WriteEndArray();
                    }
                    json.WriteEndObject(); json.Flush();
                }
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
