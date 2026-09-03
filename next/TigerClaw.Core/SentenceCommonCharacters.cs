using System.Collections.Generic;

namespace TigerClaw.Core
{
    internal static class SentenceCommonCharacters
    {
        public const int RankCutoff = 1500;
        public const string ResourceName = "TigerClaw.Core.Data.sentence_common_chars_1500.txt";

        private static readonly ISet<string> Top1500Set = SentenceCharacterRanks.TakeTop(RankCutoff);

        public static ISet<string> Top1500
        {
            get { return Top1500Set; }
        }
    }
}
