using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TigerClaw.Core
{
    internal static class SentenceCharacterRanks
    {
        public const string ResourceName = "TigerClaw.Core.Data.sentence_char_ranks.txt";
        public const int UnknownRank = 20001;

        private static readonly Dictionary<string, int> RankByCharacter = Load();

        public static int GetRank(string character)
        {
            if (string.IsNullOrEmpty(character))
            {
                return UnknownRank;
            }

            int rank;
            return RankByCharacter.TryGetValue(character, out rank) ? rank : UnknownRank;
        }

        private static Dictionary<string, int> Load()
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            Assembly assembly = typeof(SentenceCharacterRanks).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    return ranks;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    int rank = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string text = line.Trim();
                        if (text.Length == 0 || text[0] == '#')
                        {
                            continue;
                        }

                        rank++;
                        if (!ranks.ContainsKey(text))
                        {
                            ranks[text] = rank;
                        }
                    }
                }
            }

            return ranks;
        }
    }
}
