using System;
using System.Collections.Generic;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceLearningStore
    {
        private readonly object _ioGate = new();
        private readonly SentenceLearningSnapshot.Accumulator _accumulator = new();
        // At most 1 MiB of UTF-16 journal text, plus parsed records. Large
        // journals bypass this cache; the 16 MiB input bound remains unchanged.
        private const int ParseCacheCharacters = 512 * 1024;
        private string _parsedData;
        private Journal _parsedJournal;
        private long _parsedCharacters;

        internal long ParsedCharacters { get { lock (_ioGate) return _parsedCharacters; } }
        internal long ReplayedEvents { get { lock (_ioGate) return _accumulator.ReplayedEvents; } }
        internal void Refresh() { lock (_ioGate) RefreshCore(); }
        internal void Confirm(IEnumerable<SentenceLearningEvent> events) { lock (_ioGate) ConfirmCore(events); }
        internal SentenceLearningEvent[] Entries() { lock (_ioGate) return EntriesCore(); }
        internal bool UndoLast() { lock (_ioGate) return UndoLastCore(); }
        internal void Clear() { lock (_ioGate) ClearCore(); }

        private Journal ReadJournal(string data)
        {
            if (_parsedJournal != null && string.Equals(_parsedData, data, StringComparison.Ordinal))
                return _parsedJournal;
            Journal parsed = Parse(data);
            _parsedCharacters += data.Length;
            RememberJournal(data, string.Empty, parsed);
            return parsed;
        }

        private void RememberJournal(string data, string addition, Journal journal)
        {
            if (data.Length + addition.Length <= ParseCacheCharacters)
            {
                _parsedData = addition.Length == 0 ? data : data + addition;
                _parsedJournal = journal;
            }
            else
            {
                _parsedData = null;
                _parsedJournal = null;
            }
        }
    }
}
