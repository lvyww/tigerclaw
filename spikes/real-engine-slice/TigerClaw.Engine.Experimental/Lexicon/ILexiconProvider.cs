namespace TigerClaw.Engine.Experimental.Lexicon;

public interface ILexiconProvider
{
    IReadOnlyList<LexiconEntry> LookupExact(string code);

    bool HasPrefix(string code);

    bool IsUniqueTerminalCode(string code);
}
