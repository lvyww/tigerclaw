using System;

namespace TigerClaw.Core
{
    [Flags]
    internal enum SentenceCandidateSource
    {
        None = 0,
        Direct = 1,
        Composed = 2
    }

    internal static class SentenceFusionPreference
    {
        internal const string DirectToken = "D";
        internal const string ComposedToken = "C";

        internal static string Mode(string sentenceMode) =>
            string.IsNullOrEmpty(sentenceMode) ? string.Empty : "fusion-v1|" + sentenceMode;

        internal static bool IsDirect(SentenceCandidate candidate) =>
            candidate != null && (candidate.Source & SentenceCandidateSource.Direct) != 0;

        internal static bool IsComposedOnly(SentenceCandidate candidate) =>
            candidate != null && candidate.Source == SentenceCandidateSource.Composed;

        internal static string PairCode(string raw, string directText, string composedText) =>
            "~f" + SentenceLearning.ConfigurationHash(
                (raw ?? string.Empty) + "\0D\0" + (directText ?? string.Empty) +
                "\0C\0" + (composedText ?? string.Empty));

        internal static double SignedScore(
            SentenceLearningSnapshot snapshot,
            string sentenceMode,
            string raw,
            string directText,
            string composedText)
        {
            if (snapshot == null || snapshot.IsEmpty || string.IsNullOrEmpty(sentenceMode))
                return 0.0;
            string mode = Mode(sentenceMode);
            string code = PairCode(raw, directText, composedText);
            double direct = snapshot.Score(mode, code, DirectToken, string.Empty);
            double composed = snapshot.Score(mode, code, ComposedToken, string.Empty);
            return direct - composed;
        }

        internal static SentenceLearningEvent CreateEvent(
            string sentenceMode,
            string raw,
            string directText,
            string composedText,
            bool directWins,
            int rawEnd)
        {
            if (string.IsNullOrEmpty(sentenceMode)) return null;
            return new SentenceLearningEvent
            {
                Mode = Mode(sentenceMode),
                Code = PairCode(raw, directText, composedText),
                Text = directWins ? DirectToken : ComposedToken,
                Context = string.Empty,
                RawStart = 0,
                RawEnd = Math.Max(0, rawEnd),
                TextStart = 0,
                TextEnd = 1
            };
        }
    }
}
