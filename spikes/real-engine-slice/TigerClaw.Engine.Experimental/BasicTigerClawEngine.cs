using TigerClaw.Engine.Experimental.Input;
using TigerClaw.Engine.Experimental.Lexicon;

namespace TigerClaw.Engine.Experimental;

public sealed class BasicTigerClawEngine
{
    private readonly ILexiconProvider _lexicon;
    private readonly EngineConfig _config;
    private string _buffer = string.Empty;
    private int _pageIndex;

    public BasicTigerClawEngine(ILexiconProvider lexicon, EngineConfig? config = null)
    {
        _lexicon = lexicon;
        _config = config ?? new EngineConfig();
    }

    public EngineSnapshot Process(InputEvent input)
    {
        if (input.Action != KeyAction.KeyDown)
        {
            return Snapshot(handled: false, commit: null);
        }

        switch (input.Key)
        {
            case InputKey.Character:
                return AppendText(input.Text);
            case InputKey.Digit:
                return SelectByDigit(input.Text);
            case InputKey.Semicolon:
                return _config.SecondCandidateSemicolon ? CommitPageCandidate(1) : Snapshot(handled: false, commit: null);
            case InputKey.Quote:
                return _config.ThirdCandidateQuote ? CommitPageCandidate(2) : Snapshot(handled: false, commit: null);
            case InputKey.PagePrevious:
                return MovePage(-1);
            case InputKey.PageNext:
                return MovePage(+1);
            case InputKey.Space:
                return CommitSelected();
            case InputKey.Backspace:
                return Backspace();
            case InputKey.Escape:
                return Clear(handled: _buffer.Length > 0);
            default:
                return Snapshot(handled: false, commit: null);
        }
    }

    public EngineSnapshot Query()
    {
        return Snapshot(handled: false, commit: null);
    }

    private EngineSnapshot AppendText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Snapshot(handled: false, commit: null);
        }

        if (text.Length != 1 || !char.IsLetter(text[0]))
        {
            return Snapshot(handled: false, commit: null);
        }

        string next = _buffer + char.ToLowerInvariant(text[0]);
        if (_buffer.Length > 0 && !_lexicon.HasPrefix(next))
        {
            string? commit = CandidatesForCurrentBuffer().FirstOrDefault();
            _buffer = next;
            _pageIndex = 0;
            return Snapshot(handled: true, commit: commit);
        }

        _buffer = next;
        _pageIndex = 0;
        if (_config.AutoCommitUniqueTerminalCode &&
            _buffer.Length >= SafeMaxCodeLength() &&
            _lexicon.IsUniqueTerminalCode(_buffer))
        {
            string commit = CandidatesForCurrentBuffer()[0];
            _buffer = string.Empty;
            return Snapshot(handled: true, commit: commit);
        }

        return Snapshot(handled: true, commit: null);
    }

    private EngineSnapshot CommitSelected()
    {
        IReadOnlyList<string> candidates = CandidatesForCurrentBuffer();
        if (_buffer.Length == 0 || candidates.Count == 0)
        {
            return Snapshot(handled: false, commit: null);
        }

        string commit = candidates[0];
        _buffer = string.Empty;
        _pageIndex = 0;
        return EngineSnapshot.Empty(handled: true) with { Commit = commit };
    }

    private EngineSnapshot Backspace()
    {
        if (_buffer.Length == 0)
        {
            return Snapshot(handled: false, commit: null);
        }

        _buffer = _buffer[..^1];
        _pageIndex = 0;
        return Snapshot(handled: true, commit: null);
    }

    private EngineSnapshot Clear(bool handled)
    {
        _buffer = string.Empty;
        _pageIndex = 0;
        return EngineSnapshot.Empty(handled);
    }

    private EngineSnapshot Snapshot(bool handled, string? commit)
    {
        IReadOnlyList<string> allCandidates = CandidatesForCurrentBuffer();
        int pageSize = SafePageSize();
        int pageCount = allCandidates.Count == 0 ? 0 : (allCandidates.Count + pageSize - 1) / pageSize;
        _pageIndex = pageCount == 0 ? 0 : Math.Clamp(_pageIndex, 0, pageCount - 1);
        IReadOnlyList<string> candidates = allCandidates
            .Skip(_pageIndex * pageSize)
            .Take(pageSize)
            .ToArray();
        return new EngineSnapshot(
            handled,
            _buffer,
            candidates,
            0,
            commit,
            _buffer.Length > 0)
        {
            Caret = _buffer.Length,
            CandidateTotal = allCandidates.Count,
            PageIndex = _pageIndex,
            PageCount = pageCount,
        };
    }

    private IReadOnlyList<string> CandidatesForCurrentBuffer()
    {
        if (_buffer.Length == 0)
        {
            return [];
        }

        return _lexicon
            .LookupExact(_buffer)
            .Take(_config.MaxCandidates)
            .Select(entry => entry.Text)
            .ToArray();
    }

    private EngineSnapshot SelectByDigit(string? text)
    {
        return text?.Length == 1 && text[0] is >= '1' and <= '9'
            ? CommitPageCandidate(text[0] - '1')
            : Snapshot(handled: false, commit: null);
    }

    private EngineSnapshot CommitPageCandidate(int index)
    {
        EngineSnapshot current = Snapshot(handled: false, commit: null);
        if (_buffer.Length == 0 || index < 0 || index >= current.Candidates.Count)
        {
            return current;
        }

        string commit = current.Candidates[index];
        _buffer = string.Empty;
        _pageIndex = 0;
        return EngineSnapshot.Empty(handled: true) with { Commit = commit };
    }

    private EngineSnapshot MovePage(int delta)
    {
        if (_buffer.Length == 0 || CandidatesForCurrentBuffer().Count == 0)
        {
            return Snapshot(handled: false, commit: null);
        }

        int pageCount = (CandidatesForCurrentBuffer().Count + SafePageSize() - 1) / SafePageSize();
        _pageIndex = Math.Clamp(_pageIndex + delta, 0, pageCount - 1);
        return Snapshot(handled: true, commit: null);
    }

    private int SafePageSize() => Math.Clamp(_config.PageSize, 1, 10);

    private int SafeMaxCodeLength() => Math.Clamp(_config.MaxCodeLength, 1, 16);
}
