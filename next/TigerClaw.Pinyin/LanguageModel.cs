namespace TigerClaw.Pinyin;

public interface IPinyinLanguageModel
{
    double LogProbability(string previous2, string previous1, string token);
}

// Optional hot path. Token identities remain model-local; state grouping still
// uses source tokens so distinct out-of-vocabulary readings never merge.
internal interface IIndexedPinyinLanguageModel
{
    uint Index(string token);
    double LogProbability(uint previous2, uint previous1, uint token);
}
