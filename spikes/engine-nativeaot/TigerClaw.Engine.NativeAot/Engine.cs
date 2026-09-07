using TigerClaw.Core;
using System.Globalization;

namespace TigerClaw.Engine.NativeAot;

internal sealed class BasicEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly RimeLexicon _lexicon;
    private readonly PinyinLexicon _pinyinLexicon;
    private readonly BasicEngineConfig _config;
    private readonly FixedLengthMixedInputDecoder _mixedDecoder;
    private readonly Dictionary<int, string> _mixedPreferredCandidateText = [];
    private readonly Dictionary<string, string> _expandedCandidateText = [];
    private string _buffer = string.Empty;
    private string _mixedRaw = string.Empty;
    private string _mixedPrefix = string.Empty;
    private string _repeatBuffer = "重复上屏";
    private int _pageIndex;
    private bool _nextQuoteIsOpening = true;
    private bool _dotAfterDigitArmed;
    private bool _pinyinMode;
    private readonly SentenceNgramModel? _sentenceModel;
    private readonly SentenceInputDecoder? _sentenceDecoder;
    private readonly SentenceNeuralReranker? _sentenceReranker;
    private readonly object _sentenceDecoderLock = new();
    private readonly AutoResetEvent? _sentenceDecodeSignal;
    private readonly Thread? _sentenceDecodeWorker;
    private SentenceDecodeResult _sentenceResult = SentenceDecodeResult.Empty;
    private string _sentenceResultRawCode = string.Empty;
    private int _sentenceSelectedIndex;
    private long _sentenceGeneration;
    private bool _sentenceDecodePending;
    private bool _sentenceNeuralPending;
    private string[] _sentencePendingCandidates = [];
    private string _sentenceCommittedText = string.Empty;
    private int _sentenceCommittedRawLength;
    private int _sentenceLastAutoCommitRawLength;
    private bool _sentenceAutoCommitSuspended;
    private string _sentenceNeuralAcceptedRaw = string.Empty;
    private string _sentenceNeuralTopText = string.Empty;
    private readonly List<SentenceAutoCommitEvidence> _sentenceAutoCommitEvidence = [];
    private string _quickSymbolDeferredSentenceCommit = string.Empty;
    private DeferredSentenceComposition? _quickSymbolDeferredSentence;
    private bool _quickSymbolSlashCommand;
    private bool _disposed;

    private sealed record SentenceAutoCommitEvidence(
        string RawCode,
        string Proposal,
        Dictionary<string, int> RawLengths);

    private sealed record DeferredSentenceComposition(
        string RawCode,
        int SelectedIndex,
        string CommittedText,
        int CommittedRawLength);

    public BasicEngine(
        RimeLexicon lexicon,
        PinyinLexicon pinyinLexicon,
        BasicEngineConfig config,
        SentenceLexiconIndex? sentenceLexicon = null,
        SentenceSupplementMatcher? sentenceSupplements = null,
        string? sentenceModelPath = null,
        SentenceNeuralReranker? sentenceReranker = null)
    {
        _lexicon = lexicon;
        _pinyinLexicon = pinyinLexicon;
        _config = config;
        _mixedDecoder = new FixedLengthMixedInputDecoder(lexicon);
        if (config.SentenceInputEnabled && sentenceLexicon is not null && !string.IsNullOrWhiteSpace(sentenceModelPath))
        {
            _sentenceModel = SentenceNgramModel.Load(sentenceModelPath);
            _sentenceDecoder = new SentenceInputDecoder(
                sentenceLexicon,
                _sentenceModel,
                emittedCharacterReward: 2.0,
                supplementMatcher: sentenceSupplements,
                allowDuplicateSingleCharacters: config.SentenceAllowDuplicateSingleCharacters);
            _sentenceReranker = sentenceReranker;
            _sentenceDecodeSignal = new AutoResetEvent(false);
            _sentenceDecodeWorker = new Thread(SentenceDecodeWorkerMain)
            {
                IsBackground = true,
                Name = "TigerClaw macOS sentence decoder",
                Priority = ThreadPriority.BelowNormal,
            };
            _sentenceDecodeWorker.Start();
        }
    }

    private bool IsSentenceMode => _sentenceDecoder is not null;

    // Fast-symbol codes belong to the active code-table even when the selected
    // schema also enables sentence decoding.  Once one of their guide symbols
    // has been accepted, the rest of that composition must use the ordinary
    // candidate and commit paths.
    private bool IsQuickSymbolComposition => _buffer.Length > 0 && _buffer[0] is ';' or '/' or '[';

    // A sentence can only begin after the first code key. Keep one-key input
    // on the ordinary table so short-code characters retain their established
    // candidate order; an imported Tiger table may otherwise expose a word
    // from its full-code (`stem`) index ahead of that character.
    private bool IsSentenceComposition => IsSentenceMode && _buffer.Length >= 2 && !IsQuickSymbolComposition;

    // In a sentence, `[` begins the table's normal bracket fast-symbols.  A
    // following slash is an explicit escape into the table's slash commands,
    // so `[/rq` can reach {日期...} without stealing `[r` (）).
    private string QuickSymbolLookupCode(string displayCode) =>
        _quickSymbolSlashCommand && displayCode.StartsWith("[/", StringComparison.Ordinal)
            ? "/" + displayCode[2..]
            : displayCode;

    public void Dispose()
    {
        Thread? worker;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _sentenceGeneration++;
            _sentenceDecodePending = false;
            _sentenceNeuralPending = false;
            worker = _sentenceDecodeWorker;
            _sentenceDecodeSignal?.Set();
        }

        worker?.Join();
        lock (_sentenceDecoderLock)
        {
            _sentenceModel?.Dispose();
        }
        _sentenceDecodeSignal?.Dispose();
    }

    public EngineSnapshot Process(InputEvent input)
    {
        lock (_lock)
        {
            return ProcessCore(input);
        }
    }

    private EngineSnapshot ProcessCore(InputEvent input)
    {
        if (input.Action != KeyAction.KeyDown)
        {
            return Snapshot(handled: false, commit: null);
        }

        if (input.Key != InputKey.Digit &&
            !(input.Key == InputKey.Character && input.Text == "."))
        {
            _dotAfterDigitArmed = false;
        }

        if (!IsSentenceComposition && TryProcessConfiguredCandidateKey(input, out EngineSnapshot configuredKeyResult))
        {
            return configuredKeyResult;
        }

        return input.Key switch
        {
            InputKey.Character => ProcessCharacter(input),
            InputKey.Digit => ProcessDigit(input),
            InputKey.Semicolon => ProcessSemicolon(input.Modifiers),
            InputKey.Quote => ProcessQuote(),
            InputKey.PagePrevious => ProcessPageKey(-1),
            InputKey.PageNext => ProcessPageKey(+1),
            InputKey.Space => CommitSelected(),
            InputKey.Enter => _config.EnterClearsComposition ? Clear() : CommitRaw(),
            InputKey.Backspace => Backspace(),
            InputKey.Tab => IsSentenceComposition && _buffer.Length > 0
                ? MoveSentenceSelection((input.Modifiers & (1 << 0)) != 0 ? -1 : 1)
                : _config.TabClearsComposition ? Clear() : Snapshot(handled: false, commit: null),
            InputKey.ArrowUp => IsSentenceComposition && _buffer.Length > 0
                ? MoveSentenceSelection(-1)
                : Snapshot(handled: false, commit: null),
            InputKey.ArrowDown => IsSentenceComposition && _buffer.Length > 0
                ? MoveSentenceSelection(1)
                : Snapshot(handled: false, commit: null),
            InputKey.Escape => Clear(handled: _buffer.Length > 0),
            _ => Snapshot(handled: false, commit: null),
        };
    }

    public EngineSnapshot Clear(bool handled = true)
    {
        lock (_lock)
        {
            return ClearCore(handled);
        }
    }

    private EngineSnapshot ClearCore(bool handled)
    {
        _buffer = string.Empty;
        _mixedRaw = string.Empty;
        _mixedPrefix = string.Empty;
        _mixedPreferredCandidateText.Clear();
        _expandedCandidateText.Clear();
        _pageIndex = 0;
        _dotAfterDigitArmed = false;
        _pinyinMode = false;
        _sentenceResult = SentenceDecodeResult.Empty;
        _sentenceResultRawCode = string.Empty;
        _sentenceSelectedIndex = 0;
        _sentenceGeneration++;
        _sentenceDecodePending = false;
        _sentenceNeuralPending = false;
        _sentencePendingCandidates = [];
        ResetSentenceAutoCommitState();
        return EngineSnapshot.Empty(handled);
    }

    public EngineSnapshot SelectCandidate(int pageIndex)
    {
        lock (_lock)
        {
            return CommitPageCandidate(pageIndex);
        }
    }

    public EngineSnapshot CurrentSnapshot()
    {
        lock (_lock)
        {
            return Snapshot(handled: false, commit: null);
        }
    }

    private bool TryProcessConfiguredCandidateKey(InputEvent input, out EngineSnapshot result)
    {
        result = default!;
        if (_buffer.Length == 0 || input.Modifiers != 0 || string.IsNullOrEmpty(input.Text) || input.Text.Length != 1)
        {
            return false;
        }

        int selectionIndex = _config.SelectionKeys.IndexOf(input.Text[0]);
        if (selectionIndex >= 0)
        {
            result = CommitPageCandidate(selectionIndex);
            return true;
        }

        if (_config.PreviousPageKeys.Contains(input.Text[0]))
        {
            result = ProcessPageKey(-1);
            return true;
        }

        if (_config.NextPageKeys.Contains(input.Text[0]))
        {
            result = ProcessPageKey(+1);
            return true;
        }

        return false;
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

        if (_pinyinMode)
        {
            _buffer += char.ToLowerInvariant(text[0]);
            _pageIndex = 0;
            return Snapshot(handled: true, commit: null);
        }

        // A quick-symbol prefix must stay on the ordinary code-table path.
        // In sentence mode the first symbol is accepted by
        // TryProcessQuickSymbolCode, then subsequent letters arrive here.
        // Do not hand that continuation to the sentence decoder.
        bool continuingQuickSymbol = IsQuickSymbolComposition;
        if (IsSentenceMode && !continuingQuickSymbol && _buffer.Length > 0)
        {
            return AppendSentenceInput(char.ToLowerInvariant(text[0]));
        }

        if (_config.UnlimitedMixedInput && !continuingQuickSymbol)
        {
            RememberMixedPageCandidate();
            _mixedRaw += text[0];
            RebuildMixedInput();
            _pageIndex = 0;
            return Snapshot(handled: true, commit: null);
        }

        string next = _buffer + char.ToLowerInvariant(text[0]);
        string lookupNext = QuickSymbolLookupCode(next);
        if (_buffer.Length > 0 && !_lexicon.HasPrefix(lookupNext))
        {
            string? commit = CandidatesForCurrentBuffer().FirstOrDefault();
            if (commit is null && !_config.ClearOnNoCode)
            {
                _buffer = next;
                _pageIndex = 0;
                return Snapshot(handled: true, commit: null);
            }
            string? expandedCommit = commit is null ? null : ExpandCandidateText(commit);
            if (expandedCommit is not null && TryGetAction(expandedCommit, out EngineAction action))
            {
                ResetComposition();
                return EngineSnapshot.Empty(handled: true) with { Action = action };
            }
            expandedCommit = expandedCommit is null ? null : TrackCommit(expandedCommit);
            _expandedCandidateText.Clear();
            _buffer = char.ToLowerInvariant(text[0]).ToString();
            _pageIndex = 0;
            return Snapshot(handled: true, commit: expandedCommit);
        }

        _buffer = next;
        _pageIndex = 0;
        bool uniqueTerminal = _lexicon.IsUniqueTerminalCode(lookupNext);
        bool quickSymbol = _buffer[0] is ';' or '/' or '[';
        if (uniqueTerminal &&
            (quickSymbol || (_config.AutoCommitUniqueTerminalCode && _buffer.Length >= _config.MaxCodeLength)))
        {
            string commit = ExpandCandidateText(CandidatesForCurrentBuffer()[0]);
            return quickSymbol ? CommitQuickSymbol(commit) : CommitAfterReset(commit);
        }

        return Snapshot(handled: true, commit: null);
    }

    private EngineSnapshot ProcessCharacter(InputEvent input)
    {
        if (input.Text == "." &&
            input.Modifiers == 0 &&
            _dotAfterDigitArmed &&
            !_config.UseEnglishPunctuationInChinese)
        {
            _dotAfterDigitArmed = false;
            return CommitSelectedWithSuffix(".");
        }

        _dotAfterDigitArmed = false;
        if (!IsSentenceComposition && input.Text == "`" && input.Modifiers == 0 && _buffer.Length == 0 && _config.PinyinReverseEnabled && _pinyinLexicon.IsAvailable)
        {
            _pinyinMode = true;
            _pageIndex = 0;
            return Snapshot(handled: true, commit: null);
        }

        if (IsCodeLetter(input.Text))
        {
            return AppendText(input.Text);
        }

        // `[` is an independent quick-symbol guide, not a sentence selector.
        // When it follows an in-progress sentence, hold that sentence's chosen
        // output until the quick code finishes instead of committing it with a
        // literal full-width bracket on the first key.
        if (input.Text == "[" && IsSentenceMode && !IsQuickSymbolComposition && _buffer.Length > 0 && _lexicon.HasPrefix("["))
        {
            return StartQuickSymbolAfterSentence();
        }

        if (input.Text == "/" && IsQuickSymbolComposition && _buffer == "[" && _lexicon.HasPrefix("/"))
        {
            _buffer += "/";
            _quickSymbolSlashCommand = true;
            _pageIndex = 0;
            return Snapshot(handled: true, commit: null);
        }

        if (TryProcessQuickSymbolCode(input.Text) is EngineSnapshot quickSymbol)
        {
            return quickSymbol;
        }

        return TryGetChinesePunctuation(input.Text, input.Modifiers, _config.UseEnglishPunctuationInChinese, _config.SlashOutputsDunhao, out string? punctuation)
            ? CommitSelectedWithSuffix(punctuation)
            : Snapshot(handled: false, commit: null);
    }

    private EngineSnapshot StartQuickSymbolAfterSentence()
    {
        EnsureLatestSentenceResult();
        IReadOnlyList<string> candidates = CandidatesForCurrentBuffer();
        if (candidates.Count == 0)
        {
            return CommitSelectedWithSuffix("【");
        }

        string selected = candidates[Math.Clamp(_sentenceSelectedIndex, 0, candidates.Count - 1)];
        string deferredCommit = _sentenceCommittedText.Length > 0
            ? selected.StartsWith(_sentenceCommittedText, StringComparison.Ordinal)
                ? selected[_sentenceCommittedText.Length..]
                : UncommittedSentenceRawCode()
            : selected;
        DeferredSentenceComposition deferredSentence = new(
            _buffer,
            _sentenceSelectedIndex,
            _sentenceCommittedText,
            _sentenceCommittedRawLength);

        ResetComposition();
        _quickSymbolDeferredSentenceCommit = ExpandCandidateText(deferredCommit);
        _quickSymbolDeferredSentence = deferredSentence;
        return TryProcessQuickSymbolCode("[") ?? Snapshot(handled: false, commit: null);
    }

    private EngineSnapshot ProcessDigit(InputEvent input)
    {
        if ((input.Modifiers & (1 << 0)) != 0 &&
            TryGetShiftDigitPunctuation(input, out string? punctuation))
        {
            _dotAfterDigitArmed = false;
            return CommitSelectedWithSuffix(punctuation);
        }

        if (IsSentenceComposition && _buffer.Length > 0 && input.Modifiers == 0 && input.Text?.Length == 1)
        {
            _dotAfterDigitArmed = false;
            return AppendSentenceInput(input.Text[0]);
        }

        if (_buffer.Length == 0 && input.Modifiers == 0 && input.Text?.Length == 1)
        {
            _dotAfterDigitArmed = true;
        }
        else
        {
            _dotAfterDigitArmed = false;
        }

        return _config.SelectionKeys.Contains(input.Text ?? string.Empty, StringComparison.Ordinal)
            ? SelectByDigit(input.Text)
            : Snapshot(handled: false, commit: null);
    }

    private EngineSnapshot ProcessSemicolon(int modifiers)
    {
        if ((modifiers & (1 << 0)) != 0)
        {
            return CommitSelectedWithSuffix(_config.UseEnglishPunctuationInChinese ? ":" : "：");
        }

        if (IsSentenceComposition && _buffer.Length > 0 && _config.SecondCandidateSemicolon)
        {
            return AppendSentenceInput(';');
        }

        if (_buffer.Length == 0)
        {
            // The active macOS scheme uses [ as its fast-symbol guide.  A
            // bare semicolon must retain normal Chinese punctuation behavior;
            // otherwise a legacy 快符 entry (; -> ：) makes full-width ；
            // impossible to enter.
            return CommitSelectedWithSuffix(_config.UseEnglishPunctuationInChinese ? ";" : "；");
        }

        if (_buffer.Length > 0 && _config.SecondCandidateSemicolon)
        {
            return CommitPageCandidate(1);
        }

        return CommitSelectedWithSuffix(_config.UseEnglishPunctuationInChinese ? ";" : "；");
    }

    private EngineSnapshot ProcessQuote()
    {
        if (IsSentenceComposition && _config.ThirdCandidateQuote)
        {
            return AppendSentenceInput('\'');
        }

        if (_buffer.Length > 0 && _config.ThirdCandidateQuote)
        {
            return CommitPageCandidate(2);
        }

        string quote = _nextQuoteIsOpening ? "“" : "”";
        _nextQuoteIsOpening = !_nextQuoteIsOpening;
        return CommitSelectedWithSuffix(quote);
    }

    private EngineSnapshot CommitSelected()
    {
        bool quickSymbol = IsQuickSymbolComposition;
        EnsureLatestSentenceResult();
        IReadOnlyList<string> candidates = CandidatesForCurrentBuffer();
        if (_config.UnlimitedMixedInput && _mixedRaw.Length > 0)
        {
            string mixedCommit = _mixedPrefix + (candidates.Count > 0 ? ExpandCandidateText(candidates[0]) : string.Empty);
            ResetComposition();
            return EngineSnapshot.Empty(handled: true) with { Commit = mixedCommit.Length == 0 ? null : TrackCommit(mixedCommit) };
        }

        if (_buffer.Length == 0 || candidates.Count == 0)
        {
            return Snapshot(handled: false, commit: null);
        }

        int candidateIndex = IsSentenceComposition
            ? Math.Clamp(_sentenceSelectedIndex, 0, candidates.Count - 1)
            : 0;
        string commit = ExpandCandidateText(candidates[candidateIndex]);
        if (IsSentenceComposition && _sentenceCommittedText.Length > 0)
        {
            commit = commit.StartsWith(_sentenceCommittedText, StringComparison.Ordinal)
                ? commit[_sentenceCommittedText.Length..]
                : UncommittedSentenceRawCode();
        }
        return quickSymbol ? CommitQuickSymbol(commit) : CommitAfterReset(commit);
    }

    private EngineSnapshot CommitRaw()
    {
        bool quickSymbol = IsQuickSymbolComposition;
        bool wasPinyinMode = _pinyinMode;
        string raw = IsSentenceComposition
            ? UncommittedSentenceRawCode()
            : _config.UnlimitedMixedInput && !wasPinyinMode ? _mixedRaw : _buffer;
        if (raw.Length == 0)
        {
            return Snapshot(handled: false, commit: null);
        }

        string commit = ExpandOutputText(wasPinyinMode ? "·" + raw : raw);
        return quickSymbol ? CommitQuickSymbol(commit) : CommitAfterReset(commit);
    }

    private EngineSnapshot CommitSelectedWithSuffix(string suffix)
    {
        bool quickSymbol = IsQuickSymbolComposition;
        EnsureLatestSentenceResult();
        IReadOnlyList<string> candidates = CandidatesForCurrentBuffer();
        string prefix = _config.UnlimitedMixedInput ? _mixedPrefix : string.Empty;
        string? selected = IsSentenceComposition && candidates.Count > 0
            ? candidates[Math.Clamp(_sentenceSelectedIndex, 0, candidates.Count - 1)]
            : candidates.FirstOrDefault();
        if (IsSentenceComposition && _sentenceCommittedText.Length > 0)
        {
            selected = selected is not null && selected.StartsWith(_sentenceCommittedText, StringComparison.Ordinal)
                ? selected[_sentenceCommittedText.Length..]
                : UncommittedSentenceRawCode();
        }
        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(selected))
        {
            string suffixCommit = ExpandOutputText(suffix);
            return quickSymbol
                ? CommitQuickSymbol(suffixCommit)
                : CommitAfterReset(suffixCommit);
        }

        string commit = prefix + (selected is null ? string.Empty : ExpandCandidateText(selected)) + ExpandOutputText(suffix);
        return quickSymbol ? CommitQuickSymbol(commit) : CommitAfterReset(commit);
    }

    private EngineSnapshot Backspace()
    {
        if (_buffer.Length == 0)
        {
            if (_pinyinMode)
            {
                _pinyinMode = false;
                return Snapshot(handled: true, commit: null);
            }
            return Snapshot(handled: false, commit: null);
        }

        if (IsQuickSymbolComposition)
        {
            _buffer = _buffer[..^1];
            if (_buffer.Length < 2)
            {
                _quickSymbolSlashCommand = false;
            }
            if (_buffer.Length == 0 && _quickSymbolDeferredSentence is DeferredSentenceComposition deferredSentence)
            {
                _quickSymbolDeferredSentenceCommit = string.Empty;
                _quickSymbolDeferredSentence = null;
                _buffer = deferredSentence.RawCode;
                _sentenceSelectedIndex = deferredSentence.SelectedIndex;
                _sentenceCommittedText = deferredSentence.CommittedText;
                _sentenceCommittedRawLength = deferredSentence.CommittedRawLength;
                ScheduleSentenceDecode();
            }
            _pageIndex = 0;
            return Snapshot(handled: true, commit: null);
        }

        if (IsSentenceComposition)
        {
            ResetSentenceAutoCommitEvidence();
            // This is only meaningful after sentence early commit has placed
            // a prefix in the target application.  With early commit disabled
            // every Backspace must remove exactly one still-visible raw code.
            if (_config.SentenceAutoCommitEnabled &&
                _sentenceCommittedRawLength > 0 && _buffer.Length <= _sentenceCommittedRawLength + 1)
            {
                return ClearCore(handled: true);
            }
        }

        // Quick phrases bypass mixed-input decoding, so their visible buffer can be
        // non-empty while _mixedRaw is empty.  In that case delete the visible
        // quick-phrase code directly instead of slicing an empty mixed buffer.
        if (_config.UnlimitedMixedInput && _mixedRaw.Length > 0)
        {
            _mixedRaw = _mixedRaw[..^1];
            RebuildMixedInput();
        }
        else
        {
            _buffer = _buffer[..^1];
            if (IsSentenceComposition)
            {
                _sentenceCommittedRawLength = Math.Min(_sentenceCommittedRawLength, _buffer.Length);
                ScheduleSentenceDecode();
            }
        }
        _pageIndex = 0;
        return Snapshot(handled: true, commit: null);
    }

    private EngineSnapshot Snapshot(bool handled, string? commit)
    {
        IReadOnlyList<string> allCandidates = CandidatesForCurrentBuffer();
        if (IsSentenceComposition && allCandidates.Count > 0)
        {
            _sentenceSelectedIndex = Math.Clamp(_sentenceSelectedIndex, 0, allCandidates.Count - 1);
            _pageIndex = _sentenceSelectedIndex / _config.PageSize;
        }
        int pageCount = allCandidates.Count == 0 ? 0 : (allCandidates.Count + _config.PageSize - 1) / _config.PageSize;
        _pageIndex = pageCount == 0 ? 0 : Math.Clamp(_pageIndex, 0, pageCount - 1);
        IReadOnlyList<string> candidates = allCandidates
            .Skip(_pageIndex * _config.PageSize)
            .Take(_config.PageSize)
            .Select(PublishedCandidateText)
            .Select(ExpandCandidateText)
            .ToArray();
        string activeInputCode = IsSentenceComposition ? SentenceDisplayCode() : (_pinyinMode ? "·" : string.Empty) + _buffer;
        return new EngineSnapshot(
            handled,
            _mixedPrefix + activeInputCode,
            candidates,
            IsSentenceComposition ? _sentenceSelectedIndex - (_pageIndex * _config.PageSize) : 0,
            commit,
            (IsSentenceComposition ? _buffer.Length > _sentenceCommittedRawLength : _buffer.Length > 0) || _pinyinMode)
        {
            Caret = (_mixedPrefix + activeInputCode).Length,
            CompositionPrefix = _mixedPrefix,
            ActiveInputCode = activeInputCode,
            CandidateTotal = allCandidates.Count,
            PageIndex = _pageIndex,
            PageCount = pageCount,
            SentenceRerankPending = _sentenceDecodePending || _sentenceNeuralPending,
        };
    }

    // Auto-committed sentence text has already reached the target application.
    // Keep the decoder's full candidates internally so selection and final
    // commits retain their raw/text boundaries, but expose only the live suffix
    // to the candidate window. This matches the Windows Core's
    // GetPublishedSentenceCandidates contract.
    private string PublishedCandidateText(string text)
    {
        if (IsSentenceComposition && _sentenceCommittedText.Length > 0 &&
            text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
        {
            return text[_sentenceCommittedText.Length..];
        }

        return text;
    }

    private IReadOnlyList<string> CandidatesForCurrentBuffer()
    {
        if (_buffer.Length == 0)
        {
            return [];
        }

        if (IsSentenceComposition)
        {
            if (_sentenceResult.Candidates.Length == 0 && _sentenceDecodePending && _sentencePendingCandidates.Length > 0)
            {
                return _sentencePendingCandidates;
            }
            return _sentenceResult.Candidates
                .Take(_config.MaxCandidates)
                .Select(candidate => candidate.Text)
                .ToArray();
        }

        return (_pinyinMode ? _pinyinLexicon.Lookup(_buffer) : _lexicon.LookupExact(QuickSymbolLookupCode(_buffer)))
            .Take(_config.MaxCandidates)
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
        bool quickSymbol = IsQuickSymbolComposition;
        EnsureLatestSentenceResult();
        EngineSnapshot current = Snapshot(handled: false, commit: null);
        if (_buffer.Length == 0 || index < 0 || index >= current.Candidates.Count)
        {
            return current;
        }

        int globalIndex = (_pageIndex * _config.PageSize) + index;
        if (IsSentenceComposition)
        {
            _sentenceSelectedIndex = globalIndex;
        }
        // Snapshot candidates are presentation values. In sentence mode they
        // already omit a prefix which has been automatically committed.
        string commit = _mixedPrefix + current.Candidates[index];
        return quickSymbol ? CommitQuickSymbol(commit) : CommitAfterReset(commit);
    }

    private EngineSnapshot ProcessPageKey(int delta)
    {
        EnsureLatestSentenceResult();
        if (_buffer.Length == 0 || CandidatesForCurrentBuffer().Count == 0)
        {
            return CommitSelectedWithSuffix(delta < 0 ? "-" : "=");
        }

        if (IsSentenceComposition)
        {
            return MoveSentenceSelection(delta * _config.PageSize);
        }

        int pageCount = (CandidatesForCurrentBuffer().Count + _config.PageSize - 1) / _config.PageSize;
        _pageIndex = Math.Clamp(_pageIndex + delta, 0, pageCount - 1);
        return Snapshot(handled: true, commit: null);
    }

    private EngineSnapshot AppendSentenceInput(char value)
    {
        if (_buffer.Length - _sentenceCommittedRawLength >= 128)
        {
            return Snapshot(handled: true, commit: null);
        }

        _sentencePendingCandidates = CandidatesForCurrentBuffer().ToArray();
        _buffer += value;
        _sentenceSelectedIndex = 0;
        _pageIndex = 0;
        string? pendingCommit = TryAutoCommitSentencePrefix();
        ScheduleSentenceDecode();
        return Snapshot(handled: true, commit: pendingCommit);
    }

    private void ScheduleSentenceDecode()
    {
        _sentenceGeneration++;
        _sentenceNeuralPending = false;
        if (_sentenceDecoder is null || _buffer.Length == 0)
        {
            _sentenceResult = SentenceDecodeResult.Empty;
            _sentenceResultRawCode = string.Empty;
            _sentenceSelectedIndex = 0;
            _sentenceDecodePending = false;
            return;
        }

        _sentenceDecodePending = true;
        _sentenceDecodeSignal?.Set();
    }

    private void SentenceDecodeWorkerMain()
    {
        AutoResetEvent signal = _sentenceDecodeSignal!;
        while (true)
        {
            signal.WaitOne();
            while (true)
            {
                long generation;
                string rawCode;
                string requiredTextPrefix;
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (!_sentenceDecodePending || _sentenceDecoder is null || _buffer.Length == 0)
                    {
                        break;
                    }
                    generation = _sentenceGeneration;
                    rawCode = _buffer;
                    requiredTextPrefix = _sentenceCommittedText;
                }

                SentenceDecodeResult result;
                try
                {
                    lock (_sentenceDecoderLock)
                    {
                        result = _sentenceDecoder.Decode(
                            rawCode,
                            _config.MaxCandidates,
                            _config.SentenceAutoCommitEnabled,
                            requiredTextPrefix);
                    }
                }
                catch
                {
                    result = SentenceDecodeResult.Empty;
                }

                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (!_sentenceDecodePending)
                    {
                        break;
                    }
                    if (generation != _sentenceGeneration || !string.Equals(rawCode, _buffer, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    _sentenceDecodePending = false;
                    ApplySentenceDecodeResult(generation, rawCode, result);
                    break;
                }
            }
        }
    }

    private void EnsureLatestSentenceResult()
    {
        if (!_sentenceDecodePending || _sentenceDecoder is null || _buffer.Length == 0)
        {
            return;
        }

        long generation = _sentenceGeneration;
        string rawCode = _buffer;
        string requiredTextPrefix = _sentenceCommittedText;
        SentenceDecodeResult result;
        lock (_sentenceDecoderLock)
        {
            result = _sentenceDecoder.Decode(
                rawCode,
                _config.MaxCandidates,
                _config.SentenceAutoCommitEnabled,
                requiredTextPrefix);
        }
        if (_disposed || generation != _sentenceGeneration || !string.Equals(rawCode, _buffer, StringComparison.Ordinal))
        {
            return;
        }
        _sentenceDecodePending = false;
        ApplySentenceDecodeResult(generation, rawCode, result);
    }

    private void ApplySentenceDecodeResult(long generation, string rawCode, SentenceDecodeResult result)
    {
        _sentenceResult = FilterSentenceDecodeResultForCommittedPrefix(result);
        _sentenceResultRawCode = rawCode;
        _sentencePendingCandidates = [];
        if (_sentenceResult.Candidates.Length == 0)
        {
            _sentenceSelectedIndex = 0;
        }
        else
        {
            _sentenceSelectedIndex = Math.Clamp(_sentenceSelectedIndex, 0, _sentenceResult.Candidates.Length - 1);
        }

        _sentenceNeuralPending = _sentenceReranker?.Request(
            generation,
            rawCode,
            _sentenceResult.Candidates.Take(5).Select(candidate => candidate.Text).ToArray(),
            ApplySentenceNeuralScores) == true;
    }

    private void ApplySentenceNeuralScores(long generation, string rawCode, double[]? scores)
    {
        lock (_lock)
        {
            if (_disposed || generation != _sentenceGeneration || !string.Equals(rawCode, _buffer, StringComparison.Ordinal))
            {
                return;
            }

            _sentenceNeuralPending = false;
            SentenceCandidate[] candidates = _sentenceResult.Candidates;
            int rerankCount = Math.Min(5, candidates.Length);
            if (scores is null || scores.Length != rerankCount)
            {
                return;
            }

            for (int index = 0; index < rerankCount; index++)
            {
                candidates[index].FinalScore = candidates[index].BaseScore + (0.84 * scores[index]);
            }
            Array.Sort(
                candidates,
                0,
                rerankCount,
                Comparer<SentenceCandidate>.Create(SentenceCandidate.CompareByLexiconRankThenScore));
            _sentenceSelectedIndex = 0;
            _pageIndex = 0;
            _sentenceNeuralAcceptedRaw = rawCode;
            _sentenceNeuralTopText = candidates.Length > 0 ? candidates[0].Text : string.Empty;
        }
    }

    private EngineSnapshot MoveSentenceSelection(int delta)
    {
        EnsureLatestSentenceResult();
        _sentenceAutoCommitSuspended = true;
        ResetSentenceAutoCommitEvidence();
        int visibleCount = Math.Min(
            _sentenceResult.Candidates.Length,
            Math.Min(Math.Max(_config.PageSize, 1), 10));
        if (visibleCount == 0)
        {
            return Snapshot(handled: true, commit: null);
        }

        _sentenceSelectedIndex = ((_sentenceSelectedIndex + delta) % visibleCount + visibleCount) % visibleCount;
        _pageIndex = _sentenceSelectedIndex / _config.PageSize;
        return Snapshot(handled: true, commit: null);
    }

    private string SentenceDisplayCode()
    {
        if (_sentenceResult.Candidates.Length == 0)
        {
            return _buffer;
        }

        int selected = Math.Clamp(_sentenceSelectedIndex, 0, _sentenceResult.Candidates.Length - 1);
        string segmentedCode = _sentenceResult.Candidates[selected].SegmentedCode ?? _sentenceResultRawCode;
        string display = string.Equals(_sentenceResultRawCode, _buffer, StringComparison.Ordinal)
            ? segmentedCode
            : ProjectSegmentedCode(segmentedCode, _buffer);
        return TrimSegmentedCodeAfterRawPrefix(display, _sentenceCommittedRawLength);
    }

    private static string ProjectSegmentedCode(string segmentedCode, string rawCode)
    {
        var projected = new List<char>(rawCode.Length + 4);
        int rawIndex = 0;
        foreach (char value in segmentedCode)
        {
            if (value == ' ')
            {
                if (projected.Count > 0 && projected[^1] != ' ' && rawIndex < rawCode.Length)
                {
                    projected.Add(' ');
                }
                continue;
            }
            if (rawIndex >= rawCode.Length)
            {
                break;
            }
            projected.Add(rawCode[rawIndex++]);
        }
        while (rawIndex < rawCode.Length)
        {
            projected.Add(rawCode[rawIndex++]);
        }
        if (projected.Count > 0 && projected[^1] == ' ')
        {
            projected.RemoveAt(projected.Count - 1);
        }
        return new string(projected.ToArray());
    }

    private string? TryAutoCommitSentencePrefix()
    {
        if (!_config.SentenceAutoCommitEnabled || _sentenceAutoCommitSuspended)
        {
            ResetSentenceAutoCommitEvidence();
            return null;
        }

        string evidenceRaw = _sentenceResult.RawCode ?? string.Empty;
        bool currentGeneration = string.Equals(evidenceRaw, _buffer, StringComparison.Ordinal);
        bool immediatelyPreviousGeneration = evidenceRaw.Length + 1 == _buffer.Length &&
            _buffer.StartsWith(evidenceRaw, StringComparison.Ordinal);
        if (!currentGeneration && !immediatelyPreviousGeneration || evidenceRaw.Length <= 4)
        {
            ResetSentenceAutoCommitEvidence();
            return null;
        }

        SentenceEarlyCommitEvidence evidence = _sentenceResult.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
        if (evidence.ConfidenceTruncated || string.IsNullOrEmpty(evidence.Proposal))
        {
            ResetSentenceAutoCommitEvidence();
            return null;
        }

        string proposal = evidence.Proposal;
        if (!evidence.IgnoreNeuralConstraint && string.Equals(_sentenceNeuralAcceptedRaw, evidenceRaw, StringComparison.Ordinal))
        {
            while (proposal.Length > _sentenceCommittedText.Length && !_sentenceNeuralTopText.StartsWith(proposal, StringComparison.Ordinal))
            {
                proposal = RemoveLastTextElement(proposal);
            }
        }

        if (_sentenceResult.Candidates.Length > 0 && _sentenceResult.Candidates[0].SupplementScore > 0)
        {
            string supplementTop = _sentenceResult.Candidates[0].Text ?? string.Empty;
            while (proposal.Length > _sentenceCommittedText.Length && !supplementTop.StartsWith(proposal, StringComparison.Ordinal))
            {
                proposal = RemoveLastTextElement(proposal);
            }
        }

        if (proposal.Length <= _sentenceCommittedText.Length)
        {
            ResetSentenceAutoCommitEvidence();
            return null;
        }

        bool extendsPrevious = _sentenceAutoCommitEvidence.Count > 0 &&
            evidenceRaw.Length == _sentenceAutoCommitEvidence[^1].RawCode.Length + 1 &&
            evidenceRaw.StartsWith(_sentenceAutoCommitEvidence[^1].RawCode, StringComparison.Ordinal);
        if (!extendsPrevious)
        {
            _sentenceAutoCommitEvidence.Clear();
        }
        _sentenceAutoCommitEvidence.Add(new SentenceAutoCommitEvidence(
            evidenceRaw,
            proposal,
            evidence.RawLengths ?? new Dictionary<string, int>(StringComparer.Ordinal)));
        if (_sentenceAutoCommitEvidence.Count > 3)
        {
            _sentenceAutoCommitEvidence.RemoveAt(0);
        }
        if (_sentenceAutoCommitEvidence.Count < 3)
        {
            return null;
        }

        string stableProposal = LongestCommonTextElementPrefix(_sentenceAutoCommitEvidence.Select(item => item.Proposal));
        int committedRawLength = FindStableSentenceRawLength(stableProposal);
        while (stableProposal.Length > _sentenceCommittedText.Length && committedRawLength == 0)
        {
            stableProposal = RemoveLastTextElement(stableProposal);
            committedRawLength = FindStableSentenceRawLength(stableProposal);
        }
        if (committedRawLength <= _sentenceCommittedRawLength || committedRawLength > evidenceRaw.Length)
        {
            return null;
        }

        string commit = stableProposal[_sentenceCommittedText.Length..];
        if (new StringInfo(commit).LengthInTextElements < 1 ||
            evidenceRaw.Length - _sentenceLastAutoCommitRawLength < 3)
        {
            return null;
        }

        _sentenceCommittedText = stableProposal;
        _sentenceCommittedRawLength = committedRawLength;
        _sentenceLastAutoCommitRawLength = committedRawLength;
        ResetSentenceAutoCommitEvidence();
        _sentenceResult = FilterSentenceDecodeResultForCommittedPrefix(_sentenceResult);
        _sentenceGeneration++;
        return commit;
    }

    private int FindStableSentenceRawLength(string text)
    {
        if (string.IsNullOrEmpty(text) || _sentenceAutoCommitEvidence.Count < 3)
        {
            return 0;
        }

        int stableRawLength = 0;
        foreach (SentenceAutoCommitEvidence evidence in _sentenceAutoCommitEvidence)
        {
            if (!evidence.RawLengths.TryGetValue(text, out int rawLength) || rawLength <= 0)
            {
                return 0;
            }
            if (stableRawLength == 0)
            {
                stableRawLength = rawLength;
            }
            else if (stableRawLength != rawLength)
            {
                return 0;
            }
        }
        return stableRawLength;
    }

    private void ResetSentenceAutoCommitEvidence() => _sentenceAutoCommitEvidence.Clear();

    private void ResetSentenceAutoCommitState()
    {
        _sentenceCommittedText = string.Empty;
        _sentenceCommittedRawLength = 0;
        _sentenceLastAutoCommitRawLength = 0;
        _sentenceAutoCommitSuspended = false;
        _sentenceNeuralAcceptedRaw = string.Empty;
        _sentenceNeuralTopText = string.Empty;
        ResetSentenceAutoCommitEvidence();
    }

    private SentenceDecodeResult FilterSentenceDecodeResultForCommittedPrefix(SentenceDecodeResult result)
    {
        if (_sentenceCommittedText.Length == 0 || result is null)
        {
            return result ?? SentenceDecodeResult.Empty;
        }

        SentenceCandidate[] candidates = result.Candidates ?? [];
        SentenceCandidate[] filtered = candidates
            .Where(candidate => candidate?.Text?.StartsWith(_sentenceCommittedText, StringComparison.Ordinal) == true)
            .ToArray();
        if (filtered.Length == candidates.Length)
        {
            return result;
        }

        return new SentenceDecodeResult
        {
            RawCode = result.RawCode,
            Candidates = filtered,
            EarlyCommitEvidence = result.EarlyCommitEvidence,
            ExpandedStates = result.ExpandedStates,
        };
    }

    private string UncommittedSentenceRawCode()
    {
        return _sentenceCommittedRawLength > 0 && _sentenceCommittedRawLength <= _buffer.Length
            ? _buffer[_sentenceCommittedRawLength..]
            : _buffer;
    }

    private static string TrimSegmentedCodeAfterRawPrefix(string segmented, int rawPrefixLength)
    {
        if (string.IsNullOrEmpty(segmented) || rawPrefixLength <= 0)
        {
            return segmented ?? string.Empty;
        }

        int rawCount = 0;
        int index = 0;
        while (index < segmented.Length && rawCount < rawPrefixLength)
        {
            if (segmented[index] != ' ')
            {
                rawCount++;
            }
            index++;
        }
        while (index < segmented.Length && segmented[index] == ' ')
        {
            index++;
        }
        return segmented[index..];
    }

    private static string LongestCommonTextElementPrefix(IEnumerable<string> values)
    {
        string[] texts = values?.ToArray() ?? [];
        if (texts.Length == 0 || texts.Any(string.IsNullOrEmpty))
        {
            return string.Empty;
        }
        string shortest = texts.OrderBy(text => new StringInfo(text).LengthInTextElements).First();
        int[] ends = TextElementEndOffsets(shortest);
        for (int index = ends.Length - 1; index >= 0; index--)
        {
            string prefix = shortest[..ends[index]];
            if (texts.All(text => text.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return prefix;
            }
        }
        return string.Empty;
    }

    private static string RemoveLastTextElement(string text)
    {
        int[] ends = TextElementEndOffsets(text);
        return ends.Length <= 1 ? string.Empty : text[..ends[^2]];
    }

    private static int[] TextElementEndOffsets(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var ends = new List<int>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            ends.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);
        }
        return ends.ToArray();
    }

    private EngineSnapshot? TryProcessQuickSymbolCode(string? text)
    {
        if (text?.Length != 1 || text[0] is not (';' or '/' or '['))
        {
            return null;
        }

        string next = _buffer + text;
        string lookupNext = QuickSymbolLookupCode(next);
        if (!_lexicon.HasPrefix(lookupNext))
        {
            return null;
        }

        _buffer = next;
        _pageIndex = 0;
        if (_lexicon.IsUniqueTerminalCode(lookupNext))
        {
            string commit = ExpandCandidateText(CandidatesForCurrentBuffer()[0]);
            return CommitQuickSymbol(commit);
        }
        return Snapshot(handled: true, commit: null);
    }

    private void RebuildMixedInput()
    {
        int completedLength = _mixedRaw.Length == 0
            ? 0
            : ((_mixedRaw.Length - 1) / _config.MaxCodeLength) * _config.MaxCodeLength;
        foreach (int start in _mixedPreferredCandidateText.Keys.Where(start => start < 0 || start >= completedLength).ToArray())
        {
            _mixedPreferredCandidateText.Remove(start);
        }

        MixedInputDecodeResult result = _mixedDecoder.Decode(
            _mixedRaw,
            _config.MaxCodeLength,
            _mixedPreferredCandidateText);
        _mixedPrefix = result.ResolvedPrefixText;
        _buffer = result.ActiveCode;
    }

    private void RememberMixedPageCandidate()
    {
        if (_buffer.Length != _config.MaxCodeLength)
        {
            return;
        }

        string? candidate = CandidatesForCurrentBuffer()
            .Skip(_pageIndex * _config.PageSize)
            .FirstOrDefault();
        if (candidate is null)
        {
            return;
        }

        int segmentStart = _mixedRaw.Length - _buffer.Length;
        _mixedPreferredCandidateText[segmentStart] = candidate;
    }

    private void ResetComposition()
    {
        _buffer = string.Empty;
        _mixedRaw = string.Empty;
        _mixedPrefix = string.Empty;
        _mixedPreferredCandidateText.Clear();
        _expandedCandidateText.Clear();
        _pageIndex = 0;
        _dotAfterDigitArmed = false;
        _pinyinMode = false;
        _sentenceResult = SentenceDecodeResult.Empty;
        _sentenceResultRawCode = string.Empty;
        _sentenceSelectedIndex = 0;
        _sentenceGeneration++;
        _sentenceDecodePending = false;
        _sentenceNeuralPending = false;
        _sentencePendingCandidates = [];
        ResetSentenceAutoCommitState();
        _quickSymbolDeferredSentenceCommit = string.Empty;
        _quickSymbolDeferredSentence = null;
        _quickSymbolSlashCommand = false;
    }

    private string ExpandOutputText(string text)
    {
        return QuickPhraseExpander.Expand(text, _repeatBuffer);
    }

    private string ExpandCandidateText(string text)
    {
        if (_expandedCandidateText.TryGetValue(text, out string? expanded))
        {
            return expanded;
        }

        expanded = ExpandOutputText(text);
        if (!string.Equals(expanded, text, StringComparison.Ordinal))
        {
            _expandedCandidateText[text] = expanded;
        }
        return expanded;
    }

    private string TrackCommit(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _repeatBuffer = text;
            _dotAfterDigitArmed = text[^1] is >= '0' and <= '9';
        }
        return text;
    }

    private EngineSnapshot CommitAfterReset(string text)
    {
        ResetComposition();
        return CommitSnapshot(text);
    }

    private EngineSnapshot CommitQuickSymbol(string text)
    {
        string deferredSentenceCommit = _quickSymbolDeferredSentenceCommit;
        ResetComposition();
        return CommitSnapshot(deferredSentenceCommit + text);
    }

    private EngineSnapshot CommitSnapshot(string text)
    {
        return TryGetAction(text, out EngineAction action)
            ? EngineSnapshot.Empty(handled: true) with { Action = action }
            : EngineSnapshot.Empty(handled: true) with { Commit = TrackCommit(text) };
    }

    private static bool TryGetAction(string text, out EngineAction action)
    {
        action = text switch
        {
            "{添加}" or "{加词}" => EngineAction.OpenAddWord,
            "{隐藏候选}" => EngineAction.ToggleHideCandidates,
            _ => EngineAction.None,
        };
        return action != EngineAction.None;
    }

    private static bool IsCodeLetter(string? text)
    {
        return text?.Length == 1 && char.IsLetter(text[0]);
    }

    private static bool TryGetChinesePunctuation(
        string? text,
        int modifiers,
        bool useEnglishPunctuation,
        bool slashOutputsDunhao,
        out string punctuation)
    {
        punctuation = string.Empty;
        if (text?.Length != 1)
        {
            return false;
        }

        bool shift = (modifiers & (1 << 0)) != 0;
        if (useEnglishPunctuation)
        {
            punctuation = text[0].ToString();
            return text[0] is ',' or '.' or '/' or '[' or ']' or '`';
        }

        punctuation = text[0] switch
        {
            ',' => shift ? "《" : "，",
            '.' => shift ? "》" : "。",
            '/' => shift ? "？" : slashOutputsDunhao ? "、" : "/",
            '[' => shift ? "{" : "【",
            ']' => shift ? "}" : "】",
            '`' => shift ? "~" : "·",
            _ => string.Empty,
        };
        return punctuation.Length > 0;
    }

    private static bool TryGetShiftDigitPunctuation(InputEvent input, out string punctuation)
    {
        punctuation = string.Empty;
        char digit = input.Text?.Length == 1 && input.Text[0] is >= '0' and <= '9'
            ? input.Text[0]
            : input.PhysicalKey?.Length == "Digit0".Length &&
              input.PhysicalKey.StartsWith("Digit", StringComparison.Ordinal) &&
              input.PhysicalKey[^1] is >= '0' and <= '9'
                ? input.PhysicalKey[^1]
                : '\0';

        punctuation = digit switch
        {
            '0' => "）",
            '1' => "！",
            '2' => "@",
            '3' => "#",
            '4' => "￥",
            '5' => "%",
            '6' => "……",
            '7' => "&",
            '8' => "*",
            '9' => "（",
            _ => string.Empty,
        };
        return punctuation.Length > 0;
    }
}

internal sealed record BasicEngineConfig(
    int MaxCandidates,
    int PageSize,
    int MaxCodeLength,
    bool AutoCommitUniqueTerminalCode,
    bool SecondCandidateSemicolon,
    bool ThirdCandidateQuote,
    bool UnlimitedMixedInput,
    bool UseEnglishPunctuationInChinese,
    bool SlashOutputsDunhao,
    bool TabClearsComposition,
    bool EnterClearsComposition,
    bool PinyinReverseEnabled,
    bool ClearOnNoCode,
    bool SentenceInputEnabled,
    bool SentenceNeuralRerankEnabled,
    bool SentenceAutoCommitEnabled,
    int SentenceOptimalCodeHighFreqLimit,
    bool SentenceAllowDuplicateSingleCharacters,
    string SentenceFullCodeWhitelist,
    string SelectionKeys,
    string PreviousPageKeys,
    string NextPageKeys)
{
    private const string DefaultSentenceFullCodeWhitelist = "便深候整调脸照病增响剑哪微营修愿密脑续假值弹您球激游模静源副座喝富宣呼检救嘴税探脱误释跳睡减蒙镇域洞湾卖暴输缓熟庭俄韩混词授摆诺稳塔潜硬萧侵懂蒋赞赛胸偷烧墙爆操挑撤筑戴植援凭聚凌梁箭圈惨飘旗牌废缩碎挺晓桥赫凝潮掩拔播艘滚兽隆薄愤漫爹撒佩绕";

    public static BasicEngineConfig Default { get; } = new(9, 5, 4, true, true, true, false, false, true, true, false, true, true, false, true, false, 1500, true, DefaultSentenceFullCodeWhitelist, "1234567890", "-", "=");

    public static BasicEngineConfig FromAbi(TcEngineConfig config, string? sentenceFullCodeWhitelist)
    {
        return new BasicEngineConfig(
            Math.Clamp(config.MaxCandidates, 1, 99),
            Math.Clamp(config.PageSize, 1, 10),
            Math.Clamp(config.MaxCodeLength, 1, 16),
            config.AutoCommitUniqueTerminalCode != 0,
            config.SecondCandidateSemicolon != 0,
            config.ThirdCandidateQuote != 0,
            config.UnlimitedMixedInput != 0,
            config.UseEnglishPunctuationInChinese != 0,
            config.SlashOutputsDunhao != 0,
            config.TabClearsComposition != 0,
            config.EnterClearsComposition != 0,
            config.PinyinReverseEnabled != 0,
            config.ClearOnNoCode != 0,
            config.SentenceInputEnabled != 0,
            config.SentenceNeuralRerankEnabled != 0,
            config.SentenceAutoCommitEnabled != 0,
            Math.Max(0, config.SentenceOptimalCodeHighFreqLimit),
            config.SentenceAllowDuplicateSingleCharacters != 0,
            sentenceFullCodeWhitelist ?? string.Empty,
            "1234567890",
            "-",
            "=");
    }

    public BasicEngineConfig WithConfiguredKeys(string? selectionKeys, string? previousPageKeys, string? nextPageKeys)
    {
        return this with
        {
            SelectionKeys = NormalizeCandidateKeys(selectionKeys, "1234567890", allowEmpty: true),
            PreviousPageKeys = NormalizeCandidateKeys(previousPageKeys, "-"),
            NextPageKeys = NormalizeCandidateKeys(nextPageKeys, "="),
        };
    }

    private static string NormalizeCandidateKeys(string? value, string fallback, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && value.Length == 0) || value.Length > 10 || value.Any(char.IsWhiteSpace) || value.Distinct().Count() != value.Length)
        {
            return fallback;
        }

        return value;
    }
}

internal sealed record EngineSnapshot(
    bool Handled,
    string Preedit,
    IReadOnlyList<string> Candidates,
    int SelectedIndex,
    string? Commit,
    bool IsComposing)
{
    public int Caret { get; init; }

    public string CompositionPrefix { get; init; } = string.Empty;

    public string ActiveInputCode { get; init; } = string.Empty;

    public int CandidateTotal { get; init; }

    public int PageIndex { get; init; }

    public int PageCount { get; init; }

    public bool SentenceRerankPending { get; init; }

    public EngineAction Action { get; init; }

    public static EngineSnapshot Empty(bool handled = false)
    {
        return new EngineSnapshot(handled, string.Empty, [], 0, null, false);
    }
}

internal enum EngineAction
{
    None = 0,
    OpenAddWord = 1,
    ToggleHideCandidates = 2,
}

internal readonly record struct InputEvent(
    InputKey Key,
    string? Text,
    int Modifiers,
    KeyAction Action,
    string? PhysicalKey,
    int PhysicalScanCode,
    bool IsExtended,
    bool IsRepeat,
    int RepeatCount);

internal enum KeyAction
{
    KeyDown = 0,
    KeyUp = 1,
}

internal enum InputKey
{
    Unknown = 0,
    Character = 1,
    Backspace = 2,
    Enter = 3,
    Escape = 4,
    Space = 5,
    Tab = 6,
    ArrowUp = 7,
    ArrowDown = 8,
    ArrowLeft = 9,
    ArrowRight = 10,
    Digit = 11,
    Semicolon = 12,
    Quote = 13,
    PagePrevious = 14,
    PageNext = 15,
}
