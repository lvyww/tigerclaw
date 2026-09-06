using System.Text;

namespace TigerClaw.Engine.NativeAot;

internal sealed record MixedInputDecodeResult(
    string RawCode,
    string ResolvedPrefixText,
    string ActiveCode)
{
    public static MixedInputDecodeResult Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}

internal sealed class FixedLengthMixedInputDecoder(RimeLexicon lexicon)
{
    public MixedInputDecodeResult Decode(
        string rawCode,
        int maxCodeLength,
        IReadOnlyDictionary<int, string>? preferredCandidateTextByStart = null)
    {
        if (string.IsNullOrEmpty(rawCode))
        {
            return MixedInputDecodeResult.Empty;
        }

        int safeMaxCodeLength = Math.Clamp(maxCodeLength, 1, 16);
        int completedSegmentCount = (rawCode.Length - 1) / safeMaxCodeLength;
        var prefix = new StringBuilder(completedSegmentCount * safeMaxCodeLength);
        for (int index = 0; index < completedSegmentCount; index++)
        {
            int start = index * safeMaxCodeLength;
            string code = rawCode.Substring(start, safeMaxCodeLength);
            string? candidate = preferredCandidateTextByStart is not null &&
                                preferredCandidateTextByStart.TryGetValue(start, out string? preferred)
                ? preferred
                : lexicon.LookupExact(code).FirstOrDefault();
            prefix.Append(string.IsNullOrEmpty(candidate) ? code : candidate);
        }

        int activeStart = completedSegmentCount * safeMaxCodeLength;
        return new MixedInputDecodeResult(rawCode, prefix.ToString(), rawCode[activeStart..]);
    }
}
