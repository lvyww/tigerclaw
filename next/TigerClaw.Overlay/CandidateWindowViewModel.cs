using System;
using System.Collections.Generic;

namespace TigerClaw.Overlay
{
    internal sealed class CandidateAnnotationSpan
    {
        public int Start, Length;
    }

    internal sealed class CandidateWindowViewModel
    {
        public static readonly CandidateWindowViewModel Hidden =
            new CandidateWindowViewModel
            {
                Mode = CandidateDisplayMode.Hidden,
                DisplayText = string.Empty,
                SelectionStart = -1
            };

        public CandidateDisplayMode Mode { get; set; }

        public bool IsVertical { get; set; }

        public string DisplayText { get; set; }

        public int SelectionStart { get; set; }

        public int SelectionLength { get; set; }
        public int CodeStart { get; set; } = -1;
        public int CodeLength { get; set; }
        public List<CandidateAnnotationSpan> CandidateRanges { get; set; } = new List<CandidateAnnotationSpan>();
        public List<CandidateAnnotationSpan> AnnotationRanges { get; set; } = new List<CandidateAnnotationSpan>();
    }
}
