namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        // Capture pending state and the projected candidate list under ONE engine
        // lock. Decode completion must not mix an old empty list with a new flag.
        public EngineUiSnapshot GetUiSnapshot(int pageSize, out bool sentenceDecodePending)
        {
            lock (_lock)
            {
                // Both helpers reenter the same Monitor; the worker cannot run
                // between the candidate projection and the pending-state read.
                EngineUiSnapshot snapshot = GetUiSnapshot(pageSize);
                sentenceDecodePending = IsSentenceDecodePending;
                return snapshot;
            }
        }
    }
}
