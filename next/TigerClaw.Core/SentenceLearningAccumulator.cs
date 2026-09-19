using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceLearningSnapshot
    {
        // Store-worker state only. Published partitions are never mutated.
        // Build(events, now) above remains the independent full-replay oracle.
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
            private long _at;
            private bool _initialized, _future;
            internal long ReplayedEvents { get; private set; }

            internal SentenceLearningSnapshot Update(IReadOnlyList<SentenceLearningEvent> events, long now)
            {
                try { return UpdateCore(events, now); }
                catch
                {
                    _groups.Clear(); _events.Clear(); _initialized = false; _future = false;
                    _snapshot = Empty;
                    throw;
                }
            }

            private SentenceLearningSnapshot UpdateCore(IReadOnlyList<SentenceLearningEvent> events, long now)
            {
                bool append = _initialized && now >= _at && (!_future || now == _at) && events.Count >= _events.Count;
                if (append)
                    for (int i = 0; i < _events.Count; i++)
                        if (!_events[i].Matches(events[i])) { append = false; break; }
                bool rescoreAll = !append || now != _at;
                if (!append)
                {
                    _groups.Clear();
                    _events.Clear();
                    _future = false;
                }
                var changed = new HashSet<string>(StringComparer.Ordinal);
                for (int i = _events.Count; i < events.Count; i++)
                {
                    var e = events[i];
                    _events.Add(EventStamp.From(e));
                    if (!Valid(e)) continue;
                    _future |= e.Time > now;
                    Apply(e, now);
                    changed.Add(e.Code);
                    ReplayedEvents++;
                }
                if (_groups.Count == 0) _snapshot = Empty;
                else if (rescoreAll || changed.Count != 0)
                {
                    var next = new SentenceLearningSnapshot();
                    foreach (var entry in _groups)
                    {
                        next._byCode[entry.Key] = !rescoreAll && !changed.Contains(entry.Key) &&
                            _snapshot._byCode.TryGetValue(entry.Key, out var existing)
                            ? existing : ScoreCode(entry.Value, now);
                    }
                    next._codes = next._byCode.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray();
                    _snapshot = next;
                }
                _at = now;
                _initialized = true;
                return _snapshot;
            }

            private static bool Valid(SentenceLearningEvent e) => e.Mode.Length > 0 && e.Mode.Length <= 512 &&
                e.Code.Length > 0 && e.Code.Length <= 128 && SentenceLearning.StaticText(e.Text) &&
                (e.Context.Length == 0 || SentenceLearning.Characters(e.Context) > 0) && SentenceLearning.Characters(e.Context) <= 2;

            private void Apply(SentenceLearningEvent e, long now)
            {
                if (!_groups.TryGetValue(e.Code, out var code)) _groups[e.Code] = code = new();
                var key = (e.Mode, e.Context);
                if (!code.Groups.TryGetValue(key, out var choices)) code.Groups[key] = choices = new(StringComparer.Ordinal);
                long time = Math.Min(now, e.Time);
                foreach (var entry in choices)
                {
                    var c = entry.Value;
                    c.Weight *= Math.Pow(2, -Math.Max(0, time - c.Time) / (30.0 * 86400));
                    c.Time = Math.Max(c.Time, time);
                    if (entry.Key != e.Text) c.Weight *= 0.25;
                }
                if (!choices.TryGetValue(e.Text, out var target)) choices[e.Text] = target = new Choice { Time = time };
                target.Weight = Math.Min(Math.Exp(3.5), target.Weight + 1);
                target.Count = Math.Min(3, target.Count + 1);
            }

            private static Dictionary<string, ModeIndex> ScoreCode(CodeState code, long now)
            {
                var summaries = new Dictionary<(string Mode, string Text), Summary>();
                foreach (var group in code.Groups)
                {
                    foreach (var entry in group.Value)
                    {
                        var c = entry.Value;
                        double weight = c.Weight * Math.Pow(2, -Math.Max(0, now - c.Time) / (30.0 * 86400));
                        var key = (group.Key.Mode, entry.Key);
                        if (!summaries.TryGetValue(key, out var summary)) summaries[key] = summary = new();
                        summary.Scores.Exact[group.Key.Context] = Math.Clamp(9 + 2 * Math.Log(Math.Max(0.001, weight)), 0, 16);
                        summary.Weight += weight;
                        summary.Count = Math.Min(3, summary.Count + c.Count);
                        if (group.Key.Context.Length > 0 && weight >= 0.1) summary.KnownContexts++;
                    }
                }
                var modes = new Dictionary<string, ModeIndex>(StringComparer.Ordinal);
                foreach (var entry in summaries)
                {
                    var summary = entry.Value;
                    summary.Scores.General = SentenceLearning.Characters(entry.Key.Text) > 1 && summary.Count >= 3 && summary.KnownContexts >= 2
                        ? 2 * Math.Min(1, summary.Weight / 3) : 0;
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
