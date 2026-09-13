namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        // Display continuity, not a TSF identity or placement-history key. A
        // random token also prevents a restarted Core from reusing an old frame.
        private string _candidateFrameSession = System.Guid.NewGuid().ToString("N");

        internal void InvalidateCandidateFrame()
        {
            lock (_lock)
            {
                _candidateFrameSession = System.Guid.NewGuid().ToString("N");
            }
        }

        public EngineUiSnapshot GetUiSnapshot(int pageSize, out bool sentenceDecodePending)
            => GetUiSnapshot(pageSize, out sentenceDecodePending, out _);

        // Capture ALL display-continuity data with the projected candidate list.
        // Latest-only readers need not observe the intervening hidden snapshot.
        public EngineUiSnapshot GetUiSnapshot(int pageSize, out bool sentenceDecodePending,
            out string candidateFrameSession)
        {
            lock (_lock)
            {
                EngineUiSnapshot snapshot = GetUiSnapshot(pageSize);
                sentenceDecodePending = IsSentenceDecodePending;
                candidateFrameSession = _candidateFrameSession;
                return snapshot;
            }
        }
    }
}
