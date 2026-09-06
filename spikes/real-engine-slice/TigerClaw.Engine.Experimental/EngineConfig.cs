namespace TigerClaw.Engine.Experimental;

public sealed record EngineConfig(
    int MaxCandidates = 9,
    int PageSize = 5,
    int MaxCodeLength = 4,
    bool AutoCommitUniqueTerminalCode = true,
    bool SecondCandidateSemicolon = true,
    bool ThirdCandidateQuote = true,
    bool ClearOnNoCode = true);
