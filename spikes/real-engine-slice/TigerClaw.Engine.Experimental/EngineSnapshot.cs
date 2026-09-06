namespace TigerClaw.Engine.Experimental;

public sealed record EngineSnapshot(
    bool Handled,
    string Preedit,
    IReadOnlyList<string> Candidates,
    int SelectedIndex,
    string? Commit,
    bool IsComposing)
{
    public int Caret { get; init; }

    public int CandidateTotal { get; init; }

    public int PageIndex { get; init; }

    public int PageCount { get; init; }

    public static EngineSnapshot Empty(bool handled = false)
    {
        return new EngineSnapshot(handled, string.Empty, [], 0, null, false);
    }
}
