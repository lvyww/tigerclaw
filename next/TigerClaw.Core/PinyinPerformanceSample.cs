namespace TigerClaw.Core
{
    internal sealed record PinyinPerformanceSample(long Generation, long Submitted, long Started,
        long SearchStarted, long Decoded, long Ranked, long Prepared, long Published, bool Canceled);
}
