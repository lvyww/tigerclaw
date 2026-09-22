// Exact signatures from Core; standalone cross-platform model experiment only.
namespace TigerClaw.Core;
internal interface ISentenceLanguageModel : TigerClaw.Pinyin.IPinyinLanguageModel
{
    bool HasObservedBigram(string previous, string target);
}
internal interface ISentenceBoundaryLanguageModel : ISentenceLanguageModel
{
    double LogProbability(string previous2, string previous1, string target, bool includeUnigram);
}
