using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private bool _smartSpaceArmed;
        private string _smartSpaceSelectedText;
        private SentenceDecodeResult _smartDisplayResult;
        private int _smartDisplayLexiconVersion = -1;
        private bool IsSmartSentence => (_sentenceInputDecoder?.SmartMaxCodeLength ?? 0) > 0;
        private int SmartSelectionConfigMask => (_state.GetSecondCandidateSemicolon() ? 1 : 0) |
            (_state.GetThirdCandidateQuote() ? 2 : 0);

        private int CountSentenceCodes(string raw, int start = 0) => IsSmartSentence
            ? SmartSentenceSegmentation.CountLetters(raw, start)
            : Math.Max(0, raw.Length - start);

        private void RestoreSmartSpaceSelection()
        {
            if (!IsSmartSentence || _smartSpaceSelectedText == null)
            {
                return;
            }
            int index = Array.FindIndex(_sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>(),
                candidate => candidate.Text == _smartSpaceSelectedText);
            if (index >= 0)
            {
                _sentenceSelectedIndex = index;
                _sentenceManualSelectionGeneration = _sentenceGeneration;
            }
        }

        internal string GetSmartSentenceFormattedDisplay(Func<string, string> formatRaw)
        {
            lock (_lock)
            {
                if (!IsSmartSentence || _compositionState != CompositionState.CnSentence)
                {
                    return null;
                }
                string raw = _sentenceRawBuffer.ToString();
                return GetSmartSentenceDisplayCode(raw, GetUncommittedSentenceRawCode(raw), formatRaw);
            }
        }

        private string GetSmartSentenceDisplayCode(string raw, string remaining, Func<string, string> formatRaw = null)
        {
            SmartSentenceSegmentation.TryParse(raw, _sentenceInputDecoder.SmartMaxCodeLength, out var segments,
                _sentenceInputDecoder.SmartSelectionMask);
            if (segments.Count == 0)
            {
                return formatRaw == null ? remaining : formatRaw(remaining);
            }

            // Retain the last ranked path while an unfinished tail has no exact
            // candidate or the new asynchronous generation is still pending.
            // This is display-only: matching raw prefixes prevent edited segments
            // from borrowing text from an unrelated old generation.
            SentenceDecodeResult source = _smartDisplayLexiconVersion == _state.LexiconVersion
                ? _smartDisplayResult : null;
            SmartSentenceSegmentation.TryParse(source?.RawCode ?? string.Empty,
                _sentenceInputDecoder.SmartMaxCodeLength, out var sourceSegments,
                _sentenceInputDecoder.SmartSelectionMask);
            SentenceCandidate best = source?.Candidates?.FirstOrDefault(candidate =>
                candidate.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal));
            var boundaries = new Dictionary<int, SentencePathBoundary>();
            for (SentencePathBoundary boundary = best?.Boundary; boundary != null; boundary = boundary.Previous)
            {
                boundaries[boundary.RawLength] = boundary;
            }
            var parts = new List<string>();
            for (int index = 0; index < segments.Count; index++)
            {
                SmartSentenceSegment segment = segments[index];
                if (segment.End <= _sentenceCommittedRawLength)
                {
                    continue;
                }
                int start = Math.Max(segment.Start, _sentenceCommittedRawLength);
                string part = raw.Substring(start, segment.End - start).TrimEnd(' ');
                if (formatRaw != null)
                {
                    part = formatRaw(part);
                }
                if (segment.Closed && start == segment.Start && index < sourceSegments.Count)
                {
                    SmartSentenceSegment previous = sourceSegments[index];
                    if (previous.Start == segment.Start && previous.CodeEnd == segment.CodeEnd &&
                        previous.Rank == segment.Rank &&
                        string.Compare(raw, 0, source.RawCode, 0, segment.CodeEnd, StringComparison.OrdinalIgnoreCase) == 0 &&
                        boundaries.TryGetValue(previous.End, out SentencePathBoundary boundary))
                    {
                        int textStart = boundary.Previous?.TextLength ?? 0;
                        part = best.Text.Substring(textStart, boundary.TextLength - textStart);
                    }
                }
                parts.Add(part);
            }
            int parsedEnd = segments[segments.Count - 1].End;
            if (parsedEnd < raw.Length)
            {
                string tail = raw.Substring(Math.Max(parsedEnd, _sentenceCommittedRawLength));
                parts.Add(formatRaw == null ? tail : formatRaw(tail));
            }
            string display = string.Join(" ", parts);
            return raw.EndsWith(" ", StringComparison.Ordinal) && display.Length > 0 ? display + " " : display;
        }
    }
}
