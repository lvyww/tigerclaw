using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TigerClaw.Core
{
    // Isolated AOT/model-loading probe. No production pipe, UI, registration or learning.
    internal static class SentenceFivegramProbe
    {
        internal static int Run(string lexiconPath, string code, string outputPath)
        {
            try
            {
                var loaded = SentenceFivegramModel.LoadAvailable(AppContext.BaseDirectory);
                using var owned = loaded as IDisposable;
                if (loaded is not SentenceFivegramModel)
                    throw new InvalidOperationException("Fivegram unavailable; runtime selected trigram fallback or no model");
                var entries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadLines(lexiconPath))
                {
                    if (line.StartsWith('#')) continue;
                    var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2) continue;
                    if (!entries.TryGetValue(fields[1], out var words)) entries.Add(fields[1], words = new());
                    if (!words.Contains(fields[0])) words.Add(fields[0]);
                }
                using var decoder = new SentenceInputDecoder(
                    SentenceLexiconIndex.Build(entries, SentenceCharacterRanks.TakeTop(1500),
                        CoreRuntimeState.ParseCharacterSet(CoreRuntimeState.DefaultSentenceFullCodeWhitelist)),
                    loaded, emittedCharacterReward: 2, wholeInputSingleCharacterReward: 5,
                    allowDuplicateSingleCharacters: true, canonicalCodeReward: 2,
                    canonicalIsolationFactor: 0, canonicalIsolationMinCodeLength: 4,
                    lexicalPrior: SentenceLexicalPrior.LoadEmbedded(), lexicalPriorWeight: 0.1);
                var result = decoder.Decode(code, 20);
                if (result.Candidates.Length == 0) throw new InvalidOperationException("Probe has no candidates");
                File.WriteAllLines(outputPath, new[] { "fivegram\t" + SentenceFivegramModel.FileName }
                    .Concat(result.Candidates.Select(c => c.FinalScore.ToString("R", CultureInfo.InvariantCulture) + "\t" + c.Text)));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
