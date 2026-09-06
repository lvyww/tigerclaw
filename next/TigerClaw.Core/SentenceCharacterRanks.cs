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

        private static readonly ISet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);
        private static readonly string[] OrderedCharacters;
        private static readonly Dictionary<string, int> RankByCharacter;

        static SentenceCharacterRanks()
        {
            Load(out RankByCharacter, out OrderedCharacters);
        }

        public static int GetRank(string character)
        {
            if (string.IsNullOrEmpty(character))
            {
                return UnknownRank;
            }

            int rank;
            return RankByCharacter.TryGetValue(character, out rank) ? rank : UnknownRank;
        }

        public static ISet<string> TakeTop(int count)
        {
            if (count <= 0 || OrderedCharacters.Length == 0)
            {
                return EmptySet;
            }

            int n = Math.Min(count, OrderedCharacters.Length);
            var result = new HashSet<string>(n, StringComparer.Ordinal);
            for (int index = 0; index < n; index++)
            {
                result.Add(OrderedCharacters[index]);
            }

            return result;
        }

        private static void Load(out Dictionary<string, int> ranks, out string[] ordered)
        {
            ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<string>();
            Assembly assembly = typeof(SentenceCharacterRanks).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    ordered = Array.Empty<string>();
                    return;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string text = line.Trim();
                        if (text.Length == 0 || text[0] == '#')
                        {
                            continue;
                        }

                        if (ranks.ContainsKey(text))
                        {
                            continue;
                        }

                        ranks[text] = list.Count + 1;
                        list.Add(text);
                    }
                }
            }

            ordered = list.ToArray();
        }
    }
}
