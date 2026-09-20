using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceLearningSnapshot
    {
        // Store-worker state only. Published partitions are never mutated.
        // Build(events, now) remains the independent full-replay oracle.
        internal sealed class Accumulator
        {
            private sealed class CodeState
            {
                internal readonly Dictionary<(string Mode, string Context), Dictionary<string, Choice>> Groups = new();
            }
            private sealed record EventStamp(string Id, long Time, string Code, string Mode, string Text, string Context)
            {
                internal static EventStamp From(SentenceLearningEvent e) => new(e.Id, e.Time, e.Code, e.Mode, e.Text, e.Context);
                internal bool Matches(SentenceLearningEvent e) => Id == e.Id && Time == e.Time &&
                    Code == e.Code && Mode == e.Mode && Text == e.Text && Context == e.Context;
            }
            private readonly Dictionary<string, CodeState> _groups = new(StringComparer.Ordinal);
            private readonly List<EventStamp> _events = new();
            private SentenceLearningSnapshot _snapshot = Empty;
            private bool _initialized;
            internal long ReplayedEvents { get; private set; }

            internal SentenceLearningSnapshot Update(IReadOnlyList<SentenceLearningEvent> events, long now)
            {
                _ = now; // No time decay: only journal changes can change scores.
                try { return UpdateCore(events); }
                catch
                {
                    _groups.Clear(); _events.Clear(); _initialized = false;
                    _snapshot = Empty;
                    throw;
                }
            }

            private SentenceLearningSnapshot UpdateCore(IReadOnlyList<SentenceLearningEvent> events)
            {
                bool append = _initialized && events.Count >= _events.Count;
                if (append)
                    for (int i = 0; i < _events.Count; i++)
                        if (!_events[i].Matches(events[i])) { append = false; break; }

                if (!append)
                {
                    _groups.Clear();
                    _events.Clear();
                }

                var changed = new HashSet<string>(StringComparer.Ordinal);
                for (int i = _events.Count; i < events.Count; i++)
                {
                    var e = events[i];
                    _events.Add(EventStamp.From(e));
                    if (!Valid(e)) continue;
                    Apply(e);
                    changed.Add(e.Code);
                    ReplayedEvents++;
                }

                if (_groups.Count == 0) _snapshot = Empty;
                else if (!append || changed.Count != 0)
                {
                    var next = new SentenceLearningSnapshot();
                    foreach (var entry in _groups)
                    {
                        next._byCode[entry.Key] = append && !changed.Contains(entry.Key) &&
                            _snapshot._byCode.TryGetValue(entry.Key, out var existing)
                            ? existing : ScoreCode(entry.Value);
                    }
                    next._codes = next._byCode.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray();
                    _snapshot = next;
                }

                _initialized = true;
                return _snapshot;
            }

            private static bool Valid(SentenceLearningEvent e) => e.Mode.Length > 0 && e.Mode.Length <= 512 &&
                e.Code.Length > 0 && e.Code.Length <= 128 && SentenceLearning.StaticText(e.Text) &&
                (e.Context.Length == 0 || SentenceLearning.Characters(e.Context) > 0) && SentenceLearning.Characters(e.Context) <= 2;

            private void Apply(SentenceLearningEvent e)
            {
                if (!_groups.TryGetValue(e.Code, out var code)) _groups[e.Code] = code = new();
                var key = (e.Mode, e.Context);
                if (!code.Groups.TryGetValue(key, out var choices)) code.Groups[key] = choices = new(StringComparer.Ordinal);

                foreach (var entry in choices)
                    if (entry.Key != e.Text) entry.Value.Weight *= 0.25;

                if (!choices.TryGetValue(e.Text, out var target)) choices[e.Text] = target = new Choice();
                target.Weight = Math.Min(MaximumCorrectionLevel, target.Weight + 1);
            }

            private static Dictionary<string, ModeIndex> ScoreCode(CodeState code)
            {
                var summaries = new Dictionary<(string Mode, string Text), Summary>();
                foreach (var group in code.Groups)
                {
                    foreach (var entry in group.Value)
                    {
                        var key = (group.Key.Mode, entry.Key);
                        if (!summaries.TryGetValue(key, out var summary)) summaries[key] = summary = new();
                        summary.Scores.Exact[group.Key.Context] = ExactScore(entry.Value.Weight);
                        summary.Weight += entry.Value.Weight;
                    }
                }

                var modes = new Dictionary<string, ModeIndex>(StringComparer.Ordinal);
                foreach (var entry in summaries)
                {
                    var summary = entry.Value;
                    summary.Scores.General = GeneralScore(summary.Weight);
                    if (!modes.TryGetValue(entry.Key.Mode, out var index)) modes[entry.Key.Mode] = index = new();
                    index.Texts.Add(entry.Key.Text, summary.Scores);
                }
                foreach (var index in modes.Values)
                {
                    foreach (var entry in index.Texts)
                    {
                        for (int length = 1; length < entry.Key.Length; length++)
                        {
                            string prefix = entry.Key.Substring(0, length);
                            if (!index.Prefixes.TryGetValue(prefix, out var scores)) index.Prefixes[prefix] = scores = new();
                            scores.Include(entry.Value);
                        }
                    }
                }
                return modes;
            }
        }
    }
}
