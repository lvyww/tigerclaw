using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace TigerClaw.Pinyin;

internal sealed record Entry(string Text, string Code, int Frequency, string[] Characters, string[]? Tokens = null, double Bonus = 0)
{
    internal int CompletionPrefix {get;} = Tokens is {Length:>0}
        ? Code.Length - (Tokens[^1].Length - Tokens[^1].LastIndexOf('/') - 1) + 1 : int.MaxValue;
}
internal sealed record Segment(string Code, string Text, int Start, int End, string[]? Tokens = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double SpellingPenalty = 0, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int[]? RawEnds = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Incomplete = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double WordBonus = 0);
internal sealed record Candidate(string Text, double Score, double Frequency, Segment[] Segments);
internal sealed record DecodeResult(Candidate[] Candidates, int Consumed, string Tail, long Expansions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PreferExactSpelling = false);

// Shared joint-token search used by both Core and the frozen evaluator.
// Shape-code eligibility, automatic commit and production IPC stay in the host.
internal sealed class Decoder : IDisposable
{
    private sealed record Path(Path? Previous, Segment Segment);
    private sealed record State(string Text, string Previous2, string Previous1, double Score, double Frequency, Path? Path);
    private sealed class Bucket
    {
        internal Dictionary<(string Text, string A, string B), State> Values = new();
        internal State[]? Frozen;
        private double _floor = double.NegativeInfinity;
        private bool shared;
        internal Bucket Fork() => new() { Values=Values, Frozen=Frozen, _floor=_floor, shared=true };
        internal bool MayAccept(double score) => score >= _floor;
        internal void Add(State item, int beam)
        {
            var key = (item.Text, item.Previous2, item.Previous1);
            if (Values.TryGetValue(key, out var old) && Compare(old, item) <= 0) return;
            if(shared){Values=new(Values);shared=false;}
            Values[key] = item;
            Frozen = null;
            if (Values.Count > beam * 4L) Trim(beam * 2);
        }
        private void Trim(int count)
        {
            var kept = Ordered(Values.Values).Take(count).ToArray();
            if (kept.Length == count) _floor = kept[^1].Score;
            Values = kept.ToDictionary(x => (x.Text, x.Previous2, x.Previous1));
            shared=false;
        }
        internal State[] Finish(int beam)
        {
            if (Frozen != null) return Frozen;
            Trim(beam);
            return Frozen = Ordered(Values.Values).ToArray();
        }
    }
    private readonly Lexicon lexicon;
    private readonly Lexicon.MatchCache spellingCache = new();
    private readonly PinyinContextCache contextCache = new();
    private readonly IPinyinLanguageModel model;
    private readonly int beam;
    private readonly bool ownsModel;
    private readonly bool ownsQuery;
    private string cached = "";
    private Candidate? cachedPrefix;
    private List<Bucket> states = new();
    private readonly string? requiredText;
    private readonly IReadOnlySet<string>? firstCharacters;
    private readonly PinyinSpellingOptions spellingOptions;
    private readonly Decoder? precise;
    private readonly IIndexedPinyinLanguageModel? indexed;
    private sealed record PreparedEntry(uint[] Tokens, double[] Suffix, double Frequency);
    private Dictionary<Entry, PreparedEntry> prepared = new(ReferenceEqualityComparer.Instance);
    private PreparedEntry Prepare(Entry entry)
    {
        if (prepared.TryGetValue(entry, out var result)) return result;
        var tokens = (entry.Tokens ?? entry.Characters).Select(indexed!.Index).ToArray();
        var suffix = new double[Math.Max(0, tokens.Length - 2)];
        for (int i = 2; i < tokens.Length; i++) suffix[i-2] = indexed.LogProbability(tokens[i-2], tokens[i-1], tokens[i]) + 2.0;
        return prepared[entry] = new(tokens, suffix, Math.Log(1.0 + entry.Frequency));
    }
    internal Decoder(Lexicon lexicon, IPinyinLanguageModel model, int beam, bool ownsModel = false, string? requiredText = null, PinyinSpellingOptions spellingOptions = PinyinSpellingOptions.None, IReadOnlySet<string>? firstCharacters = null)
    {
        if (beam < 1 || beam > 100000) throw new ArgumentOutOfRangeException(nameof(beam));
        this.model = !ownsModel && model is Kenlm native ? native.CreateQuery() : model;
        ownsQuery = !ReferenceEquals(this.model, model);
        indexed = this.model as IIndexedPinyinLanguageModel;
        this.lexicon = lexicon; this.beam = beam; this.ownsModel = ownsModel; this.requiredText = requiredText; this.spellingOptions = spellingOptions; this.firstCharacters = firstCharacters;
        if ((spellingOptions & (PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Typos)) != 0)
            precise = new Decoder(lexicon, this.model, beam, requiredText: requiredText,
                spellingOptions: spellingOptions & ~(PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Typos), firstCharacters: firstCharacters);
        if (precise != null) precise.prepared = prepared;
    }
    private PinyinSearchProfile? profile;
    internal void EnableProfiling()
    { profile=new();if(model is Kenlm native)native.ProfileEnabled=true;precise?.EnableProfiling(); }
    internal (double LookupMs,double ScoreMs,double LoopMs,long Calls,long Misses,double NativeMs) ProfileMetrics
    {
        get
        {
            double factor=1000.0/System.Diagnostics.Stopwatch.Frequency;
            var child=precise?.ProfileMetrics??default;
            var native=model as Kenlm;
            return ((profile?.LookupTicks??0)*factor+child.LookupMs,(profile?.ScoreTicks??0)*factor+child.ScoreMs,
                (profile?.LoopTicks??0)*factor+child.LoopMs,(native?.ProfileCalls??0)+child.Calls,
                (native?.ProfileMisses??0)+child.Misses,(native?.ProfileNativeTicks??0)*128*factor+child.NativeMs);
        }
    }
    private static IOrderedEnumerable<State> Ordered(IEnumerable<State> items) => items
        .OrderByDescending(s => s.Score).ThenByDescending(s => s.Frequency).ThenBy(s => s.Text, StringComparer.Ordinal);
    private static int Compare(State a, State b)
    {
        int n = b.Score.CompareTo(a.Score);
        if (n == 0) n = b.Frequency.CompareTo(a.Frequency);
        return n != 0 ? n : StringComparer.Ordinal.Compare(a.Text, b.Text);
    }
    internal void Reset() { cached = ""; states.Clear(); precise?.Reset(); }
    internal DecodeResult Decode(string input, int limit = 50, bool incremental = true,
        Candidate? prefix = null, bool completeLastSyllable = false, CancellationToken cancellation = default)
    {
        profile?.Clear();if(profile!=null&&model is Kenlm native)native.ResetProfile();
        string savedRaw=cached;var savedPrefix=cachedPrefix;var savedStates=states;
        states=states.Select(b=>b.Fork()).ToList();
        try
        {
            if (precise != null)
            {
                var exact = precise.Decode(input, limit, incremental, prefix, false, cancellation);
                // A valid fully spelled path wins over abbreviations/corrections.
                // Bare consonant interjections (n/m/hm/ng) must not monopolize
                // short abbreviations such as nh or nhao. Keep them in an
                // otherwise fully spelled sentence (at least two full syllables).
                bool regular = exact.Consumed == input.Length && exact.Candidates.Take(1).Any(c =>
                {
                    var readings = c.Segments.SelectMany(s => s.Tokens ?? []).ToArray();
                    int full = readings.Count(t => Lexicon.RawReading(t).Any(ch => "aeiouv".Contains(ch)));
                    return full == readings.Length || full >= 2;
                });
                if (regular) return exact with { PreferExactSpelling = true };
            }
            return DecodeCore(input, limit, incremental, prefix, completeLastSyllable, cancellation);
        }
        catch (OperationCanceledException)
        { cached=savedRaw;cachedPrefix=savedPrefix;states=savedStates;throw; }
        catch { Reset(); throw; }
    }

    private DecodeResult DecodeCore(string input, int limit, bool incremental, Candidate? prefix,
        bool completeLastSyllable, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string raw = input.ToLowerInvariant();
        if (raw.Length > 4096 || raw.Any(c => c != '\'' && (c < 'a' || c > 'z')))
            throw new ArgumentException("Use full pinyin a-z and apostrophe only; maximum 4096 keys");
        if (raw.StartsWith('\'') || raw.Contains("''")) throw new ArgumentException("Empty forced syllable");
        bool hasSeparator = raw.Contains('\'');
        int prefixEnd = prefix?.Segments.LastOrDefault()?.End ?? 0;
        if (prefixEnd > raw.Length) throw new ArgumentException("Locked prefix exceeds input");
        bool reuse = incremental && ReferenceEquals(cachedPrefix, prefix) && !hasSeparator && !cached.Contains('\'') && cached.Length > 0;
        cachedPrefix = prefix;
        int oldLength = cached.Length;
        long expansions = 0;
        double[] incrementBuffer = new double[64];
        if (reuse && raw.Length <= oldLength && cached.StartsWith(raw, StringComparison.Ordinal))
        {
            states.RemoveRange(raw.Length + 1, states.Count - raw.Length - 1);
        }
        else
        {
            bool append = reuse && raw.StartsWith(cached, StringComparison.Ordinal);
            int from = 0;
            if (append)
            {
                from = Math.Max(0, oldLength + 1 - lexicon.MaximumLength);
                while (states.Count <= raw.Length) states.Add(new());
            }
            else
            {
                oldLength = -1;
                states = Enumerable.Range(0, raw.Length + 1).Select(_ => new Bucket()).ToList();
                string a = "\u0002", b = a; double score = 0, prefixFrequency = 0; Path? path = null;
                if (prefix != null)
                    foreach (var segment in prefix.Segments)
                    {
                        foreach (string token in segment.Tokens ?? segment.Text.EnumerateRunes().Select(r => r.ToString()).ToArray())
                        { score += model.LogProbability(a, b, token) + 2; a = b; b = token; }
                        var entry = lexicon.Edges(segment.Code, 0).Select(e => e.Entry).FirstOrDefault(e => e.Text == segment.Text && e.Code == segment.Code);
                        score += (entry?.Bonus ?? 0) - segment.SpellingPenalty;
                        prefixFrequency += Math.Log(1.0 + (entry?.Frequency ?? 0));
                        path = new(path, segment with { WordBonus = entry?.Bonus ?? 0 });
                    }
                states[prefixEnd].Add(new(prefix?.Text ?? "", a, b, score, prefixFrequency, path), beam);
                from = prefixEnd;
            }
            for (int position = from; position < raw.Length; position++)
            {
                cancellation.ThrowIfCancellationRequested();
                var current = states[position].Finish(beam);
                if (current.Length == 0) continue;
                if (raw[position] == '\'')
                {
                    foreach (var item in current) states[position + 1].Add(item, beam);
                    continue;
                }
                // Trigram probabilities depend on the last two model tokens.
                // Preserve each group's score order so its losing suffix can be skipped.
                var contexts = current.GroupBy(s => (s.Previous2, s.Previous1))
                    .Select(g => (Items: g.ToArray(), Previous2: g.Key.Previous2, Previous1: g.Key.Previous1, A: indexed?.Index(g.Key.Previous2) ?? 0, B: indexed?.Index(g.Key.Previous1) ?? 0)).ToArray();
                var contextBases = contexts.Select(c=>c.Items[0].Score).ToArray();
                var contextScores = indexed == null ? null : contextCache.Get(indexed, contexts.Select(c=>(c.A,c.B)).ToArray(), cancellation);
                long lookupStart=profile==null?0:System.Diagnostics.Stopwatch.GetTimestamp();
                var edges=lexicon.SpelledEdges(raw, position, spellingOptions, cancellation, oldLength, spellingCache);
                if(profile!=null){edges=edges.ToArray();profile.LookupTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-lookupStart;}
                foreach (var edge in edges)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (edge.End <= oldLength) continue;
                    var entry = edge.Entry;
                    long scoreStart=profile==null?0:System.Diagnostics.Stopwatch.GetTimestamp();
                    var fast = indexed == null ? null : Prepare(entry);
                    if (incrementBuffer.Length < entry.Characters.Length) incrementBuffer = new double[entry.Characters.Length];
                    var increments = incrementBuffer.AsSpan(0, entry.Characters.Length);
                    // Trie edges cannot cross an apostrophe. Earlier syllables/words
                    // remain legal: nihaoxi'an is not restricted to one edge before '.
                    var firstScores = fast == null ? null : contextScores!.First(fast.Tokens[0]);
                    var secondScores = fast == null || fast.Tokens.Length < 2 ? null : contextScores!.Second(fast.Tokens[0],fast.Tokens[1]);
                    var bestScores = fast == null || firstCharacters != null || requiredText != null ? null :
                        contextScores!.Best(contextBases,entry.Bonus,edge.Penalty,firstScores!,secondScores,fast.Suffix);
                    if(profile!=null)profile.ScoreTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-scoreStart;
                    long loopStart=profile==null?0:System.Diagnostics.Stopwatch.GetTimestamp();
                    var targetBucket=states[edge.End];
                    for (int contextIndex=0;contextIndex<contexts.Length;contextIndex++)
                    {
                        // Exact score of the highest-scoring state in this group.
                        // Equal scores still compete by frequency/source ordering.
                        if(bestScores!=null&&!targetBucket.MayAccept(bestScores[contextIndex])){expansions++;continue;}
                        var context=contexts[contextIndex];
                        string a = context.Previous2, b = context.Previous1;
                        var tokens = entry.Tokens ?? entry.Characters;
                        if (fast != null)
                        {
                            increments[0] = firstScores![contextIndex];
                            if (tokens.Length > 1) increments[1] = secondScores![contextIndex];
                            if (tokens.Length > 2) fast.Suffix.AsSpan().CopyTo(increments[2..]);
                            a = tokens.Length == 1 ? b : tokens[^2]; b = tokens[^1];
                        }
                        else for (int i = 0; i < tokens.Length; i++)
                        {
                            string c = tokens[i];
                            increments[i] = model.LogProbability(a, b, c) + 2.0;
                            a = b; b = c;
                        }
                        foreach (var item in context.Items)
                        {
                            if (firstCharacters != null && item.Text.Length == (prefix?.Text.Length ?? 0) && !firstCharacters.Contains(entry.Characters[0])) continue;
                            if (requiredText != null && !requiredText.StartsWith(item.Text + entry.Text, StringComparison.Ordinal)) continue;
                            double score = item.Score + entry.Bonus - edge.Penalty;
                            foreach (double increment in increments) score += increment;
                            expansions++;
                            // Later states in this context cannot beat the score floor.
                            if (!states[edge.End].MayAccept(score)) break;
                            states[edge.End].Add(new(item.Text + entry.Text, a, b, score,
                                item.Frequency + (fast?.Frequency ?? Math.Log(1.0 + entry.Frequency)),
                                new(item.Path, new(entry.Code, entry.Text, position, edge.End, entry.Tokens, edge.Penalty, edge.RawEnds, edge.Incomplete, entry.Bonus))), beam);
                        }
                    }
                    if(profile!=null)profile.LoopTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-loopStart;
                }
            }
        }
        cached = raw;
        int consumed = raw.Length;
        var completed = states[consumed].Finish(beam);
        // Completion is a display-only alternate lattice. Do not contaminate the
        // exact-input cache or change the ranking of any fully decoded input.
        if (completed.Length == 0 && completeLastSyllable && !spellingOptions.HasFlag(PinyinSpellingOptions.DoublePinyin))
        {
            var completions = new Bucket();
            for (int at = prefixEnd; at < raw.Length; at++)
            {
                cancellation.ThrowIfCancellationRequested();
                var previous = states[at].Finish(beam);
                if (previous.Length == 0) continue;
                foreach (var entry in lexicon.Completions(raw, at, cancellation))
                    foreach (var item in previous)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (firstCharacters != null && item.Text.Length == (prefix?.Text.Length ?? 0) && !firstCharacters.Contains(entry.Characters[0])) continue;
                        string a = item.Previous2, b = item.Previous1; double score = item.Score + entry.Bonus;
                        foreach (string token in entry.Tokens!)
                        { score += model.LogProbability(a, b, token) + 2; a = b; b = token; }
                        completions.Add(new(item.Text + entry.Text, a, b, score,
                            item.Frequency + Math.Log(1.0 + entry.Frequency),
                            new(item.Path, new(entry.Code, entry.Text, at, raw.Length, entry.Tokens, Incomplete: true, WordBonus: entry.Bonus))), beam);
                    }
            }
            completed = completions.Finish(beam);
        }
        if (completed.Length == 0)
        {
            // An incomplete final syllable is displayed as raw code, never expanded.
            consumed = 0;
            for (int at = raw.Length - 1; at > 0; at--)
                if (lexicon.ProperPrefix(raw[at..]) && states[at].Values.Count > 0)
                { consumed = at; completed = states[at].Finish(beam); break; }
        }
        var candidates = completed.Where(s => s.Text.Length > 0 && (requiredText == null || s.Text == requiredText)).Select(s =>
        {
            var segments = new List<Segment>();
            for (var p = s.Path; p != null; p = p.Previous) segments.Add(p.Segment);
            segments.Reverse();
            return new Candidate(s.Text, s.Score + model.LogProbability(s.Previous2, s.Previous1, "\u0003"), s.Frequency, segments.ToArray());
        }).OrderByDescending(c => c.Score).ThenByDescending(c => c.Frequency).ThenBy(c => c.Text, StringComparer.Ordinal)
          .DistinctBy(c => c.Text).Take(limit).ToArray();
        return new(candidates, consumed, raw[consumed..], expansions);
    }
    public void Dispose() { contextCache.Dispose(); precise?.Dispose(); if ((ownsModel || ownsQuery) && model is IDisposable d) d.Dispose(); }
}
