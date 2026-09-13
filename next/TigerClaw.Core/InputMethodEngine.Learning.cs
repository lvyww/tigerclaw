using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private SentenceCandidate _learningBaseline;
        private readonly List<SentenceLearningEvent> _pendingLearning = new();
        private readonly List<SentenceLearningEvent> _readyLearning = new();
        private string _learningOutput;
        private SentenceLearningStore _learningStore;
        private string _learningMode = "", _learningSchema;
        private int _learningConfigVersion = -1;
        private bool _learningContextValid = true;

        private void ConfigureSentenceLearning()
        {
            // Configuration metadata is cached. No per-key file enumeration,
            // journal reads or disk writes; refreshes run in the store worker.
            string schema = _state.GetCurrentSchema();
            if (_learningConfigVersion != _state.ConfigVersion || _learningSchema != schema)
            {
                bool changedScheme = _learningSchema != schema;
                _learningConfigVersion = _state.ConfigVersion; _learningSchema = schema;
                string mode = "";
                if (_state.GetSentenceLearningEnabled() && _state.IsSentenceInputActive())
                    mode = "sentence-v1|dup=" + (_state.GetSentenceAllowDuplicateSingleCharacters() ? "1" : "0") +
                          "|optimal=" + _state.GetSentenceOptimalCodeHighFreqLimit().ToString(CultureInfo.InvariantCulture) + "|whitelist=" + SentenceLearning.ConfigurationHash(_state.GetSentenceFullCodeWhitelistText());
                if (mode != _learningMode || changedScheme) { _pendingLearning.Clear(); _learningBaseline = null; }
                _learningMode = mode;
                if (mode.Length == 0) _learningStore = null;
                else
                {
                    try
                    {
                    string directory = _state.GetCurrentCodeTablePath();
                    // No fallback to the root: records belong to ONE input scheme.
                    string path = string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(schema) ||
                        !string.Equals(new DirectoryInfo(directory).Name, schema, StringComparison.OrdinalIgnoreCase)
                        ? null : Path.Combine(directory, ".tigerclaw-learning-v1.log");
                    if (path == null) _learningStore = null;
                    else if (_learningStore == null || !string.Equals(_learningStore.Path, path, StringComparison.OrdinalIgnoreCase))
                        _learningStore = new SentenceLearningStore(path);
                    }
                    catch (Exception) { _learningStore = null; }
                }
            }
            _learningStore?.RefreshAsync();
            _sentenceInputDecoder?.SetLearning(_learningStore?.Snapshot, _learningMode);
        }
        private void CaptureSentenceLearning(int index)
        {
            if (!_sentenceTabSelectionPending || !_learningContextValid || _learningBaseline == null || !_state.GetSentenceLearningEnabled()) return;
            var candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            if (index < 0 || index >= candidates.Length || _sentenceDecodeResult.RawCode != _sentenceRawBuffer.ToString()) return;
            int floor = Math.Max(_sentenceCommittedRawLength, ActiveSentenceLockedPrefix?.RawCode.Length ?? 0);
            _pendingLearning.AddRange(SentenceLearning.Diff(_sentenceRawBuffer.ToString(), _learningBaseline, candidates[index], floor, _sentenceDecodeResult.LearningMode));
            _learningBaseline = null;
        }
        private void ReleaseSentenceLearning(string text, int rawEnd, string output)
        {
            foreach (var e in _pendingLearning)
                if (e.RawEnd <= rawEnd && e.TextEnd <= text.Length && text.Substring(e.TextStart, e.TextEnd - e.TextStart) == e.Text) _readyLearning.Add(e);
            _pendingLearning.RemoveAll(e => e.RawEnd <= rawEnd);
            if (_readyLearning.Count > 0) _learningOutput = output;
        }
        internal SentenceLearningEvent[] TakeSentenceLearning(KeyEngineResult result, out SentenceLearningStore store)
        {
            lock (_lock)
            {
                store = _learningStore;
                var events = _state.GetSentenceLearningEnabled() && result != null && !string.IsNullOrEmpty(result.TextToOutput) &&
                    string.Equals(result.TextToOutput, _learningOutput, StringComparison.Ordinal)
                    ? _readyLearning.Where(e => e.Mode == _learningMode).ToArray() : Array.Empty<SentenceLearningEvent>();
                _readyLearning.Clear(); _learningOutput = null; return events;
            }
        }
    }
}
