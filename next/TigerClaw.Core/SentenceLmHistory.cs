using System;

namespace TigerClaw.Core
{
    // Most recent token first. A fivegram needs four tokens, including BOS until
    // it leaves the window. Value states survive incremental/locked-prefix reuse.
    internal readonly record struct SentenceLmHistory(uint A, uint B, uint C, uint D, int Count)
    {
        public SentenceLmHistory Append(uint token) => new(token, A, B, C, Math.Min(4, Count + 1));
    }

    internal interface ISentenceHistoryLanguageModel : ISentenceLanguageModel
    {
        SentenceLmHistory BeginHistory { get; }
        double Step(SentenceLmHistory history, string target, out SentenceLmHistory next);
    }

}
