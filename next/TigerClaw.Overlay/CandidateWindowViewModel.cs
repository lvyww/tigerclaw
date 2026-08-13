using System;

namespace TigerClaw.Overlay
{
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
    }
}
