using System;
using System.Collections.Generic;
using TigerClaw.Core;

internal static class Program
{
    private static int Main()
    {
        var mixed = new FixedLengthMixedInputDecoder(
            code => code == "ab" ? new List<string> { "候" } : new List<string>(),
            candidate => candidate);
        var decoded = mixed.Decode(new MixedInputDecodeRequest
        {
            RawCode = "abc",
            MaxCodeLength = 2,
            LexiconVersion = 1
        });

        Require(decoded.ResolvedPrefixText == "候", "mixed resolved prefix");
        Require(decoded.ActiveCode == "c", "mixed active code");

        var matcher = SentenceSupplementMatcher.Build(new[]
        {
            SentenceSupplementEntry.Create("虎爪", 1000)
        });
        int state = matcher.Advance(0, "虎", out double firstReward);
        state = matcher.Advance(state, "爪", out double finalReward);
        Require(firstReward == 0.0 && finalReward > 0.0, "supplement matcher");

        SimpleJsonObject json = SimpleJson.Parse("{\"enabled\":true,\"count\":2}");
        Require((bool)json.GetValue("enabled") && (long)json.GetValue("count") == 2L, "simple json");

        Console.WriteLine("NET10_LINKED_SOURCE_PROBE_PASS");
        return 0;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Probe assertion failed: " + label);
        }
    }
}
