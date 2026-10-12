using System;
using System.Text;
using System.Collections.Generic;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class CandidateTextFormatter
    {
        public CandidateDisplayMode GetDisplayMode(OverlayUiState state, bool showCandidates)
        {
            if (!IsCandidateVisible(state))
            {
                return CandidateDisplayMode.Hidden;
            }

            bool hasCandidates = HasCandidates(state);
            bool showInputCode = ShouldShowInputCode(state);
            bool hasInputCode = HasInputCode(state);
            bool useHiddenCandidateMode = !showCandidates || state.HideCandidateItems;

            if (useHiddenCandidateMode)
            {
                return showInputCode && hasInputCode
                    ? CandidateDisplayMode.CodeOnly
                    : CandidateDisplayMode.Hidden;
            }

            if (!hasCandidates)
            {
                return hasInputCode
                    ? CandidateDisplayMode.InputOnly
                    : CandidateDisplayMode.Hidden;
            }

            if (showInputCode && hasInputCode)
            {
                return CandidateDisplayMode.CodeAndCandidates;
            }

            return CandidateDisplayMode.CandidatesOnly;
        }

        public bool ShouldShowCandidateWindow(OverlayUiState state)
        {
            return ShouldShowCandidateWindow(state, showCandidates: true);
        }

        public bool ShouldShowCandidateWindow(OverlayUiState state, bool showCandidates)
        {
            return GetDisplayMode(state, showCandidates) != CandidateDisplayMode.Hidden;
        }

        public CandidateWindowViewModel BuildViewModel(OverlayUiState state, bool showCandidates, bool includeAnnotations, bool above = false)
        {
            CandidateDisplayMode mode = GetDisplayMode(state, showCandidates);
            if (mode == CandidateDisplayMode.Hidden)
            {
                return CandidateWindowViewModel.Hidden;
            }

            var candidateRanges = new List<CandidateAnnotationSpan>();
            var annotationRanges = new List<CandidateAnnotationSpan>();
            string escapedInputCode = EscapeCandidateForDisplay(state.InputCode ?? string.Empty);
            string displayText = BuildDisplayText(
                state,
                mode,
                escapedInputCode,
                includeAnnotations,
                above,
                candidateRanges, annotationRanges,
                out int selectionStart,
                out int selectionLength);

            return new CandidateWindowViewModel
            {
                CandidateRanges = candidateRanges, AnnotationRanges = annotationRanges,
                Mode = mode,
                IsVertical = state.VerticalCandidates,
                DisplayText = displayText,
                SelectionStart = selectionStart,
                SelectionLength = selectionLength,
                CodeStart = (mode == CandidateDisplayMode.CodeAndCandidates || mode == CandidateDisplayMode.InputOnly)
                    ? (!state.VerticalCandidates || above || mode == CandidateDisplayMode.InputOnly ? 0 : displayText.Length - escapedInputCode.Length) : -1,
                CodeLength = (mode == CandidateDisplayMode.CodeAndCandidates || mode == CandidateDisplayMode.InputOnly)
                    ? escapedInputCode.Length : 0
            };
        }

        private static bool IsCandidateVisible(OverlayUiState state)
        {
            return state != null && state.CandidateVisible;
        }

        private static bool HasCandidates(OverlayUiState state)
        {
            return (state.Candidates?.Length ?? 0) > 0;
        }

        // Matches InputMethodEngine.CompositionState.CnSentence.
        private const int SentenceCompositionState = 5;

        private static int ResolveHighlightIndex(OverlayUiState state)
        {
            int selectedIndex = state.SelectedCandidateIndex;
            if (state.CompositionState == SentenceCompositionState && selectedIndex == 0)
            {
                return -1;
            }

            return selectedIndex;
        }

        private static bool HasInputCode(OverlayUiState state)
        {
            return !string.IsNullOrEmpty(state.InputCode);
        }

        internal static bool ShouldShowInputCode(OverlayUiState state)
        {
            return state != null &&
                   (state.IsNativeHook || state.ShowInputCodeInCandidateWindow);
        }

        private static string BuildDisplayText(
            OverlayUiState state,
            CandidateDisplayMode mode,
            string escapedInputCode,
            bool includeAnnotations,
            bool above,
            List<CandidateAnnotationSpan> candidateRanges, List<CandidateAnnotationSpan> annotationRanges,
            out int selectionStart,
            out int selectionLength)
        {
            selectionStart = -1;
            selectionLength = 0;
            if (mode == CandidateDisplayMode.CodeOnly || mode == CandidateDisplayMode.InputOnly)
            {
                return escapedInputCode;
            }

            string[] candidates = state.Candidates ?? Array.Empty<string>();
            string[] annotations = includeAnnotations ? (state.CandidateAnnotations ?? Array.Empty<string>()) : Array.Empty<string>();
            bool showIndex = state.ShowCandidateIndex;
            bool showCode = mode == CandidateDisplayMode.CodeAndCandidates;
            int selectedIndex = ResolveHighlightIndex(state);
            var sb = new StringBuilder(256);

            if (state.VerticalCandidates)
            {
                BuildVerticalDisplayText(
                    sb,
                    escapedInputCode,
                    candidates,
                    annotations,
                    showIndex,
                    showCode,
                    selectedIndex,
                    above,
                    candidateRanges, annotationRanges,
                    ref selectionStart,
                    ref selectionLength);
            }
            else
            {
                BuildHorizontalDisplayText(
                    sb,
                    escapedInputCode,
                    candidates,
                    annotations,
                    showIndex,
                    showCode,
                    selectedIndex,
                    candidateRanges, annotationRanges,
                    ref selectionStart,
                    ref selectionLength);
            }

            return sb.ToString();
        }

        private static void BuildVerticalDisplayText(
            StringBuilder sb,
            string escapedInputCode,
            string[] candidates,
            string[] annotations,
            bool showIndex,
            bool showCode,
            int selectedIndex,
            bool above,
            List<CandidateAnnotationSpan> candidateRanges, List<CandidateAnnotationSpan> annotationRanges,
            ref int selectionStart,
            ref int selectionLength)
        {
            if (showCode && above)
            {
                sb.Append(escapedInputCode);
                if (candidates.Length > 0)
                {
                    sb.Append('\n');
                }
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }

                AppendCandidate(sb, candidates, annotations, above ? candidates.Length - i - 1 : i,
                    showIndex, selectedIndex, candidateRanges, annotationRanges, ref selectionStart, ref selectionLength);
            }
            if (showCode && !above)
            {
                if (candidates.Length > 0) sb.Append('\n');
                sb.Append(escapedInputCode);
            }
        }

        private static void BuildHorizontalDisplayText(
            StringBuilder sb,
            string escapedInputCode,
            string[] candidates,
            string[] annotations,
            bool showIndex,
            bool showCode,
            int selectedIndex,
            List<CandidateAnnotationSpan> candidateRanges, List<CandidateAnnotationSpan> annotationRanges,
            ref int selectionStart,
            ref int selectionLength)
        {
            if (showCode)
            {
                sb.Append(escapedInputCode);
                if (candidates.Length > 0)
                {
                    sb.Append(' ');
                }
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                AppendCandidate(sb, candidates, annotations, i, showIndex, selectedIndex, candidateRanges, annotationRanges, ref selectionStart, ref selectionLength);
            }
        }

        private static void AppendCandidate(
            StringBuilder sb,
            string[] candidates,
            string[] annotations,
            int index,
            bool showIndex,
            int selectedIndex,
            List<CandidateAnnotationSpan> candidateRanges, List<CandidateAnnotationSpan> annotationRanges,
            ref int selectionStart,
            ref int selectionLength)
        {
            int candidateStart = sb.Length;
            sb.Append(EscapeCandidateForDisplay(candidates[index]));
            if (showIndex && index > 0)
            {
                const string digits = "\u2070\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079";
                foreach (char digit in (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    sb.Append(digits[digit - '0']);
            }

            string annotation = index < annotations.Length ? annotations[index] : string.Empty;
            if (!string.IsNullOrEmpty(annotation))
            {
                int annotationStart = sb.Length;
                sb.Append('\u3014');
                sb.Append(EscapeCandidateForDisplay(annotation));
                sb.Append('\u3015');
                annotationRanges.Add(new CandidateAnnotationSpan { Start = annotationStart, Length = sb.Length - annotationStart });
            }

            candidateRanges.Add(new CandidateAnnotationSpan { Start = candidateStart, Length = sb.Length - candidateStart });
            if (index == selectedIndex)
            {
                selectionStart = candidateStart;
                selectionLength = sb.Length - candidateStart;
            }
        }

        private static string EscapeCandidateForDisplay(string candidate)
        {
            return (candidate ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
