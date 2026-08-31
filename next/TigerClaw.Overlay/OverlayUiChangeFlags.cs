using System;

namespace TigerClaw.Overlay
{
    [Flags]
    internal enum OverlayUiChangeFlags
    {
        None = 0,
        Status = 1,
        Content = 2,
        Style = 4,
        Position = 8,
        Sound = 16,
        CandidateAnchor = 32,
        All = Status | Content | Style | Position | Sound | CandidateAnchor
    }
}
