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
                    mode = "整句|单字重码=" + (_state.GetSentenceAllowDuplicateSingleCharacters() ? "1" : "0") +
                          "|最优码限制=" + _state.GetSentenceOptimalCodeHighFreqLimit().ToString(CultureInfo.InvariantCulture) + "|全码白名单=" + _state.GetSentenceFullCodeWhitelistText();
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
                        ? null : Path.Combine(directory, "自学习-虎爪.txt");
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
            if (!_learningContextValid || !_state.GetSentenceLearningEnabled()) return;
            var candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            if (index < 0 || index >= candidates.Length || _sentenceDecodeResult.RawCode != _sentenceRawBuffer.ToString()) return;
            SentenceCandidate selected = candidates[index];
            SentenceCandidate before = _learningBaseline ?? (index > 0 ? candidates[0] : null);
            // A fresh non-first tap/number is an explicit correction without
            // Tab. Do not teach Direct-to-Direct dictionary-rank preferences.
            if (SentenceFusionPreference.IsDirect(before) && SentenceFusionPreference.IsDirect(selected))
            {
                before = candidates.Take(index).Where(SentenceFusionPreference.IsComposedOnly)
                    .OrderByDescending(c => c.FinalScore).FirstOrDefault();
            }
            static bool LegalSource(SentenceCandidate c) => c != null &&
                (c.Source == SentenceCandidateSource.Direct || c.Source == SentenceCandidateSource.Composed ||
                 c.Source == (SentenceCandidateSource.Direct | SentenceCandidateSource.Composed));
            if (!LegalSource(before) || !LegalSource(selected)) { _learningBaseline = null; return; }
            int floor = Math.Max(_sentenceCommittedRawLength, ActiveSentenceLockedPrefix?.RawCode.Length ?? 0);
            string raw = _sentenceRawBuffer.ToString();
            var events = SentenceLearning.SelectionEvents(raw, before, selected, floor,
                _sentenceDecodeResult.LearningMode, _learningStore?.Snapshot,
                text => _sentenceInputDecoder?.IsSupplementalFragment(text) ?? false);
            SentenceLearning.PlanCorrectionLevels(events, _learningStore?.Snapshot, raw, before, selected);
            _pendingLearning.AddRange(events);
            _learningBaseline = null;
        }

        private void ReleaseSentenceLearning(string text, int rawEnd, string output)
        {
            foreach (var e in _pendingLearning)
            {
                if (e.Mode == _learningMode && !SentenceLearning.LegacyPairMode(e.Mode) &&
                    e.RawEnd <= rawEnd && e.TextStart >= 0 && e.TextEnd >= e.TextStart && e.TextEnd <= text.Length &&
                    text.Substring(e.TextStart, e.TextEnd - e.TextStart) == e.Text)
                    _readyLearning.Add(e);
            }
            _pendingLearning.RemoveAll(e => e.RawEnd <= rawEnd);
            if (_readyLearning.Count > 0) _learningOutput = output;
        }

        internal SentenceLearningEvent[] TakeSentenceLearning(KeyEngineResult result, out SentenceLearningStore store)
        {
            lock (_lock)
            {
                if (_pinyinLearningOutput != null)
                {
                    store = _pinyinLearning;
                    var pinyin = _state.GetFullPinyinLearningEnabled() && result?.TextToOutput == _pinyinLearningOutput
                        ? _pinyinReadyLearning : Array.Empty<SentenceLearningEvent>();
                    _pinyinReadyLearning = Array.Empty<SentenceLearningEvent>(); _pinyinLearningOutput = null;
                    return pinyin;
                }
                store = _learningStore;
                var events = _state.GetSentenceLearningEnabled() && result != null && !string.IsNullOrEmpty(result.TextToOutput) &&
                    string.Equals(result.TextToOutput, _learningOutput, StringComparison.Ordinal)
                    ? _readyLearning.Where(e => e.Mode == _learningMode && !SentenceLearning.LegacyPairMode(e.Mode)).ToArray() : Array.Empty<SentenceLearningEvent>();
                _readyLearning.Clear(); _learningOutput = null; return events;
            }
        }
    }
}
