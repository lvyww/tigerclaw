using System.Text;

namespace TigerClaw.Pinyin;

internal sealed record PinyinChoice(Candidate Path, string Text, int Consumed, bool Whole, double Reward = 0, bool Literal = false, string Annotation = "")
{
    internal string Identity => Text + "\0" + Consumed;
}

// UI-independent composition. The host owns threading and publishes generation-
// checked snapshots; workers never mutate this object.
internal sealed class PinyinSession
{
    private Lexicon? lexicon;
    private PinyinSpellingOptions spelling;
    private PinyinChoice[] extras = [];
    private bool preferExtras;
    private IReadOnlySet<string>? firstCharacters;
    internal double SentenceConfidence { get; private set; }
    private PinyinPreferences preferences = PinyinPreferences.Empty;
    internal string Raw { get; private set; } = "";
    internal int Cursor { get; private set; }
    internal long Generation { get; private set; }
    internal long AppliedGeneration { get; private set; } = -1;
    internal List<Candidate> Locks { get; } = new();
    internal Candidate? Prefix => Locks.LastOrDefault();
    internal int LockedEnd => Prefix?.Segments.LastOrDefault()?.End ?? 0;
    internal string LockedText => Prefix?.Text ?? "";
    internal bool SentenceMenu { get; private set; }
    internal int Selected { get; private set; }
    internal PinyinChoice[] Choices { get; private set; } = [];
    internal Candidate[] Sentences { get; private set; } = [];
    internal bool HasComplete { get; private set; }
    internal bool ExactComplete { get; private set; }
    internal string Display => LockedText + Raw[LockedEnd..];
    internal int DisplayCursor => LockedText.Length + Cursor - LockedEnd;
    internal List<(string Raw, string Text, string[] Tokens)> Corrections { get; } = new();

    internal PinyinSession ForkForDecode()
    {
        var copy=new PinyinSession { Raw=Raw, Cursor=Cursor, Generation=Generation, SentenceMenu=SentenceMenu };
        copy.Locks.AddRange(Locks);
        return copy;
    }
    internal void AdoptPrepared(PinyinSession prepared)
    {
        if(Generation!=prepared.Generation||Raw!=prepared.Raw||LockedEnd!=prepared.LockedEnd)return;
        lexicon=prepared.lexicon;spelling=prepared.spelling;firstCharacters=prepared.firstCharacters;
        preferences=prepared.preferences;extras=prepared.extras;preferExtras=prepared.preferExtras;
        Choices=prepared.Choices;Sentences=prepared.Sentences;HasComplete=prepared.HasComplete;
        ExactComplete=prepared.ExactComplete;SentenceConfidence=prepared.SentenceConfidence;
        AppliedGeneration=prepared.AppliedGeneration;Selected=0;
    }

    internal void Reset(string raw = "")
    {
        Raw = raw; Cursor = raw.Length; Locks.Clear(); Corrections.Clear();
        extras = []; preferExtras = false;
        Choices = []; Sentences = []; SentenceMenu = false; HasComplete = ExactComplete = false;
        Changed();
    }
    internal void Invalidate() { AppliedGeneration = -1; Changed(); }
    private void Changed() { Generation++; Selected = 0; }
    internal void Insert(char value)
    {
        if (Raw.Length >= 4096) return;
        Raw = Raw.Insert(Cursor, value.ToString()); Cursor++; Changed();
    }
    internal void MoveCursor(int value, bool absolute = false)
    { Cursor = Math.Clamp(absolute ? value : Cursor + value, LockedEnd, Raw.Length); }
    internal void Backspace()
    {
        if (Cursor == LockedEnd && Locks.Count > 0)
        {
            int oldEnd = LockedEnd;
            Locks.RemoveAt(Locks.Count - 1); Cursor = oldEnd;
            Corrections.Clear(); Changed(); return;
        }
        if (Cursor <= LockedEnd) return;
        Raw = Raw.Remove(--Cursor, 1); Corrections.Clear(); Changed();
    }
    internal void Delete()
    {
        if (Cursor >= Raw.Length) return;
        Raw = Raw.Remove(Cursor, 1); Corrections.Clear(); Changed();
    }
    internal void EditSyllable(bool forward, bool delete)
    {
        if (!forward && Cursor == LockedEnd && Locks.Count > 0) { Backspace(); return; }
        var boundaries = new SortedSet<int> { LockedEnd, Raw.Length };
        foreach (var segment in Sentences.FirstOrDefault()?.Segments ?? [])
        {
            if (segment.RawEnds != null) foreach (int end in segment.RawEnds) boundaries.Add(end);
            else
            {
                int position = segment.Start;
                foreach (string token in segment.Tokens ?? [])
                {
                    while (position < Raw.Length && Raw[position] == '\'') position++;
                    position += Lexicon.RawReading(token).Length;
                    boundaries.Add(Math.Min(position, segment.End));
                }
                boundaries.Add(segment.End);
            }
        }
        int target = forward ? boundaries.FirstOrDefault(x => x > Cursor, Raw.Length) : boundaries.LastOrDefault(x => x < Cursor, LockedEnd);
        target = Math.Clamp(target, LockedEnd, Raw.Length);
        if (delete)
        {
            int begin = Math.Min(Cursor, target), end = Math.Max(Cursor, target);
            Raw = Raw.Remove(begin, end - begin); Cursor = begin; Corrections.Clear(); Changed();
        }
        else Cursor = target;
    }
    internal void MoveSelection(int delta)
    { if (Choices.Length != 0) Selected = (Selected + delta % Choices.Length + Choices.Length) % Choices.Length; }
    internal void SelectIndex(int index) { Selected = Math.Clamp(index, 0, Math.Max(0, Choices.Length - 1)); }
    internal void ToggleMenu() { SentenceMenu = !SentenceMenu; RebuildMenu(); }

    internal void Apply(long generation, DecodeResult result, Func<string, string, double>? reward = null, Lexicon? lexicon = null, PinyinSpellingOptions spelling = PinyinSpellingOptions.None, PinyinPreferences? preferences = null, IReadOnlySet<string>? firstCharacters = null, bool deferMenu = false)
    {
        if (generation != Generation) return;
        this.lexicon = lexicon;
        this.spelling = result.PreferExactSpelling ? spelling & ~(PinyinSpellingOptions.Abbreviations | PinyinSpellingOptions.Typos) : spelling;
        this.firstCharacters = firstCharacters;
        this.preferences = preferences ?? PinyinPreferences.Empty;
        Sentences = result.Candidates;
        if (reward != null)
            Sentences = Sentences.OrderByDescending(c => c.Score + reward(Raw, c.Text))
                .ThenByDescending(c => c.Frequency).ThenBy(c => c.Text, StringComparer.Ordinal).ToArray();
        var pins = this.preferences.MatchingPins(Lexicon.NormalizeCode(Raw));
        if (pins.Length > 0)
            Sentences = Sentences.OrderByDescending(c => pins.Any(p => c.Text.StartsWith(p.Text, StringComparison.Ordinal) &&
                c.Segments.Any(s => s.End == p.Code.Length && string.Concat(c.Segments.Where(x => x.End <= s.End).Select(x => x.Text)) == p.Text))).ToArray();
        // Relative mass within the retained candidate pool, not a calibrated
        // probability of correctness. Include learning in the same displayed order.
        var scores = Sentences.Select(c => c.Score + (reward?.Invoke(Raw, c.Text) ?? 0)).ToArray();
        double maximum = scores.Length == 0 ? 0 : scores.Max();
        SentenceConfidence = scores.Length == 0 || !double.IsFinite(maximum) ? 0 :
            Math.Exp(scores[0] - maximum) / scores.Sum(score => Math.Exp(score - maximum));
        HasComplete = result.Consumed == Raw.Length && Sentences.Length > 0;
        ExactComplete = HasComplete && Sentences[0].Segments.All(s => !s.Incomplete);
        AppliedGeneration = generation;
        if (!deferMenu) RebuildMenu();
    }

    internal void SetExtras(IEnumerable<PinyinChoice> choices, bool prefer = false)
    { extras = choices.ToArray(); preferExtras = prefer; RebuildMenu(); }
    internal void ApplySpecial(IEnumerable<string> texts, string annotation)
    {
        Sentences = []; HasComplete = ExactComplete = false; AppliedGeneration = Generation;
        extras = texts.Select(t => new PinyinChoice(new(LockedText + t, 0, 0, []), t, Raw.Length, true, Literal: true, Annotation: annotation)).ToArray();
        preferExtras = true; RebuildMenu();
    }
    private int[] TokenEnds(Segment segment)
    {
        if (segment.RawEnds != null) return segment.RawEnds;
        // Legacy exact edges and terminal completions have no explicit offsets.
        // Other spellings carry RawEnds from the spelling lattice.
        int position = segment.Start;
        return (segment.Tokens ?? []).Select(token =>
        {
            if (position < segment.End && Raw[position] == '\'') position++;
            position = Math.Min(segment.End, position + Lexicon.RawReading(token).Length);
            return position;
        }).ToArray();
    }

    private void RebuildMenu()
    {
        Selected = 0;
        int floor = LockedEnd, textFloor = LockedText.Length;
        int liveStart = floor < Raw.Length && Raw[floor] == '\'' ? floor + 1 : floor;
        // A consonant-only abbreviation cannot by itself justify a new cut in
        // the middle of a full syllable. Genuine abbreviated cuts are retained
        // from the decoder's winning path below.
        var suffixes = lexicon?.MenuSuffixes(Lexicon.NormalizeCode(Raw), spelling & ~PinyinSpellingOptions.Abbreviations, !ExactComplete);
        // Honor intentional interjections/partial tails retained by the decoder,
        // without allowing every lower-ranked consonant split to create a menu cut.
        var decodedEnds = (Sentences.FirstOrDefault()?.Segments ?? []).SelectMany(TokenEnds).ToHashSet();
        bool LegalEnd(int end) => end == Raw.Length || suffixes == null || suffixes[end] || decodedEnds.Contains(end);
        var legalExtras = extras.Where(c => LegalEnd(c.Consumed)).ToArray();
        var result = new List<PinyinChoice>();
        if (floor == 0)
            foreach (var phrase in preferences.GetPhrases(Lexicon.NormalizeCode(Raw)))
                result.Add(new(new(phrase.Text, 0, 0, []), phrase.Text, Raw.Length, true, Literal: true, Annotation: "短语"));
        if (preferExtras) result.AddRange(legalExtras.Where(c => c.Whole && c.Annotation.StartsWith("英文", StringComparison.Ordinal)));
        if (HasComplete)
        {
            int count = SentenceMenu || SentenceConfidence >= 0.90 ? 1 : 2;
            foreach (var sentence in Sentences.Take(count))
                result.Add(new(sentence, sentence.Text[textFloor..], Raw.Length, true));
        }
        if (SentenceMenu)
        {
            foreach (var candidate in Sentences.Skip(HasComplete ? 1 : 0))
            {
                int end = candidate.Segments.LastOrDefault()?.End ?? 0;
                if (end > floor) result.Add(new(candidate, candidate.Text[textFloor..], end, end == Raw.Length));
            }
        }
        else
        {
            var words = new List<(PinyinChoice Choice, int Length, double Frequency)>();
            var locked = Prefix?.Segments ?? [];
            if (lexicon != null)
            {
                // Enumerate the lexicon directly: a useful homophone need not be
                // represented in the retained Beam/Top50 sentence candidates.
                foreach (var edge in lexicon.MenuEdges(Lexicon.NormalizeCode(Raw), liveStart, spelling)
                    .Where(e => LegalEnd(e.End) && (e.RawEnds == null || e.RawEnds.All(LegalEnd)))
                    .GroupBy(e => e.Entry).Select(g => g.OrderBy(e => e.Penalty).ThenByDescending(e => e.End).First()))
                {
                    var entry = edge.Entry;
                    if (firstCharacters != null && !firstCharacters.Contains(entry.Characters[0])) continue;
                    var segment = new Segment(entry.Code, entry.Text, liveStart, edge.End, entry.Tokens,
                        edge.Penalty, edge.RawEnds, edge.Incomplete, entry.Bonus);
                    var path = new Candidate(LockedText + entry.Text, 0, Math.Log(1.0 + entry.Frequency), locked.Append(segment).ToArray());
                    words.Add((new(path, entry.Text, edge.End, edge.End == Raw.Length), entry.Characters.Length, entry.Frequency));
                }
            }
            // Preserve terminal-syllable completions and callers without a lexicon.
            // These are actual decoder segments, never a synthetic full sentence.
            foreach (var sentence in Sentences)
            {
                var first = sentence.Segments.FirstOrDefault(s => s.Start >= floor);
                if (first == null) continue;
                if (lexicon != null && !first.Incomplete) continue;
                var path = new Candidate(LockedText + first.Text, sentence.Score, sentence.Frequency, locked.Append(first).ToArray());
                double frequency = lexicon?.Edges(first.Code, 0).Where(e => e.Entry.Text == first.Text && e.Entry.Code == first.Code)
                    .Select(e => (double)e.Entry.Frequency).DefaultIfEmpty(0).Max() ?? sentence.Frequency;
                words.Add((new(path, first.Text, first.End, first.End == Raw.Length), first.Text.EnumerateRunes().Count(), frequency));
                if (first.Tokens is { Length: > 1 })
                {
                    string character = first.Text.EnumerateRunes().First().ToString();
                    int end = TokenEnds(first)[0];
                    var segment = new Segment(Lexicon.RawReading(first.Tokens[0]), character, first.Start, end,
                        [first.Tokens[0]], RawEnds: [end], Incomplete: first.Incomplete);
                    words.Add((new(new(LockedText + character, sentence.Score, sentence.Frequency, locked.Append(segment).ToArray()),
                        character, end, end == Raw.Length), 1, 0));
                }
            }
            // English/mixed prefixes share the word section so a large Chinese
            // homophone list cannot truncate them out of the candidate budget.
            words.AddRange(legalExtras.Where(c => c.Annotation.StartsWith("英文", StringComparison.Ordinal))
                .Select(c => (c, c.Text.EnumerateRunes().Count(), c.Path.Frequency)));
            result.AddRange(words.OrderByDescending(w => w.Length).ThenByDescending(w => w.Frequency)
                .ThenBy(w => w.Choice.Path.Segments.Last().SpellingPenalty)
                .ThenByDescending(w => w.Choice.Consumed).ThenBy(w => w.Choice.Text, StringComparer.Ordinal)
                .Select(w => w.Choice));
        }
        result.AddRange(legalExtras);
        Choices = result.DistinctBy(c => c.Identity)
            .OrderByDescending(c => preferences.IsPinned(Lexicon.NormalizeCode(Raw[..c.Consumed]), c.Path.Text))
            .Take(200).ToArray();
    }

    // Returns a whole committed sentence, or null when only a prefix was locked.
    internal string? Confirm(int index, bool learn)
    {
        if (AppliedGeneration != Generation || index < 0 || index >= Choices.Length) return null;
        var selected = Choices[index];
        if (selected.Literal && selected.Consumed == Raw.Length) return selected.Path.Text;
        // A single-character selection inside a sentence is not standalone
        // character learning. Incomplete syllable completions are not exact codes.
        bool single = selected.Text.EnumerateRunes().Count() == 1;
        bool standalone = LockedEnd == 0 && selected.Consumed == Raw.Length &&
            selected.Path.Segments.All(s => !s.Incomplete);
        if (learn && !selected.Literal && Sentences.Length > 0 && (!single || standalone))
        {
            var best = Sentences[0];
            string baseline = string.Concat(best.Segments.Where(s => s.End <= selected.Consumed).Select(s => s.Text));
            if (selected.Path.Text != baseline && !best.Text.StartsWith(selected.Path.Text, StringComparison.Ordinal))
            {
                var tokens = selected.Path.Segments.SelectMany(s => s.Tokens ?? []).ToArray();
                Corrections.Add((Lexicon.NormalizeCode(Raw[..selected.Consumed]), selected.Path.Text, tokens));
            }
        }
        if (selected.Consumed == Raw.Length) return selected.Path.Text;
        Locks.Add(selected.Path); Cursor = Raw.Length; Changed(); return null;
    }
}
