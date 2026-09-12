using System;

namespace TigerClaw.Overlay
{
    // No WPF/Win32 calls: the identical production arithmetic is tested on Linux.
    internal struct CandidateRect : IEquatable<CandidateRect>
    {
        public int Left, Top, Right, Bottom;
        public CandidateRect(int left, int top, int right, int bottom)
        { Left = left; Top = top; Right = right; Bottom = bottom; }
        public bool Equals(CandidateRect b)
        { return Left == b.Left && Top == b.Top && Right == b.Right && Bottom == b.Bottom; }
    }

    internal struct CandidatePlacementEnvironment : IEquatable<CandidatePlacementEnvironment>
    {
        public long Revision, Owner, Foreground, Focus, Monitor;
        public int ProcessId, Dpi;
        public string InstanceId;
        public bool StartMenu, UncertainOwner;
        public CandidateRect Work, OwnerBounds, ForegroundBounds;
        public bool Equals(CandidatePlacementEnvironment b)
        {
            return string.Equals(InstanceId, b.InstanceId, StringComparison.Ordinal) &&
                UncertainOwner == b.UncertainOwner && Revision == b.Revision && Owner == b.Owner && Foreground == b.Foreground &&
                Focus == b.Focus && Monitor == b.Monitor && ProcessId == b.ProcessId && Dpi == b.Dpi &&
                StartMenu == b.StartMenu && Work.Equals(b.Work) && OwnerBounds.Equals(b.OwnerBounds) &&
                ForegroundBounds.Equals(b.ForegroundBounds);
        }
    }

    // Value type deliberately: prepare on a copy; accept only after a current
    // successfully positioned frame has been made visible. Ordinary commit/cancel
    // resets the composition anchor, not this input-environment-owned memory.
    internal struct CandidateOrientation
    {
        private bool _valid, _above;
        private int _referenceY, _referenceHeight;
        private CandidatePlacementEnvironment _environment;
        public bool Above => _valid && _above;
        public bool Valid => _valid;
        public int ReferenceY => _referenceY;
        public void Reset() { _valid = false; _above = false; }
        public static int Tolerance(int height, int referenceHeight, int dpi)
        {
            long pixels = (3L * (dpi > 0 ? dpi : 96) + 48) / 96;
            return (int)Math.Max(0, Math.Min(pixels, Math.Min((long)height, referenceHeight) / 4));
        }
        public bool TryPlace(int caretX, int caretY, int caretHeight,
            CandidatePlacementEnvironment env, int width, int height, out int x, out int y)
        {
            x = y = 0;
            var work = env.Work;
            if (caretHeight <= 0 || width <= 0 || height <= 0 ||
                work.Right <= work.Left || work.Bottom <= work.Top) return false;
            if (!_valid || !_environment.Equals(env))
            {
                _above = false;
                _referenceY = caretY;
                _referenceHeight = caretHeight;
            }
            else
            {
                int epsilon = Tolerance(caretHeight, _referenceHeight, env.Dpi);
                long delta = (long)caretY - _referenceY;
                if (delta < -epsilon)
                {
                    _above = false; // Re-evaluate, not unconditionally move below.
                    _referenceY = caretY;
                    _referenceHeight = caretHeight;
                }
                else if (delta > epsilon)
                {
                    _referenceY = caretY;
                    _referenceHeight = caretHeight;
                }
                // Do not chase every jitter sample; cumulative drift must count.
            }
            const int gap = 5;
            long below = (long)caretY + gap;
            long above = (long)caretY - caretHeight - gap - height;
            // Keep TigerClaw's existing two-pixel bottom/right reserves.
            long bottom = (long)work.Bottom - 2;
            bool fitsBelow = below >= work.Top && below + height <= bottom;
            bool fitsAbove = above >= work.Top && above + height <= bottom;
            if (!(_above && fitsAbove))
            {
                if (fitsBelow) _above = false;
                else if (fitsAbove) _above = true;
                else
                {
                    long roomAbove = Math.Max(0, Math.Min((long)caretY - caretHeight - gap, bottom) - work.Top);
                    long roomBelow = Math.Max(0, bottom - Math.Max(below, work.Top));
                    _above = roomAbove > roomBelow;
                }
            }
            x = Clamp(caretX, work.Left, Math.Max((long)work.Left, (long)work.Right - width - 2));
            y = Clamp(_above ? above : below, work.Top, Math.Max((long)work.Top, bottom - height));
            _environment = env;
            _valid = true;
            return true;
        }
        internal static int Clamp(long value, long minimum, long maximum)
        { return (int)Math.Max(minimum, Math.Min(value, maximum)); }
    }
}
