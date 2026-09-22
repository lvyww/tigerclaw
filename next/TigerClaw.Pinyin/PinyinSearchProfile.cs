namespace TigerClaw.Pinyin;
internal sealed class PinyinSearchProfile
{
    internal long LookupTicks,ScoreTicks,LoopTicks;
    internal void Clear(){LookupTicks=ScoreTicks=LoopTicks=0;}
}
