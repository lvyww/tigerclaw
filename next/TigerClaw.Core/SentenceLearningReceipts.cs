using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    // In-memory, bounded and one-use. Nothing is persisted before a matching
    // frontend's successful document-edit receipt. Old frontends simply do not learn.
    internal sealed class SentenceLearningReceipts
    {
        private sealed class Pending
        {
            internal string Client;
            internal long Tick;
            internal SentenceLearningStore Store;
            internal SentenceLearningEvent[] Events;
        }
        private readonly object _gate = new();
        private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
        private readonly Func<long> _clock;
        internal SentenceLearningReceipts(Func<long> clock = null) { _clock = clock ?? (() => Environment.TickCount64); }
        internal string Issue(string client, SentenceLearningStore store, SentenceLearningEvent[] events)
        {
            if (string.IsNullOrEmpty(client) || client.Length > 128 || store == null || events.Length == 0) return null;
            lock (_gate)
            {
                Prune(); if (_pending.Count >= 128) return null;
                string token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending { Client = client, Tick = _clock(), Store = store, Events = events };
                return token;
            }
        }
        internal bool Acknowledge(string client, string token, bool success)
        {
            lock (_gate)
            {
                Prune();
                if (token == null || !_pending.TryGetValue(token, out var p) || !string.Equals(p.Client, client, StringComparison.Ordinal)) return false;
                _pending.Remove(token); // A failure/duplicate/retry cannot later turn into another learning event.
                if (!success) return false;
                foreach (var e in p.Events) e.Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return p.Store.ConfirmAsync(p.Events);
            }
        }
        private void Prune()
        {
            long now = _clock();
            foreach (var key in _pending.Where(p => now - p.Value.Tick > 30000 || now < p.Value.Tick).Select(p => p.Key).ToArray()) _pending.Remove(key);
        }
        internal void Cancel() { lock (_gate) _pending.Clear(); }
    }
}
