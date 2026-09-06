using System;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlaySessionState
    {
        private OverlayUiState _current;

        public OverlayUiChangeFlags Update(OverlayUiState next)
        {
            next = next ?? new OverlayUiState();

            if (_current == null)
            {
                _current = Clone(next);
                return OverlayUiChangeFlags.All;
            }

            OverlayUiChangeFlags changes = OverlayUiChangeFlags.None;

            if (_current.IsOff != next.IsOff ||
                _current.IsNativeHook != next.IsNativeHook ||
                _current.IsChinese != next.IsChinese ||
                !StringEquals(_current.StatusText, next.StatusText) ||
                _current.HideStatusBar != next.HideStatusBar)
            {
                changes |= OverlayUiChangeFlags.Status;
            }

            if (_current.CandidateVisible != next.CandidateVisible ||
                _current.IsNativeHook != next.IsNativeHook ||
                !StringEquals(_current.InputCode, next.InputCode) ||
                _current.HideCandidateItems != next.HideCandidateItems ||
                _current.ShowInputCodeInCandidateWindow != next.ShowInputCodeInCandidateWindow ||
                _current.CandidateExpandDelayMs != next.CandidateExpandDelayMs ||
                _current.AnnotationExpandDelayMs != next.AnnotationExpandDelayMs ||
                _current.ShowCandidateIndex != next.ShowCandidateIndex ||
                _current.VerticalCandidates != next.VerticalCandidates ||
                _current.SelectedCandidateIndex != next.SelectedCandidateIndex ||
                !StringArrayEquals(_current.Candidates, next.Candidates) ||
                !StringArrayEquals(_current.CandidateAnnotations, next.CandidateAnnotations))
            {
                changes |= OverlayUiChangeFlags.Content;
            }

            if (!StringEquals(_current.ThemeName, next.ThemeName) ||
                !StringEquals(_current.FontName, next.FontName) ||
                _current.FontSize != next.FontSize ||
                _current.VerticalCandidates != next.VerticalCandidates)
            {
                changes |= OverlayUiChangeFlags.Style;
            }

            if (_current.CaretX != next.CaretX ||
                _current.CaretY != next.CaretY ||
                _current.IsNativeHook != next.IsNativeHook ||
                _current.CandidateVisible != next.CandidateVisible ||
                _current.HideCandidateItems != next.HideCandidateItems ||
                _current.ShowInputCodeInCandidateWindow != next.ShowInputCodeInCandidateWindow ||
                !StringEquals(_current.InputCode, next.InputCode))
            {
                changes |= OverlayUiChangeFlags.Position;
            }

            if (_current.CandidateAnchorRevision != next.CandidateAnchorRevision)
            {
                changes |= OverlayUiChangeFlags.CandidateAnchor | OverlayUiChangeFlags.Position;
            }

            if (_current.SoundSeq != next.SoundSeq ||
                _current.SoundVk != next.SoundVk ||
                _current.SoundVolumePercent != next.SoundVolumePercent)
            {
                changes |= OverlayUiChangeFlags.Sound;
            }

            _current = Clone(next);
            return changes;
        }

        private static OverlayUiState Clone(OverlayUiState state)
        {
            return new OverlayUiState
            {
                IsOff = state.IsOff,
                IsNativeHook = state.IsNativeHook,
                IsChinese = state.IsChinese,
                StatusText = state.StatusText ?? string.Empty,
                CandidateVisible = state.CandidateVisible,
                InputCode = state.InputCode ?? string.Empty,
                Candidates = CloneArray(state.Candidates),
                SelectedCandidateIndex = state.SelectedCandidateIndex,
                CompositionState = state.CompositionState,
                CaretX = state.CaretX,
                CaretY = state.CaretY,
                CaretHeight = state.CaretHeight,
                CandidateAnchorRevision = state.CandidateAnchorRevision,
                VerticalCandidates = state.VerticalCandidates,
                ShowCandidateIndex = state.ShowCandidateIndex,
                HideCandidateItems = state.HideCandidateItems,
                CodeMasking = state.CodeMasking ?? string.Empty,
                ThemeName = state.ThemeName ?? string.Empty,
                FontName = state.FontName ?? string.Empty,
                FontSize = state.FontSize,
                CandidateAnnotations = CloneArray(state.CandidateAnnotations),
                HideStatusBar = state.HideStatusBar,
                SoundSeq = state.SoundSeq,
                SoundVk = state.SoundVk,
                SoundVolumePercent = state.SoundVolumePercent,
                ShowInputCodeInCandidateWindow = state.ShowInputCodeInCandidateWindow,
                CandidateExpandDelayMs = state.CandidateExpandDelayMs,
                AnnotationExpandDelayMs = state.AnnotationExpandDelayMs
            };
        }

        private static string[] CloneArray(string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<string>();
            }

            var copy = new string[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        private static bool StringArrayEquals(string[] left, string[] right)
        {
            left = left ?? Array.Empty<string>();
            right = right ?? Array.Empty<string>();
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!StringEquals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StringEquals(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        }
    }
}


