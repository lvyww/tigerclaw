namespace TigerClaw.Pinyin;

[Flags]
internal enum PinyinSpellingOptions { None = 0, Abbreviations = 1, Aliases = 2, Typos = 4, NL = 8, ZZh = 16, CCh = 32, SSh = 64, EnEng = 128, InIng = 256, AnAng = 512, DoublePinyin = 1024 }

internal sealed record PinyinEdge(Entry Entry, int End, double Penalty = 0, int[]? RawEnds = null, bool Incomplete = false);

internal sealed partial class Lexicon
{
    private readonly Lazy<SpellingIndex> spelling;
    internal readonly record struct FormMatch(string Reading, double Penalty, bool Incomplete, int End);
    internal sealed class MatchCache
    {
        private string raw = "";
        private PinyinSpellingOptions options;
        internal readonly Dictionary<int, FormMatch[]> Forms = new();
        internal void Prepare(string value,PinyinSpellingOptions spelling,int maximumForm)
        {
            if(raw==value&&options==spelling)return;
            if(options==spelling&&value.StartsWith(raw,StringComparison.Ordinal))
            {
                int affected=Math.Max(0,raw.Length-maximumForm+1);
                foreach(int start in Forms.Keys.Where(k=>k>=affected).ToArray())Forms.Remove(start);
            }
            else Forms.Clear();
            raw=value;options=spelling;
        }
    }
    // Menu lookup also permits word edges across explicit syllable separators,
    // even when aliases and abbreviations are disabled.
    internal IEnumerable<PinyinEdge> MenuEdges(string raw, int start, PinyinSpellingOptions options)
        => spelling.Value.Match(raw, start, options, default);
    internal bool[] MenuSuffixes(string raw, PinyinSpellingOptions options, bool allowIncomplete)
        => spelling.Value.Suffixes(raw, options, allowIncomplete);
    internal IEnumerable<PinyinEdge> SpelledEdges(string raw, int start, PinyinSpellingOptions options,
        CancellationToken cancellation = default, int afterEnd = -1, MatchCache? cache = null)
    {
        if (options == PinyinSpellingOptions.None)
            return Edges(raw, start).Where(e => e.End > afterEnd).Select(e => new PinyinEdge(e.Entry, e.End));
        return spelling.Value.Match(raw, start, options, cancellation, afterEnd, cache);
    }

    // Index canonical syllables, not every possible abbreviated word. Thus
    // nihao, nhao, nih and nh share one word and the same pronunciation tokens.
    internal static string? XiaoheCode(string reading)
    {
        if (reading.Length == 0) return null;
        if ("aoe".Contains(reading[0]))
            return reading.Length == 1 ? reading + reading : reading is "ang" or "eng" ? reading[..1] + (reading == "ang" ? "h" : "g") : reading;
        string onset = reading.StartsWith("zh") || reading.StartsWith("ch") || reading.StartsWith("sh") ? reading[..2] : reading[..1];
        string tail = reading[onset.Length..];
        if (tail.Length == 0) return null;
        string key = tail switch
        {
            "iu" => "q", "ei" => "w", "uan" => "r", "ue" or "ve" => "t", "un" => "y", "uo" => "o", "ie" => "p",
            "ong" or "iong" => "s", "ing" or "uai" => "k", "ai" => "d", "en" => "f", "eng" => "g", "iang" or "uang" => "l",
            "ang" => "h", "ian" => "m", "an" => "j", "ou" => "z", "ia" or "ua" => "x", "iao" => "n", "ao" => "c", "ui" => "v", "in" => "b",
            _ => tail.Length == 1 ? tail : ""
        };
        return key.Length == 0 ? null : (onset == "zh" ? "v" : onset == "ch" ? "i" : onset == "sh" ? "u" : onset) + key;
    }
    private sealed class SpellingIndex
    {
        internal bool[] Suffixes(string raw, PinyinSpellingOptions options, bool allowIncomplete)
        {
            bool Allowed((string Reading, PinyinSpellingOptions Option, double Penalty, bool Incomplete) mapping)
                => options.HasFlag(PinyinSpellingOptions.DoublePinyin) == (mapping.Option == PinyinSpellingOptions.DoublePinyin)
                && (mapping.Option & options) == mapping.Option
                // Bare consonant interjections must not legalize cutting cha|ng
                // inside a complete chang. Actual decoded interjections are
                // preserved separately through the winning path's boundaries.
                && (options.HasFlag(PinyinSpellingOptions.Abbreviations) || mapping.Reading.Any(c => "aeiouv".Contains(c)));
            var completion = new Dictionary<FormNode, bool>();
            bool CanComplete(FormNode node)
            {
                if (!completion.TryGetValue(node, out bool value))
                    completion[node] = value = node.Forms.Any(Allowed) || node.Next.Values.Any(CanComplete);
                return value;
            }
            var valid = new bool[raw.Length + 1]; valid[^1] = true;
            for (int start = raw.Length - 1; start >= 0; start--)
            {
                if (raw[start] == '\'') { valid[start] = valid[start + 1]; continue; }
                var node = forms;
                for (int at = start; at < raw.Length && node.Next.TryGetValue(raw[at], out node!); at++)
                {
                    int end = at + 1;
                    if (valid[end] && node.Forms.Any(m => Allowed(m) && (!m.Incomplete || end == raw.Length)))
                    { valid[start] = true; break; }
                    // Keep legal incomplete final syllables during ongoing typing.
                    if (allowIncomplete && end == raw.Length && CanComplete(node)) valid[start] = true;
                }
            }
            return valid;
        }
        private sealed class FormNode
        {
            internal readonly Dictionary<char, FormNode> Next = new();
            internal readonly List<(string Reading, PinyinSpellingOptions Option, double Penalty, bool Incomplete)> Forms = new();
        }
        private readonly CompactPinyinTrie<string> syllables;
        private readonly FormNode forms = new();
        private int maximumForm;
        internal SpellingIndex(IEnumerable<Entry> entries)
        {
            var readings = new HashSet<string>(StringComparer.Ordinal);
            var pool = new Dictionary<string,string>(StringComparer.Ordinal);
            var builder = new CompactPinyinTrie<string>.Builder();
            var tokenReadings=new Dictionary<string,string>(StringComparer.Ordinal);
            string Reading(string token)
            {
                if(tokenReadings.TryGetValue(token,out var cached))return cached;
                string reading=RawReading(token);
                if(!pool.TryGetValue(reading,out var shared))pool[reading]=shared=reading;
                readings.Add(shared);return tokenReadings[token]=shared;
            }
            Func<string,string> convert=Reading;
            foreach (var entry in entries)
            {
                if(entry.Tokens!=null)builder.AddTransformed(entry.Tokens,convert,entry);
            }
            syllables = builder.Build(StringComparer.Ordinal);
            foreach (string reading in readings.Order(StringComparer.Ordinal))
            {
                Add(reading, reading, PinyinSpellingOptions.None, 0);
                string? doubleCode = XiaoheCode(reading);
                if (doubleCode != null)
                {
                    Add(doubleCode, reading, PinyinSpellingOptions.DoublePinyin, 0);
                    Add(doubleCode[..1], reading, PinyinSpellingOptions.DoublePinyin, 0, true);
                    if (reading.Length == 2 && "jqxy".Contains(reading[0]) && reading[1] == 'u')
                        Add(reading[..1] + "v", reading, PinyinSpellingOptions.DoublePinyin, 0);
                }
                if (reading.Length >= 3)
                    for (int at = 0; at + 1 < reading.Length; at++)
                    {
                        char[] swapped = reading.ToCharArray(); (swapped[at], swapped[at + 1]) = (swapped[at + 1], swapped[at]);
                        string typo = new(swapped);
                        if (typo != reading && !readings.Contains(typo)) Add(typo, reading, PinyinSpellingOptions.Typos, 4.0);
                    }
                foreach (var pair in new[] { ("n", "l", PinyinSpellingOptions.NL), ("z", "zh", PinyinSpellingOptions.ZZh),
                    ("c", "ch", PinyinSpellingOptions.CCh), ("s", "sh", PinyinSpellingOptions.SSh) })
                {
                    string onset = reading.StartsWith("zh") || reading.StartsWith("ch") || reading.StartsWith("sh") ? reading[..2] : reading[..1];
                    if (onset == pair.Item1 || onset == pair.Item2)
                        Add((onset == pair.Item1 ? pair.Item2 : pair.Item1) + reading[onset.Length..], reading, pair.Item3, 1.5);
                }
                foreach (var pair in new[] { ("en", "eng", PinyinSpellingOptions.EnEng), ("in", "ing", PinyinSpellingOptions.InIng), ("an", "ang", PinyinSpellingOptions.AnAng) })
                {
                    if (reading.EndsWith(pair.Item1, StringComparison.Ordinal)) Add(reading[..^pair.Item1.Length] + pair.Item2, reading, pair.Item3, 1.5);
                    if (reading.EndsWith(pair.Item2, StringComparison.Ordinal)) Add(reading[..^pair.Item2.Length] + pair.Item1, reading, pair.Item3, 1.5);
                }
                if (reading.Length > 1)
                    Add(reading[..1], reading, PinyinSpellingOptions.Abbreviations, 3.0);
                if (reading.Length > 2 && (reading.StartsWith("zh") || reading.StartsWith("ch") || reading.StartsWith("sh")))
                    Add(reading[..2], reading, PinyinSpellingOptions.Abbreviations, 3.0);
                if (reading is "nue" or "lue") Add(reading[..1] + "ve", reading, PinyinSpellingOptions.Aliases, 0);
                if (reading.Length >= 2 && "jqxy".Contains(reading[0]) && reading[1] == 'u')
                    Add(reading[..1] + "v" + reading[2..], reading, PinyinSpellingOptions.Aliases, 0);
            }
        }
        private void Add(string input, string reading, PinyinSpellingOptions option, double penalty, bool incomplete = false)
        {
            maximumForm=Math.Max(maximumForm,input.Length);
            var node = forms;
            foreach (char c in input)
            {
                if (!node.Next.TryGetValue(c, out var next)) node.Next[c] = next = new();
                node = next;
            }
            node.Forms.Add((reading, option, penalty, incomplete));
        }
        internal IEnumerable<PinyinEdge> Match(string raw, int start, PinyinSpellingOptions options, CancellationToken cancellation, int afterEnd = -1, MatchCache? cache = null)
        {
            cache ??= new MatchCache();
            cache.Prepare(raw,options,maximumForm);
            FormMatch[] FormsAt(int from)
            {
                if(cache.Forms.TryGetValue(from,out var found))return found;
                var matches=new List<FormMatch>();var form=forms;
                for(int at=from;at<raw.Length&&form.Next.TryGetValue(raw[at],out form!);at++)
                    foreach(var mapping in form.Forms)
                        if(options.HasFlag(PinyinSpellingOptions.DoublePinyin)==(mapping.Option==PinyinSpellingOptions.DoublePinyin)&&
                            (mapping.Option&options)==mapping.Option&&(!mapping.Incomplete||at+1==raw.Length))
                            matches.Add(new(mapping.Reading,mapping.Penalty,mapping.Incomplete,at+1));
                return cache.Forms[from]=matches.ToArray();
            }
            var best = new Dictionary<(int Node, int End), double>();
            var output = new List<PinyinEdge>();
            void Walk(int node, int position, double penalty, int[] ends, bool incomplete = false)
            {
                cancellation.ThrowIfCancellationRequested();
                if (best.TryGetValue((node, position), out double previous) && previous <= penalty) return;
                best[(node, position)] = penalty;
                if (position > afterEnd) foreach (var entry in syllables.Values(node)) output.Add(new(entry, position, penalty, ends, incomplete));
                if (position >= raw.Length) return;
                int from = position;
                if (raw[from] == '\'') { if (ends.Length == 0 || ++from >= raw.Length) return; }
                foreach (var mapping in FormsAt(from))
                {
                    if (!syllables.TryNext(node, mapping.Reading, out var child)) continue;
                    var nextEnds = new int[ends.Length + 1]; ends.CopyTo(nextEnds, 0); nextEnds[^1] = mapping.End;
                    Walk(child, mapping.End, penalty + mapping.Penalty, nextEnds, incomplete || mapping.Incomplete);
                }
            }
            Walk(0, start, 0, []);
            return output.GroupBy(e => (e.Entry, e.End)).Select(g => g.MinBy(e => e.Penalty)!)
                .OrderBy(e => e.End).ThenBy(e => e.Penalty).ThenByDescending(e => e.Entry.Frequency)
                .ThenBy(e => e.Entry.Text, StringComparer.Ordinal);
        }
    }
}
