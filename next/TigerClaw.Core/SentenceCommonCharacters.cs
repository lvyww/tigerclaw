using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TigerClaw.Core
{
    internal static class SentenceCommonCharacters
    {
        public const int RankCutoff = 1500;
        public const string ResourceName = "TigerClaw.Core.Data.sentence_common_chars_1500.txt";

        private static readonly HashSet<string> Top1500Set = LoadTop1500();

        public static ISet<string> Top1500
        {
            get { return Top1500Set; }
        }

        private static HashSet<string> LoadTop1500()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Assembly assembly = typeof(SentenceCommonCharacters).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    return result;
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

                        result.Add(text);
                        if (result.Count >= RankCutoff)
                        {
                            break;
                        }
                    }
                }
            }

            return result;
        }
    }
}
