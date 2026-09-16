using System;
using System.Collections.Generic;
using System.Globalization;

namespace TigerClaw.Core
{
    internal sealed class SentenceIsolationPenalty
    {
        public static readonly SentenceIsolationPenalty None = new SentenceIsolationPenalty
        {
            RankThreshold = 0,
            Lambda = 0.0,
            UseLogRank = false
        };

        public static SentenceIsolationPenalty CreateDefault()
        {
            return new SentenceIsolationPenalty
            {
                RankThreshold = 3000,
                Lambda = 2.0,
                UseLogRank = false
            };
        }

        public int RankThreshold { get; set; }
        public double Lambda { get; set; }
        public bool UseLogRank { get; set; }

        public bool Enabled
        {
            get { return RankThreshold > 0 && Lambda > 0.0; }
        }

        public double Apply(string text, ISentenceLanguageModel model)
        {
            if (!Enabled || model == null || string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            List<string> characters = SplitCharacters(text);
            double penalty = 0.0;
            for (int index = 0; index < characters.Count; index++)
            {
                string current = characters[index];
                int rank = SentenceCharacterRanks.GetRank(current);
                if (rank <= RankThreshold)
                {
                    continue;
                }

                bool leftHit = index > 0 && model.HasObservedBigram(characters[index - 1], current);
                bool rightHit = index + 1 < characters.Count &&
                                model.HasObservedBigram(current, characters[index + 1]);
                if (leftHit || rightHit)
                {
                    continue;
                }

                penalty += Weight(rank);
            }

            return penalty;
        }

        public double Apply(
            string text,
            SentencePathBoundary boundary,
            ISentenceLanguageModel model,
            double protectedFactor,
            int protectedMinimumCodeLength)
        {
            if (!Enabled || model == null || string.IsNullOrEmpty(text) || boundary == null)
            {
                return 0.0;
            }

            var boundaries = new List<SentencePathBoundary>();
            for (SentencePathBoundary current = boundary; current != null; current = current.Previous)
            {
                boundaries.Add(current);
            }
            boundaries.Reverse();

            double penalty = 0.0;
            double previousWeight = 0.0;
            string previousCharacter = null;
            int textStart = 0;
            foreach (SentencePathBoundary current in boundaries)
            {
                int textEnd = Math.Min(text.Length, Math.Max(textStart, current.TextLength));
                string edge = text.Substring(textStart, textEnd - textStart);
                double factor = current.ProtectsRareCharacter &&
                    current.CodeLength >= protectedMinimumCodeLength
                    ? Math.Clamp(protectedFactor, 0.0, 1.0)
                    : 1.0;
                foreach (string character in SplitCharacters(edge))
                {
                    int rank = SentenceCharacterRanks.GetRank(character);
                    double currentWeight = rank > RankThreshold ? Weight(rank) * factor : 0.0;
                    bool linked = previousCharacter != null &&
                        (previousWeight > 0.0 || currentWeight > 0.0) &&
                        model.HasObservedBigram(previousCharacter, character);
                    if (previousWeight > 0.0 && linked) penalty -= previousWeight;
                    previousWeight = currentWeight > 0.0 && !linked ? currentWeight : 0.0;
                    if (previousWeight > 0.0) penalty += previousWeight;
                    previousCharacter = character;
                }
                textStart = textEnd;
            }

            // Defensive fallback for a legacy or externally-created boundary
            // chain which ends before the candidate text.
            if (textStart < text.Length)
            {
                foreach (string character in SplitCharacters(text.Substring(textStart)))
                {
                    int rank = SentenceCharacterRanks.GetRank(character);
                    double currentWeight = rank > RankThreshold ? Weight(rank) : 0.0;
                    bool linked = previousCharacter != null &&
                        (previousWeight > 0.0 || currentWeight > 0.0) &&
                        model.HasObservedBigram(previousCharacter, character);
                    if (previousWeight > 0.0 && linked) penalty -= previousWeight;
                    previousWeight = currentWeight > 0.0 && !linked ? currentWeight : 0.0;
                    if (previousWeight > 0.0) penalty += previousWeight;
                    previousCharacter = character;
                }
            }
            return penalty;
        }

        public double Weight(int rank)
        {
            if (!UseLogRank)
            {
                return Lambda;
            }

            double ratio = Math.Max(rank, RankThreshold + 1) / (double)RankThreshold;
            return Lambda * Math.Log(ratio);
        }

        public string Label()
        {
            return "T" + RankThreshold.ToString(CultureInfo.InvariantCulture) +
                   "_L" + Lambda.ToString("0.##", CultureInfo.InvariantCulture) +
                   (UseLogRank ? "_log" : "_const");
        }

        private static List<string> SplitCharacters(string text)
        {
            var characters = new List<string>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                characters.Add(enumerator.GetTextElement());
            }

            return characters;
        }
    }
}
