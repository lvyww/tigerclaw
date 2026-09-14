using System.Runtime.Serialization;

namespace TigerClaw.Shared
{
    [DataContract]
    public sealed class OverlayUiState
    {
        [DataMember(Order = 1)]
        public bool IsOff { get; set; }

        [DataMember(Order = 2)]
        public bool IsChinese { get; set; }

        [DataMember(Order = 3)]
        public string StatusText { get; set; }

        [DataMember(Order = 4)]
        public bool CandidateVisible { get; set; }

        [DataMember(Order = 5)]
        public string InputCode { get; set; }

        [DataMember(Order = 6)]
        public string[] Candidates { get; set; }

        [DataMember(Order = 7)]
        public int CompositionState { get; set; }

        [DataMember(Order = 8)]
        public int CaretX { get; set; }

        [DataMember(Order = 9)]
        public int CaretY { get; set; }

        [DataMember(Order = 10)]
        public bool VerticalCandidates { get; set; }

        [DataMember(Order = 11)]
        public bool ShowCandidateIndex { get; set; }

        [DataMember(Order = 12)]
        public bool HideCandidateItems { get; set; }

        [DataMember(Order = 13)]
        public string CodeMasking { get; set; }

        [DataMember(Order = 14)]
        public string ThemeName { get; set; }

        [DataMember(Order = 15)]
        public string FontName { get; set; }

        [DataMember(Order = 16)]
        public double FontSize { get; set; }

        [DataMember(Order = 17)]
        public string[] CandidateAnnotations { get; set; }

        [DataMember(Order = 18)]
        public bool HideStatusBar { get; set; }

        [DataMember(Order = 19)]
        public long SoundSeq { get; set; }

        [DataMember(Order = 20)]
        public int SoundVk { get; set; }

        [DataMember(Order = 21)]
        public int SoundVolumePercent { get; set; }

        [DataMember(Order = 22)]
        public bool ShowInputCodeInCandidateWindow { get; set; }

        [DataMember(Order = 23)]
        public int CandidateExpandDelayMs { get; set; }

        [DataMember(Order = 24)]
        public int AnnotationExpandDelayMs { get; set; }

        [DataMember(Order = 25)]
        public bool IsNativeHook { get; set; }

        [DataMember(Order = 26)]
        public int SelectedCandidateIndex { get; set; } = -1;

        [DataMember(Order = 27)]
        public int CaretHeight { get; set; }

        [DataMember(Order = 28)]
        public long CandidateAnchorRevision { get; set; }
        [DataMember(Order = 29)]
        public long CandidateBackgroundUntil { get; set; }
        [DataMember(Order = 30)]
        public bool CandidateAnimationEnabled { get; set; } = true;
        [DataMember(Order = 33)]
        public int CandidateAnimationDurationMs { get; set; } = 200;
        public int CandidateResidenceDurationMs { get; set; } = 0;

        // Optional hint; old overlays still see CandidateVisible == false.
        [DataMember(Order = 34)]
        public bool CandidateHoldWhilePending { get; set; }

        // Optional display-continuity token. Missing/empty tokens cannot hold a frame.
        [DataMember(Order = 35)]
        public string CandidateFrameSession { get; set; }
    }
}


