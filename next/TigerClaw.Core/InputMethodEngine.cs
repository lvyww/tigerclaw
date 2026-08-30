using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace TigerClaw.Core
{
    internal sealed class InputMethodEngine : IDisposable
    {
        private enum CompositionState
        {
            En = 0,
            CnIdle = 1,
            CnComposing = 2,
            CnUpperCase = 3,
            CnPinyin = 4,
            CnSentence = 5
        }

        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_MENU = 0x12;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_CAPITAL = 0x14;
        private const int VK_SPACE = 0x20;
        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_PRIOR = 0x21;
        private const int VK_NEXT = 0x22;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_OEM_1 = 0xBA; // ;:
        private const int VK_OEM_2 = 0xBF; // /?
        private const int VK_OEM_PLUS = 0xBB;
        private const int VK_OEM_MINUS = 0xBD;
        private const int VK_OEM_3 = 0xC0;
        private const int VK_OEM_4 = 0xDB; // [{
        private const int VK_OEM_5 = 0xDC;
        private const int VK_OEM_6 = 0xDD;
        private const int VK_OEM_7 = 0xDE; // '"
        private const int VK_OEM_COMMA = 0xBC; // ,<
        private const int VK_OEM_PERIOD = 0xBE; // .>
        private const int VK_0 = 0x30;
        private const int VK_1 = 0x31;
        private const int VK_9 = 0x39;
        private const int VK_NUMPAD0 = 0x60;
        private const int VK_NUMPAD9 = 0x69;
        private const int VK_A = 0x41;
        private const int VK_M = 0x4D;
        private const int VK_Z = 0x5A;
        private const double SentenceEmittedCharacterReward = 2.0;
        private const double SentenceEarlyCommitStrongShare = 0.99999;

        private sealed class SentenceAutoCommitEvidence
        {
            public string RawCode { get; set; }
            public string Proposal { get; set; }
            public Dictionary<string, int> RawLengths { get; set; }
            public bool Strong { get; set; }
        }

        private sealed class SentenceEmptyCodePending
        {
            public string CandidateText { get; set; }
            public string CommittedText { get; set; }
            public int BaseRawLength { get; set; }
            public int LastSegmentStart { get; set; }
        }

        private readonly object _lock = new object();
        private readonly StringBuilder _inputBuffer = new StringBuilder(64);
        private readonly StringBuilder _mixedRawBuffer = new StringBuilder(128);
        private readonly CoreRuntimeState _state;
        private readonly IMixedInputDecoder _mixedInputDecoder;
        private readonly Dictionary<int, string> _mixedPreferredCandidateText = new Dictionary<int, string>();
        private MixedInputDecodeResult _mixedDecodeResult = MixedInputDecodeResult.Empty;
        private int _mixedDecodedLexiconVersion = -1;
        private int _mixedDecodedMaxCodeLength = -1;
        private readonly StringBuilder _sentenceRawBuffer = new StringBuilder(128);
        private SentenceInputDecoder _sentenceInputDecoder;
        private ISentenceLanguageModel _sentenceLanguageModel;
        private readonly bool _sentenceDecoderExternallyProvided;
        private readonly bool _sentenceDecodeSynchronously;
        private SentenceDecodeResult _sentenceDecodeResult = SentenceDecodeResult.Empty;
        private int _sentenceDecodedLexiconVersion = -1;
        private int _sentenceResultLexiconVersion = -1;
        private int _sentenceSelectedIndex;
        private string _sentenceCommittedText = string.Empty;
        private int _sentenceCommittedRawLength;
        private readonly List<SentenceAutoCommitEvidence> _sentenceAutoCommitEvidence =
            new List<SentenceAutoCommitEvidence>(3);
        private int _sentenceLastAutoCommitRawLength;
        private bool _sentenceAutoCommitSuspended;
        private SentenceEmptyCodePending _sentenceEmptyCodePending;
        private string _sentenceNeuralAcceptedRaw = string.Empty;
        private string _sentenceNeuralTopText = string.Empty;
        private long _sentenceGeneration;
        private ISentenceRerankService _sentenceRerankService;
        private Action _sentenceDecodeCompletedCallback;
        private bool _sentenceDecodeWorkerRunning;
        private readonly Dictionary<int, int> _customSelectionKeyMap = new Dictionary<int, int>();
        private Dictionary<int, List<int>> _customSelectionBindings = BuildDefaultSelectionKeyBindings();
        private readonly HashSet<int> _handledModifierSelectionKeys = new HashSet<int>();
        private static readonly Dictionary<int, string> CnSymbols = new Dictionary<int, string>
        {
            { VK_OEM_PLUS, "=" },
            { VK_OEM_COMMA, "\uFF0C" }, // unicode: ，
            { VK_OEM_MINUS, "-" },
            { VK_OEM_PERIOD, "\u3002" }, // unicode: 。
            { VK_OEM_3, "\u00B7" }, // unicode: ·
            { VK_OEM_4, "\u3010" }, // unicode: 【
            { VK_OEM_5, "\u3001" }, // unicode: 、
            { VK_OEM_6, "\u3011" }, // unicode: 】
            { VK_OEM_2, "\u3001" }, // unicode: 、
            { VK_OEM_1, "\uFF1B" } // unicode: ；
        };
        private static readonly Dictionary<int, string> ShiftCnSymbols = new Dictionary<int, string>
        {
            { 0x30, "\uFF09" }, // unicode: ）
            { 0x31, "\uFF01" }, // unicode: ！
            { 0x32, "@" },
            { 0x33, "#" },
            { 0x34, "\uFFE5" }, // unicode: ￥
            { 0x35, "%" },
            { 0x36, "\u2026\u2026" }, // unicode: ……
            { 0x37, "&" },
            { 0x38, "*" },
            { 0x39, "\uFF08" }, // unicode: （
            { VK_OEM_PLUS, "+" },
            { VK_OEM_COMMA, "\u300A" }, // unicode: 《
            { VK_OEM_MINUS, "\u2014\u2014" }, // unicode: ——
            { VK_OEM_PERIOD, "\u300B" }, // unicode: 》
            { VK_OEM_3, "~" },
            { VK_OEM_4, "{" },
            { VK_OEM_5, "|" },
            { VK_OEM_6, "}" },
            { VK_OEM_2, "\uFF1F" }, // unicode: ？
            { VK_OEM_1, "\uFF1A" } // unicode: ：
        };
        private static readonly Dictionary<int, string> EnSymbols = new Dictionary<int, string>
        {
            { VK_OEM_PLUS, "=" },
            { VK_OEM_COMMA, "," },
            { VK_OEM_MINUS, "-" },
            { VK_OEM_PERIOD, "." },
            { VK_OEM_3, "`" },
            { VK_OEM_4, "[" },
            { VK_OEM_5, "\\" },
            { VK_OEM_6, "]" },
            { VK_OEM_2, "/" },
            { VK_OEM_1, ";" },
            { VK_OEM_7, "'" }
        };
        private static readonly Dictionary<int, string> ShiftEnSymbols = new Dictionary<int, string>
        {
            { 0x30, ")" },
            { 0x31, "!" },
            { 0x32, "@" },
            { 0x33, "#" },
            { 0x34, "$" },
            { 0x35, "%" },
            { 0x36, "^" },
            { 0x37, "&" },
            { 0x38, "*" },
            { 0x39, "(" },
            { VK_OEM_PLUS, "+" },
            { VK_OEM_COMMA, "<" },
            { VK_OEM_MINUS, "_" },
            { VK_OEM_PERIOD, ">" },
            { VK_OEM_3, "~" },
            { VK_OEM_4, "{" },
            { VK_OEM_5, "|" },
            { VK_OEM_6, "}" },
            { VK_OEM_2, "?" },
            { VK_OEM_1, ":" },
            { VK_OEM_7, "\"" }
        };

        private bool _isChinese = true;
        private CompositionState _compositionState = CompositionState.CnIdle;
        private bool _leftShiftDown;
        private bool _rightShiftDown;
        private bool _shiftChordUsed;
        private bool _skipShiftToggleOnce;
        private bool _ctrlChordDown;
        private bool _spaceChordDown;
        private bool _ctrlSpaceArmed;
        private bool _ctrlSpaceSwitched;
        // VK of a one-shot Ctrl/Alt shortcut (switch-schema / add-word / reorder) that has already
        // fired for the current physical press; 0 = none. Reset on that key's key-up. Auto-repeat
        // key-downs and the TSF test+commit double dispatch all carry repeat==1, so this gate (not
        // the repeat count) is what stops a single press from re-firing the action rapidly.
        private int _oneShotActionKey;
        private DateTime _lastCtrlUpUtc = DateTime.MinValue;
        private static readonly TimeSpan CtrlSpaceGrace = TimeSpan.FromMilliseconds(250);
        private bool _leftSingleQuote = true;
        private bool _leftDoubleQuote = true;
        private bool _quoteDownSeen;
        private bool _deletedSingleQuoteArmed;
        private bool _deletedDoubleQuoteArmed;
        private bool _dotAfterDigitArmed;
        private int _candidatePageIndex;
        private string _candidatePageCode = string.Empty;
        private CompositionState _candidatePageState = CompositionState.CnIdle;
        private string _repeatBuffer = "\u91cd\u590d\u4e0a\u5c4f"; // unicode: 重复上屏
        private readonly Random _random = new Random();
        private readonly Stack<string> _sendHistory = new Stack<string>();
        private Timer _manualTimer;

        public InputMethodEngine(
            CoreRuntimeState state,
            SentenceInputDecoder sentenceInputDecoder = null,
            bool? sentenceDecodeSynchronously = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _mixedInputDecoder = new FixedLengthMixedInputDecoder(ResolveCandidates, GetCandidateOutputText);
            _sentenceInputDecoder = sentenceInputDecoder;
            _sentenceDecoderExternallyProvided = sentenceInputDecoder != null;
            _sentenceDecodeSynchronously = sentenceDecodeSynchronously ?? _sentenceDecoderExternallyProvided;
            if (_sentenceDecoderExternallyProvided)
            {
                _sentenceDecodedLexiconVersion = _state.LexiconVersion;
            }
            else if (_state.IsSentenceInputActive())
            {
                ReloadSentenceResources();
            }
            LoadCustomSelectionKeyConfig();
        }

        public void ReloadSentenceResources()
        {
            lock (_lock)
            {
                if (_sentenceDecoderExternallyProvided)
                {
                    return;
                }

                if (!_state.IsSentenceInputActive())
                {
                    _sentenceInputDecoder = null;
                    DisposeSentenceLanguageModel();
                    _sentenceLanguageModel = null;
                    _sentenceDecodedLexiconVersion = -1;
                    return;
                }

                if (_sentenceLanguageModel == null)
                {
                    _sentenceLanguageModel = SentenceNgramModel.LoadAvailable(_state.GetRuntimeBaseDirectory());
                }
                if (_sentenceLanguageModel == null)
                {
                    _sentenceInputDecoder = null;
                    _sentenceDecodedLexiconVersion = _state.LexiconVersion;
                    return;
                }

                SentenceLexiconIndex lexicon = SentenceLexiconIndex.Build(_state.GetSentenceLexiconSnapshot());
                SentenceSupplementMatcher supplementMatcher = SentenceSupplementMatcher.Build(
                    _state.GetSentenceSupplementSnapshot());
                _sentenceInputDecoder = new SentenceInputDecoder(
                    lexicon,
                    _sentenceLanguageModel,
                    emittedCharacterReward: SentenceEmittedCharacterReward,
                    supplementMatcher: supplementMatcher);
                _sentenceDecodedLexiconVersion = _state.LexiconVersion;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _sentenceInputDecoder = null;
                DisposeSentenceLanguageModel();
                _sentenceLanguageModel = null;
            }
        }

        private void DisposeSentenceLanguageModel()
        {
            if (!_sentenceDecoderExternallyProvided && _sentenceLanguageModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public void SetSentenceRerankService(ISentenceRerankService service)
        {
            lock (_lock)
            {
                _sentenceRerankService = service;
            }
        }

        public void SetSentenceDecodeCompletedCallback(Action callback)
        {
            lock (_lock)
            {
                _sentenceDecodeCompletedCallback = callback;
            }
        }

        public bool ApplySentenceNeuralScores(long generation, string rawCode, double[] scores)
        {
            lock (_lock)
            {
                SentenceCandidate[] candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
                int rerankCount = Math.Min(5, candidates.Length);
                if (_compositionState != CompositionState.CnSentence ||
                    generation != _sentenceGeneration ||
                    !string.Equals(rawCode, _sentenceRawBuffer.ToString(), StringComparison.Ordinal) ||
                    scores == null || scores.Length != rerankCount)
                {
                    return false;
                }

                for (int index = 0; index < rerankCount; index++)
                {
                    candidates[index].FinalScore = candidates[index].BaseScore + 0.84 * scores[index];
                }
                Array.Sort(
                    candidates,
                    0,
                    rerankCount,
                    Comparer<SentenceCandidate>.Create(SentenceCandidate.CompareByLexiconRankThenScore));
                _sentenceNeuralAcceptedRaw = rawCode;
                _sentenceNeuralTopText = candidates.Length > 0 ? candidates[0].Text ?? string.Empty : string.Empty;
                _sentenceSelectedIndex = 0;
                return true;
            }
        }

        public bool IsChinese
        {
            get
            {
                lock (_lock)
                {
                    return _isChinese;
                }
            }
        }

        public void SetChinese(bool isChinese, out string textToCommit)
        {
            lock (_lock)
            {
                textToCommit = string.Empty;
                bool currentChinese = _compositionState != CompositionState.En;
                if (currentChinese == isChinese)
                {
                    return;
                }

                if (!isChinese && HasCompositionInput())
                {
                    textToCommit = CommitCodeBuffer();
                    if (!string.IsNullOrEmpty(textToCommit))
                    {
                        AppendSendHistory(textToCommit);
                    }
                    ClearCompositionInput();
                }

                _isChinese = isChinese;
                _compositionState = isChinese ? CompositionState.CnIdle : CompositionState.En;
                ResetCandidatePageTracker();
                _dotAfterDigitArmed = false;
            }
        }

        public void ToggleChinese(out string textToCommit)
        {
            lock (_lock)
            {
                SetChinese(!_isChinese, out textToCommit);
            }
        }

        public void OnFocusChanged()
        {
            lock (_lock)
            {
                ResetCtrlSpaceState();
                ResetShiftToggleState();
                ResetCandidatePageTracker();
                _dotAfterDigitArmed = false;
            }
        }

        public void OnExternalCompositionCanceled()
        {
            lock (_lock)
            {
                ClearCompositionInput();
                _compositionState = _isChinese ? CompositionState.CnIdle : CompositionState.En;
                ResetCandidatePageTracker();
                ResetCtrlSpaceState();
                ResetShiftToggleState();
                _dotAfterDigitArmed = false;
            }
        }

        public bool ResetCompositionForConfigChange()
        {
            lock (_lock)
            {
                bool hadComposition = HasCompositionInput();
                ClearCompositionInput();
                _compositionState = _isChinese ? CompositionState.CnIdle : CompositionState.En;
                ResetCandidatePageTracker();
                _dotAfterDigitArmed = false;
                return hadComposition;
            }
        }

        public void ReloadCustomSelectionKeyConfig()
        {
            lock (_lock)
            {
                LoadCustomSelectionKeyConfig();
            }
        }

        public string GetCustomSelectionKeyConfigText()
        {
            lock (_lock)
            {
                return BuildSelectionKeyBindingsText(_customSelectionBindings);
            }
        }

        public string GetDefaultCustomSelectionKeyConfigText()
        {
            return BuildSelectionKeyBindingsText(BuildDefaultSelectionKeyBindings());
        }

        public bool TrySaveCustomSelectionKeyConfig(string configText, out string error)
        {
            lock (_lock)
            {
                string path = _state.GetCustomSelectionKeyConfigPath();
                try
                {
                    if (!TryParseCustomSelectionKeyConfigLines(
                        EnumerateConfigLines(configText),
                        BuildDefaultSelectionKeyBindings(),
                        out Dictionary<int, List<int>> bindings,
                        out error))
                    {
                        return false;
                    }

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(path, BuildSelectionKeyFileText(bindings), new UTF8Encoding(true));
                    LoadCustomSelectionKeyConfig();
                    error = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        public void PostProcessKey(int vk, string action, KeyEngineResult result, bool shift, bool ctrl, bool alt, bool win, bool capsLock)
        {
            lock (_lock)
            {
                bool isKeyDown = string.Equals(action, "down", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(action, "key_down", StringComparison.OrdinalIgnoreCase);
                if (result != null)
                {
                    NormalizeResultTextActions(result);
                }

                if (isKeyDown &&
                    vk == VK_BACK &&
                    (result == null || !result.Handled) &&
                    _sendHistory.Count > 0)
                {
                    string last = _sendHistory.Pop();
                    if (string.Equals(last, "\u201C", StringComparison.Ordinal) || // unicode: “
                        string.Equals(last, "\u201D", StringComparison.Ordinal)) // unicode: ”
                    {
                        _leftDoubleQuote = !_leftDoubleQuote;
                        _deletedDoubleQuoteArmed = true;
                    }
                    else if (string.Equals(last, "\u2018", StringComparison.Ordinal) || // unicode: ‘
                             string.Equals(last, "\u2019", StringComparison.Ordinal)) // unicode: ’
                    {
                        _leftSingleQuote = !_leftSingleQuote;
                        _deletedSingleQuoteArmed = true;
                    }
                    else
                    {
                        _deletedSingleQuoteArmed = false;
                        _deletedDoubleQuoteArmed = false;
                    }
                }

                if (isKeyDown && (result == null || !result.Handled))
                {
                    AppendGuessedPassThroughHistory(vk, shift, ctrl, alt, win, capsLock);
                }

                if (result != null && !string.IsNullOrEmpty(result.TextToOutput))
                {
                    AppendSendHistory(result.TextToOutput);
                    if (result.Handled)
                    {
                        _repeatBuffer = result.TextToOutput;
                    }
                }

                if (isKeyDown)
                {
                    bool committedDigit = false;
                    if (result != null && !string.IsNullOrEmpty(result.TextToOutput))
                    {
                        committedDigit = IsTextEndingWithDigit(result.TextToOutput);
                    }
                    else if (result == null || !result.Handled)
                    {
                        committedDigit = IsPassThroughDigitKey(vk, shift, ctrl, alt, win);
                    }

                    if (committedDigit)
                    {
                        _dotAfterDigitArmed = true;
                    }
                    else if (!IsModifierKey(vk))
                    {
                        _dotAfterDigitArmed = false;
                    }
                }

                if (_state.GetCoreSendHistoryLogEnabled())
                {
                    PrintSendHistoryDebug(vk, action, result);
                }
            }
        }

        public KeyEngineResult ProcessKey(int vk, int scan, string action, bool shift, bool ctrl, bool alt, bool win, bool capsLock, bool numLock, int repeat, bool extended)
        {
            lock (_lock)
            {
                bool isDown = string.Equals(action, "down", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(action, "key_down", StringComparison.OrdinalIgnoreCase);
                bool isUp = string.Equals(action, "up", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(action, "key_up", StringComparison.OrdinalIgnoreCase);
                int resolvedVk = ResolveSelectionVirtualKey(vk, scan, extended);

                if (!isDown && !isUp)
                {
                    return KeyEngineResult.Pass(_isChinese);
                }

                if (isDown && resolvedVk != VK_OEM_7 && resolvedVk != VK_BACK && !IsModifierKey(resolvedVk))
                {
                    _deletedSingleQuoteArmed = false;
                    _deletedDoubleQuoteArmed = false;
                }

                if (resolvedVk == VK_OEM_7)
                {
                    if (isDown)
                    {
                        _quoteDownSeen = true;
                    }
                    else if (isUp)
                    {
                        bool hadDown = _quoteDownSeen;
                        _quoteDownSeen = false;
                        if (!hadDown &&
                            TryHandleQuoteOnKeyUpFallback(shift, ctrl, alt, win, capsLock, out KeyEngineResult quoteUpFallbackResult))
                        {
                            return quoteUpFallbackResult;
                        }
                    }
                }

                if (TryHandleCtrlSpaceChord(resolvedVk, isDown, isUp, repeat, shift, alt, win, out KeyEngineResult ctrlSpaceResult))
                {
                    return ctrlSpaceResult;
                }

                if (isDown && TryHandleModifierSelectionOnKeyDown(resolvedVk, out KeyEngineResult modifierSelectionResult))
                {
                    if (resolvedVk == VK_SHIFT || resolvedVk == VK_LSHIFT || resolvedVk == VK_RSHIFT)
                    {
                        _skipShiftToggleOnce = true;
                    }

                    return modifierSelectionResult;
                }

                if (isUp && _handledModifierSelectionKeys.Remove(resolvedVk))
                {
                    if (_skipShiftToggleOnce &&
                        (resolvedVk == VK_SHIFT || resolvedVk == VK_LSHIFT || resolvedVk == VK_RSHIFT) &&
                        !_leftShiftDown && !_rightShiftDown)
                    {
                        _skipShiftToggleOnce = false;
                    }

                    return KeyEngineResult.CreateHandled(_isChinese);
                }

                if (HandleShiftStateAndToggle(resolvedVk, isDown, isUp, ctrl, alt, win, out KeyEngineResult shiftResult))
                {
                    return shiftResult;
                }

                if (!IsShiftKey(resolvedVk) && isDown && (_leftShiftDown || _rightShiftDown))
                {
                    _shiftChordUsed = true;
                }

                if (isUp && _oneShotActionKey != 0 && resolvedVk == _oneShotActionKey)
                {
                    // Physical release re-arms the one-shot shortcut for the next press, so a held
                    // key (auto-repeat) fires exactly once and only a real new press fires again.
                    _oneShotActionKey = 0;
                }

                if (!isDown)
                {
                    return KeyEngineResult.Pass(_compositionState != CompositionState.En);
                }

                if (resolvedVk == VK_ESCAPE)
                {
                    // no-op here; escape is handled in state processors.
                }

                if (alt)
                {
                    bool isBareAltKey = resolvedVk == VK_MENU || resolvedVk == VK_LMENU || resolvedVk == VK_RMENU;
                    bool shouldCancelCompositionBeforePass =
                        !isBareAltKey &&
                        _inputBuffer.Length > 0 &&
                        (_compositionState == CompositionState.CnComposing ||
                         _compositionState == CompositionState.CnPinyin ||
                         _compositionState == CompositionState.CnUpperCase ||
                         _compositionState == CompositionState.CnSentence);

                    if (!ctrl && !win && !shift &&
                        _compositionState == CompositionState.CnComposing &&
                        resolvedVk >= VK_1 && resolvedVk <= VK_9)
                    {
                        if (_oneShotActionKey == resolvedVk)
                        {
                            // Auto-repeat / duplicate dispatch of the held number key: consume it,
                            // keep the composition, don't adjust the word order again.
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, _inputBuffer.ToString(), true);
                        }

                        string currentCode = _inputBuffer.ToString();
                        List<string> allCandidates = ResolveCandidates(currentCode);
                        List<string> candidates = GetCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, out _);
                        int index = resolvedVk - VK_1;
                        if (candidates != null && index >= 0 && index < candidates.Count)
                        {
                            _state.TryUserAdvance(currentCode, candidates[index], out _);
                            _oneShotActionKey = resolvedVk;
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, currentCode, true);
                        }
                    }

                    if (shouldCancelCompositionBeforePass)
                    {
                        CancelCompositionStateForPassShortcut();
                    }

                    return KeyEngineResult.Pass(_compositionState != CompositionState.En, null, null, false, shouldCancelCompositionBeforePass);
                }

                if (ctrl)
                {
                    if (resolvedVk == VK_OEM_PLUS && _state.GetCtrlEqualAddCiEnabled())
                    {
                        if (_oneShotActionKey == resolvedVk)
                        {
                            // Auto-repeat / duplicate dispatch of a held Ctrl+=: consume it without
                            // reopening the add-word window (matches the post-open empty composition).
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, null, false);
                        }
                        _oneShotActionKey = resolvedVk;
                        return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, null, false, openAddCiWindow: true);
                    }

                    if (resolvedVk == VK_M && !alt && !win && !shift && _state.GetCtrlMSwitchSchemaEnabled())
                    {
                        bool composing = _inputBuffer.Length > 0 &&
                                         (_compositionState == CompositionState.CnComposing ||
                                          _compositionState == CompositionState.CnPinyin ||
                                          _compositionState == CompositionState.CnUpperCase ||
                                          _compositionState == CompositionState.CnSentence);

                        // One switch per physical press (see _oneShotActionKey). A single Ctrl+m reaches
                        // Core through the TSF test phase plus key-down, and a held key auto-repeats many
                        // key-downs (each repeat==1); without the gate every message would flip the schema
                        // again, producing the back-and-forth oscillation.
                        if (_oneShotActionKey == resolvedVk)
                        {
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, _inputBuffer.ToString(), composing);
                        }

                        if (_state.TrySwitchRecentSchema(out _))
                        {
                            _oneShotActionKey = resolvedVk;
                            ReloadSentenceResources();
                            if (IsMixedInputSession())
                            {
                                _mixedInputDecoder.ClearCache();
                                RebuildMixedInput();
                            }
                            // Keep the in-flight code; the new code table re-resolves candidates on the
                            // next UI snapshot. Reset paging because the candidate list changed.
                            ResetCandidatePageTracker();
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, _inputBuffer.ToString(), composing);
                        }
                        // Could not switch (fewer than two code tables): fall through to normal Ctrl handling.
                    }

                    if (!alt && !win &&
                        _compositionState == CompositionState.CnComposing &&
                        resolvedVk >= VK_1 && resolvedVk <= VK_9)
                    {
                        if (_oneShotActionKey == resolvedVk)
                        {
                            // Auto-repeat / duplicate dispatch of the held number key: consume it,
                            // keep the composition, don't re-top / re-delete.
                            return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, _inputBuffer.ToString(), true);
                        }

                        string currentCode = _inputBuffer.ToString();
                        List<string> allCandidates = ResolveCandidates(currentCode);
                        List<string> candidates = GetCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, out _);
                        int index = resolvedVk - VK_1;
                        if (candidates != null && index >= 0 && index < candidates.Count)
                        {
                            string chosen = candidates[index];
                            bool changed = shift
                                ? _state.TryUserDelete(currentCode, chosen, out _)
                                : _state.TryUserTop(currentCode, chosen, out _);
                            if (changed)
                            {
                                _oneShotActionKey = resolvedVk;
                                return KeyEngineResult.CreateHandled(_compositionState != CompositionState.En, null, currentCode, true);
                            }
                        }
                    }

                    bool isBareControlKey = IsControlKey(resolvedVk);
                    bool shouldCancelCompositionBeforePass =
                        !isBareControlKey &&
                        _inputBuffer.Length > 0 &&
                        (_compositionState == CompositionState.CnComposing ||
                         _compositionState == CompositionState.CnPinyin ||
                         _compositionState == CompositionState.CnUpperCase ||
                         _compositionState == CompositionState.CnSentence);

                    if (shouldCancelCompositionBeforePass)
                    {
                        CancelCompositionStateForPassShortcut();
                    }

                    return KeyEngineResult.Pass(_compositionState != CompositionState.En, null, null, false, shouldCancelCompositionBeforePass);
                }

                if (win)
                {
                    bool isBareWinKey = resolvedVk == VK_LWIN || resolvedVk == VK_RWIN;
                    bool shouldCancelCompositionBeforePass =
                        !isBareWinKey &&
                        _inputBuffer.Length > 0 &&
                        (_compositionState == CompositionState.CnComposing ||
                         _compositionState == CompositionState.CnPinyin ||
                         _compositionState == CompositionState.CnUpperCase ||
                         _compositionState == CompositionState.CnSentence);

                    if (shouldCancelCompositionBeforePass)
                    {
                        CancelCompositionStateForPassShortcut();
                    }

                    return KeyEngineResult.Pass(_compositionState != CompositionState.En, null, null, false, shouldCancelCompositionBeforePass);
                }

                if (resolvedVk == VK_CAPITAL)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        string commit = _compositionState == CompositionState.CnSentence
                            ? CommitCodeBuffer()
                            : CombineMixedCommit(ResolveCommitTextForCurrentState(_inputBuffer.ToString()));
                        ClearCompositionInput();
                        _compositionState = CompositionState.CnIdle;
                        ResetCandidatePageTracker();
                        _isChinese = true;
                        return KeyEngineResult.Pass(_compositionState != CompositionState.En, commit, string.Empty, false);
                    }

                    return KeyEngineResult.Pass(_compositionState != CompositionState.En);
                }

                if (win || capsLock)
                {
                    return KeyEngineResult.Pass(_compositionState != CompositionState.En);
                }

                switch (_compositionState)
                {
                    case CompositionState.En:
                        return KeyEngineResult.Pass(false);
                    case CompositionState.CnIdle:
                        return ProcessCnIdleKeyDown(resolvedVk, shift);
                    case CompositionState.CnComposing:
                        return ProcessCnComposingKeyDown(resolvedVk, shift);
                    case CompositionState.CnUpperCase:
                        return ProcessCnUpperCaseKeyDown(resolvedVk, shift);
                    case CompositionState.CnPinyin:
                        return ProcessCnPinyinKeyDown(resolvedVk, shift);
                    case CompositionState.CnSentence:
                        return ProcessCnSentenceKeyDown(resolvedVk, shift);
                    default:
                        return KeyEngineResult.Pass(_compositionState != CompositionState.En);
                }
            }
        }

        private KeyEngineResult ProcessCnIdleKeyDown(int vk, bool shift)
        {
            _isChinese = true;

            if (vk == VK_SPACE)
            {
                return KeyEngineResult.Pass(true);
            }

            if (_state.IsShortSymbolSemicolonEnabled() && vk == VK_OEM_1 && !shift)
            {
                if (_state.IsAutoShortSymbol(";"))
                {
                    return KeyEngineResult.CreateHandled(true, ResolveCommitText(";"), string.Empty, false);
                }

                _inputBuffer.Clear();
                _inputBuffer.Append(';');
                _compositionState = CompositionState.CnComposing;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (_state.IsShortSymbolSlashEnabled() && vk == VK_OEM_2 && !shift)
            {
                if (_state.IsAutoShortSymbol("/"))
                {
                    return KeyEngineResult.CreateHandled(true, ResolveCommitText("/"), string.Empty, false);
                }

                _inputBuffer.Clear();
                _inputBuffer.Append('/');
                _compositionState = CompositionState.CnComposing;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (_state.IsShortSymbolLBracketEnabled() && vk == VK_OEM_4 && !shift)
            {
                if (_state.IsAutoShortSymbol("["))
                {
                    return KeyEngineResult.CreateHandled(true, ResolveCommitText("["), string.Empty, false);
                }

                _inputBuffer.Clear();
                _inputBuffer.Append('[');
                _compositionState = CompositionState.CnComposing;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (_state.IsShortSymbolZEnabled() && vk == VK_Z && !shift)
            {
                if (_state.IsAutoShortSymbol("z"))
                {
                    return KeyEngineResult.CreateHandled(true, ResolveCommitText("z"), string.Empty, false);
                }

                _inputBuffer.Clear();
                _inputBuffer.Append('z');
                _compositionState = CompositionState.CnComposing;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (shift && ShiftCnSymbols.TryGetValue(vk, out string shiftCn))
            {
                return KeyEngineResult.CreateHandled(true, _state.GetUseEnPuncInCn() && ShiftEnSymbols.TryGetValue(vk, out string shiftEn) ? shiftEn : shiftCn, null, false);
            }

            if (_state.GetBackQueryEnabled() && _state.HasPinyinLexicon() && !shift && vk == VK_OEM_3)
            {
                _inputBuffer.Clear();
                _inputBuffer.Append('\u00B7');
                _compositionState = CompositionState.CnPinyin;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (TryResolveCnSymbolOutput(vk, out string symbolOut))
            {
                return KeyEngineResult.CreateHandled(true, symbolOut, null, false);
            }

            if (vk == VK_OEM_7)
            {
                if (shift)
                {
                    return KeyEngineResult.CreateHandled(true, EmitSmartQuote(isDoubleQuote: true), null, false);
                }

                return KeyEngineResult.CreateHandled(true, EmitSmartQuote(isDoubleQuote: false), null, false);
            }

            if (vk >= VK_A && vk <= VK_Z && shift)
            {
                _inputBuffer.Clear();
                _inputBuffer.Append((char)('A' + (vk - VK_A)));
                _compositionState = CompositionState.CnUpperCase;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (vk == VK_RCONTROL)
            {
                _compositionState = CompositionState.En;
                _isChinese = false;
                return KeyEngineResult.CreateHandled(false);
            }

            if (vk >= VK_0 && vk <= VK_9)
            {
                return KeyEngineResult.Pass(true);
            }

            if (TryMapIdleCodeChar(vk, shift, out char idleCodeChar))
            {
                if (_state.IsSentenceInputActive() && _sentenceInputDecoder != null)
                {
                    _compositionState = CompositionState.CnSentence;
                    StartSentenceInput(idleCodeChar);
                    return KeyEngineResult.CreateHandled(_isChinese, null, _sentenceRawBuffer.ToString(), true);
                }

                if (_state.GetUnlimitedMixedChineseEnglishInput())
                {
                    StartMixedInput(idleCodeChar);
                    _compositionState = CompositionState.CnComposing;
                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                _inputBuffer.Append(idleCodeChar);
                int maxCodeLen = GetSafeMaxCodeLen();
                string currentCode = _inputBuffer.ToString();
                if (_state.GetMaxCodeAutoCommit() && maxCodeLen == 1 && _state.IsUniqueTerminalCode(currentCode))
                {
                    string commit = ResolveCommitTextForCurrentState(currentCode);
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(_isChinese, commit, string.Empty, false);
                }

                _compositionState = CompositionState.CnComposing;
                return KeyEngineResult.CreateHandled(_isChinese, null, currentCode, true);
            }

            if (vk == VK_RETURN)
            {
                return KeyEngineResult.Pass(true, "\n", null, false);
            }

            return KeyEngineResult.Pass(true);
        }

        private KeyEngineResult ProcessCnComposingKeyDown(int vk, bool shift)
        {
            string currentCode = _inputBuffer.ToString();
            List<string> allCandidates = ResolveCandidates(currentCode);
            List<string> candidates = GetCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, out _);
            bool hasAnyCandidate = allCandidates != null && allCandidates.Count > 0;
            bool hasPageCandidate = candidates != null && candidates.Count > 0;
            bool isMixedInputSession = IsMixedInputSession();
            bool hasMixedResolvedPrefix = !string.IsNullOrEmpty(GetMixedResolvedPrefixText());

            if (shift && ShiftCnSymbols.ContainsKey(vk))
            {
                if (hasPageCandidate || hasMixedResolvedPrefix || isMixedInputSession)
                {
                    string activeOutput = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : string.Empty;
                    string symbol = _state.GetUseEnPuncInCn() && ShiftEnSymbols.TryGetValue(vk, out string shiftEn) ? shiftEn : ShiftCnSymbols[vk];
                    return CompleteCnComposition(activeOutput, symbol);
                }

                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            if (!shift && TryHandleSelectionKeyInCnComposing(vk, candidates, out KeyEngineResult selectionResult))
            {
                return selectionResult;
            }

            if (_state.IsShortSymbolLBracketEnabled() && string.Equals(currentCode, "[", StringComparison.Ordinal) &&
                (vk == VK_OEM_4 || (vk == VK_SPACE && !hasAnyCandidate)))
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, "\u3010", string.Empty, false); // unicode: 【
            }

            if (IsNextPageKey(vk, shift))
            {
                MoveCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, +1);
                return KeyEngineResult.CreateHandled(true, null, currentCode, true);
            }

            if (IsPrevPageKey(vk, shift))
            {
                MoveCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, -1);
                return KeyEngineResult.CreateHandled(true, null, currentCode, true);
            }

            if (vk == VK_BACK)
            {
                if (IsMixedInputSession())
                {
                    BackspaceMixedInput();
                    if (_mixedRawBuffer.Length == 0)
                    {
                        _compositionState = CompositionState.CnIdle;
                        ResetCandidatePageTracker();
                        return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                    }

                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Length -= 1;
                }

                if (_inputBuffer.Length == 0)
                {
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
            }

            if (vk == VK_ESCAPE)
            {
                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(_isChinese, null, string.Empty, false);
            }

            if (vk == VK_RETURN)
            {
                string output = _state.GetEnterClear() ? string.Empty : CommitCodeBuffer();
                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(_isChinese, output, string.Empty, false);
            }

            if (vk == VK_TAB)
            {
                if (_state.GetTabClear())
                {
                    ClearCompositionInput();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(_isChinese, null, string.Empty, false);
                }

                return KeyEngineResult.Pass(_isChinese);
            }

            if (TryHandleQuickSymbol(vk, currentCode, candidates, out KeyEngineResult quickResult))
            {
                return quickResult;
            }

            if (vk == VK_SPACE)
            {
                string commitSpace = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : null;
                return CompleteCnComposition(commitSpace);
            }

            if (_state.GetSecondCandidateSemicolon() && vk == VK_OEM_1 && !shift && hasPageCandidate && candidates.Count >= 2)
            {
                string commitSecond = GetCandidateOutputText(candidates[1]);
                return CompleteCnComposition(commitSecond);
            }

            if (_state.GetThirdCandidateQuote() && vk == VK_OEM_7 && !shift && hasPageCandidate && candidates.Count >= 3)
            {
                string commitThird = GetCandidateOutputText(candidates[2]);
                return CompleteCnComposition(commitThird);
            }

            if (!shift && vk >= VK_0 && vk <= VK_9)
            {
                int num = vk - VK_1 + 1;
                string activeOutput = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : string.Empty;
                return CompleteCnComposition(activeOutput, num.ToString(CultureInfo.InvariantCulture));
            }

            if (TryResolveCnSymbolOutput(vk, out _))
            {
                return HandleCnSymbolAfterCandidateCommit(vk, shift, hasPageCandidate ? candidates[0] : null);
            }

            if (vk == VK_OEM_7)
            {
                if (hasPageCandidate || hasMixedResolvedPrefix || isMixedInputSession)
                {
                    string quoteOut = EmitSmartQuote(shift);
                    string activeOutput = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : string.Empty;
                    return CompleteCnComposition(activeOutput, quoteOut);
                }

                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            if (TryMapLetter(vk, out char nextLetter))
            {
                if (IsMixedInputSession())
                {
                    char mixedLetter = shift ? char.ToUpperInvariant(nextLetter) : nextLetter;
                    AppendMixedInput(mixedLetter, candidates);
                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                int maxCodeLen = GetSafeMaxCodeLen();
                string extendedCode = currentCode + nextLetter;

                if ((_state.IsShortSymbolSemicolonEnabled() || _state.IsShortSymbolSlashEnabled() || _state.IsShortSymbolLBracketEnabled() || _state.IsShortSymbolZEnabled()) &&
                    _state.IsAutoShortSymbol(extendedCode))
                {
                    string autoOutput = ResolveCommitTextForCurrentState(extendedCode);
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, autoOutput, string.Empty, false);
                }

                if (currentCode.Length <= maxCodeLen - 2)
                {
                    _inputBuffer.Append(nextLetter);
                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                if (currentCode.Length == maxCodeLen - 1)
                {
                    if (_state.GetMaxCodeAutoCommit() && _state.IsUniqueTerminalCode(extendedCode))
                    {
                        string autoCommit = ResolveCommitTextForCurrentState(extendedCode);
                        _inputBuffer.Clear();
                        _compositionState = CompositionState.CnIdle;
                        ResetCandidatePageTracker();
                        return KeyEngineResult.CreateHandled(_isChinese, autoCommit, string.Empty, false);
                    }

                    _inputBuffer.Append(nextLetter);
                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                if (_state.GetMaxCodeAutoCommit() && _state.IsUniqueTerminalCode(extendedCode))
                {
                    string autoCommitAtMax = ResolveCommitTextForCurrentState(extendedCode);
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(_isChinese, autoCommitAtMax, string.Empty, false);
                }

                if (_state.HasCode(extendedCode) || _state.IsNonTerminalCode(extendedCode))
                {
                    _inputBuffer.Append(nextLetter);
                    return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
                }

                string dingCommit = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : null;
                if (_state.GetClearOnNoCode())
                {
                    _inputBuffer.Clear();
                    _inputBuffer.Append(nextLetter);
                    return KeyEngineResult.CreateHandled(_isChinese, dingCommit, _inputBuffer.ToString(), true);
                }

                if (hasPageCandidate)
                {
                    _inputBuffer.Clear();
                    _inputBuffer.Append(nextLetter);
                    return KeyEngineResult.CreateHandled(_isChinese, dingCommit, _inputBuffer.ToString(), true);
                }

                _inputBuffer.Append(nextLetter);
                return KeyEngineResult.CreateHandled(_isChinese, null, _inputBuffer.ToString(), true);
            }

            return KeyEngineResult.Pass(_isChinese);
        }

        public bool IsSentenceCompositionActive
        {
            get
            {
                lock (_lock)
                {
                    return _compositionState == CompositionState.CnSentence && _sentenceRawBuffer.Length > 0;
                }
            }
        }

        public bool IsSentenceDecodePending
        {
            get
            {
                lock (_lock)
                {
                    if (_compositionState != CompositionState.CnSentence || _sentenceRawBuffer.Length == 0)
                    {
                        return false;
                    }

                    return _sentenceDecodeWorkerRunning ||
                           _sentenceResultLexiconVersion != _state.LexiconVersion ||
                           !string.Equals(
                               _sentenceDecodeResult.RawCode,
                               _sentenceRawBuffer.ToString(),
                               StringComparison.Ordinal);
                }
            }
        }

        private KeyEngineResult ProcessCnSentenceKeyDown(int vk, bool shift)
        {
            if (vk == VK_BACK)
            {
                ResetSentenceAutoCommitEvidence();
                ResetSentenceEmptyCodePending();
                // The raw buffer retains the committed prefix so the decoder
                // can keep its full context, but that prefix no longer belongs
                // to the active TSF composition. Backspace must consume only
                // the live tail; once the tail is empty, end the composition
                // instead of replaying backspace over already committed text.
                if (_sentenceCommittedRawLength > 0 &&
                    _sentenceRawBuffer.Length <= _sentenceCommittedRawLength + 1)
                {
                    ClearCompositionInput();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                if (_sentenceRawBuffer.Length > 0)
                {
                    _sentenceRawBuffer.Length -= 1;
                }

                if (_sentenceCommittedRawLength > _sentenceRawBuffer.Length)
                {
                    // The host cannot retract committed text. Keep it excluded
                    // from the remaining composition and stop proposing more.
                    _sentenceCommittedRawLength = _sentenceRawBuffer.Length;
                }

                if (_sentenceRawBuffer.Length == 0)
                {
                    ClearCompositionInput();
                    _compositionState = CompositionState.CnIdle;
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                RebuildSentenceInput();
                return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
            }

            if (vk == VK_ESCAPE)
            {
                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            if (vk == VK_RETURN)
            {
                string remainingRaw = _sentenceRawBuffer.ToString();
                if (_sentenceCommittedRawLength > 0 && _sentenceCommittedRawLength <= remainingRaw.Length)
                {
                    remainingRaw = remainingRaw.Substring(_sentenceCommittedRawLength);
                }
                string output = _state.GetEnterClear() ? string.Empty : remainingRaw;
                ClearCompositionInput();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, output.Length > 0 ? output : null, string.Empty, false);
            }

            if (vk == VK_TAB)
            {
                ResetSentenceEmptyCodePending();
                EnsureSentenceDecodeCurrent();
                if (HasSentenceCandidates())
                {
                    _sentenceAutoCommitSuspended = true;
                    MoveSentenceSelection(shift ? -1 : 1);
                    return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
                }

                if (_state.GetTabClear())
                {
                    ClearCompositionInput();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
            }

            if (vk == VK_SPACE)
            {
                ResetSentenceEmptyCodePending();
                EnsureSentenceDecodeCurrent();
                if (!HasSentenceCandidates())
                {
                    return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
                }

                return CompleteSentenceCandidate(_sentenceSelectedIndex);
            }

            if (vk == VK_UP || vk == VK_DOWN)
            {
                ResetSentenceEmptyCodePending();
                EnsureSentenceDecodeCurrent();
                _sentenceAutoCommitSuspended = true;
                MoveSentenceSelection(vk == VK_UP ? -1 : 1);
                return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
            }

            if (!shift && vk == VK_OEM_1 && _state.GetSecondCandidateSemicolon())
            {
                string commit = AppendSentenceInput(';');
                return KeyEngineResult.CreateHandled(true, commit, GetSentenceDisplayCode(), true);
            }

            if (!shift && vk == VK_OEM_7 && _state.GetThirdCandidateQuote())
            {
                string commit = AppendSentenceInput('\'');
                return KeyEngineResult.CreateHandled(true, commit, GetSentenceDisplayCode(), true);
            }

            if (!shift && vk >= VK_0 && vk <= VK_9)
            {
                string commit = AppendSentenceInput((char)('0' + (vk - VK_0)));
                return KeyEngineResult.CreateHandled(true, commit, GetSentenceDisplayCode(), true);
            }

            if (!shift && vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9)
            {
                string commit = AppendSentenceInput((char)('0' + (vk - VK_NUMPAD0)));
                return KeyEngineResult.CreateHandled(true, commit, GetSentenceDisplayCode(), true);
            }

            if (TryMapLetter(vk, out char letter))
            {
                string commit = AppendSentenceInput(letter);
                return KeyEngineResult.CreateHandled(true, commit, GetSentenceDisplayCode(), true);
            }

            if (vk == VK_OEM_7)
            {
                return CompleteSentenceWithSuffix(EmitSmartQuote(shift));
            }

            if (shift && ShiftCnSymbols.TryGetValue(vk, out string shiftSymbol))
            {
                return CompleteSentenceWithSuffix(
                    _state.GetUseEnPuncInCn() && ShiftEnSymbols.TryGetValue(vk, out string shiftEnglish)
                        ? shiftEnglish
                        : shiftSymbol);
            }

            if (TryResolveCnSymbolOutput(vk, out string symbol))
            {
                return CompleteSentenceWithSuffix(symbol);
            }

            return KeyEngineResult.Pass(true);
        }

        private KeyEngineResult ProcessCnPinyinKeyDown(int vk, bool shift)
        {
            string currentCode = _inputBuffer.ToString();
            string pinyinCode = currentCode.Length > 0 ? currentCode.Substring(1) : string.Empty;
            List<string> allCandidates = ResolvePinyinCandidates(pinyinCode);
            List<string> candidates = GetCandidatePage(currentCode, CompositionState.CnPinyin, allCandidates, out _);
            bool hasAnyCandidate = allCandidates != null && allCandidates.Count > 0;
            bool hasPageCandidate = candidates != null && candidates.Count > 0;

            if (shift && ShiftCnSymbols.ContainsKey(vk))
            {
                if (hasPageCandidate)
                {
                    string output = GetCandidateOutputText(candidates[0]) + (_state.GetUseEnPuncInCn() && ShiftEnSymbols.TryGetValue(vk, out string shiftEn) ? shiftEn : ShiftCnSymbols[vk]);
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
                }

                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            if (IsNextPageKey(vk, shift))
            {
                MoveCandidatePage(currentCode, CompositionState.CnPinyin, allCandidates, +1);
                return KeyEngineResult.CreateHandled(true, null, currentCode, true);
            }

            if (IsPrevPageKey(vk, shift))
            {
                MoveCandidatePage(currentCode, CompositionState.CnPinyin, allCandidates, -1);
                return KeyEngineResult.CreateHandled(true, null, currentCode, true);
            }

            if (_state.GetBackQueryEnabled() && _state.HasPinyinLexicon() && currentCode == "\u00B7" && vk == VK_OEM_3) // unicode: ·
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, "\u00B7", string.Empty, false); // unicode: ·
            }

            if (_state.IsShortSymbolSemicolonEnabled() && string.Equals(currentCode, ";", StringComparison.Ordinal) &&
                (vk == VK_OEM_1 || (vk == VK_SPACE && !hasAnyCandidate)))
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, "\uFF1B", string.Empty, false); // unicode: ；
            }

            if (_state.IsShortSymbolSlashEnabled() && string.Equals(currentCode, "/", StringComparison.Ordinal) &&
                (vk == VK_OEM_2 || (vk == VK_SPACE && !hasAnyCandidate)))
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, "\u3001", string.Empty, false); // unicode: 、
            }

            if (vk == VK_SPACE)
            {
                string commitSpace = hasPageCandidate ? GetCandidateOutputText(candidates[0]) : null;
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, commitSpace, string.Empty, false);
            }

            if (_state.GetSecondCandidateSemicolon() && vk == VK_OEM_1 && !shift && hasPageCandidate && candidates.Count >= 2)
            {
                string commitSecond = GetCandidateOutputText(candidates[1]);
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, commitSecond, string.Empty, false);
            }

            if (_state.GetThirdCandidateQuote() && vk == VK_OEM_7 && !shift && hasPageCandidate && candidates.Count >= 3)
            {
                string commitThird = GetCandidateOutputText(candidates[2]);
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, commitThird, string.Empty, false);
            }

            if (!shift && TryHandleSelectionKeyInCnComposing(vk, candidates, out KeyEngineResult selectionResult))
            {
                return selectionResult;
            }

            if (TryResolveCnSymbolOutput(vk, out _))
            {
                return HandleCnSymbolAfterCandidateCommit(vk, shift, hasPageCandidate ? candidates[0] : null);
            }

            if (vk == VK_OEM_7)
            {
                if (hasPageCandidate)
                {
                    string quoteOut = EmitSmartQuote(shift);

                    string output = GetCandidateOutputText(candidates[0]) + quoteOut;
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
                }

                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            if (TryMapLetter(vk, out char letter))
            {
                _inputBuffer.Append(letter);
                _compositionState = CompositionState.CnPinyin;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (!shift && vk >= VK_0 && vk <= VK_9)
            {
                int num = vk - VK_1 + 1;
                string output = (hasPageCandidate ? GetCandidateOutputText(candidates[0]) : string.Empty) + num.ToString(CultureInfo.InvariantCulture);
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
            }

            if (vk == VK_BACK)
            {
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Length -= 1;
                }

                if (_inputBuffer.Length <= 1)
                {
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                _compositionState = CompositionState.CnPinyin;
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (vk == VK_RETURN)
            {
                string output = _state.GetEnterClear() ? string.Empty : currentCode;
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
            }

            if (vk == VK_TAB)
            {
                if (_state.GetTabClear())
                {
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    ResetCandidatePageTracker();
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                return KeyEngineResult.Pass(true);
            }

            if (vk == VK_ESCAPE)
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                ResetCandidatePageTracker();
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            return KeyEngineResult.Pass(true);
        }

        private KeyEngineResult ProcessCnUpperCaseKeyDown(int vk, bool shift)
        {
            string currentCode = _inputBuffer.ToString();
            if (vk == VK_SPACE || vk == VK_RETURN)
            {
                string output = CommitUpperCaseCode(currentCode);
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
            }

            if (vk == VK_TAB)
            {
                if (_state.GetTabClear())
                {
                    _inputBuffer.Clear();
                    _compositionState = CompositionState.CnIdle;
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                return KeyEngineResult.Pass(true);
            }

            if (shift && ShiftEnSymbols.TryGetValue(vk, out string shiftEnSymbol))
            {
                string output = CommitUpperCaseCode(currentCode) + shiftEnSymbol;
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
            }

            if (EnSymbols.ContainsKey(vk))
            {
                if ((vk == VK_OEM_PERIOD || vk == VK_OEM_COMMA) && IsTimerOrCnum(currentCode))
                {
                    _inputBuffer.Append(EnSymbols[vk]);
                    return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
                }

                string output = CommitUpperCaseCode(currentCode) + EnSymbols[vk];
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
            }

            if (vk >= VK_0 && vk <= VK_9)
            {
                _inputBuffer.Append((char)vk);
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (vk >= VK_A && vk <= VK_Z)
            {
                _inputBuffer.Append(shift ? (char)('A' + (vk - VK_A)) : (char)('a' + (vk - VK_A)));
                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (vk == VK_BACK)
            {
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Length -= 1;
                }
                if (_inputBuffer.Length == 0)
                {
                    _compositionState = CompositionState.CnIdle;
                    return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                }

                return KeyEngineResult.CreateHandled(true, null, _inputBuffer.ToString(), true);
            }

            if (vk == VK_ESCAPE)
            {
                _inputBuffer.Clear();
                _compositionState = CompositionState.CnIdle;
                return KeyEngineResult.CreateHandled(true, null, string.Empty, false);
            }

            return KeyEngineResult.Pass(true);
        }

        private bool TryHandleQuoteOnKeyUpFallback(bool shift, bool ctrl, bool alt, bool win, bool capsLock, out KeyEngineResult result)
        {
            result = null;

            if (ctrl || alt || win || capsLock)
            {
                return false;
            }

            switch (_compositionState)
            {
                case CompositionState.CnIdle:
                    {
                        if (shift)
                        {
                            result = KeyEngineResult.CreateHandled(true, EmitSmartQuote(isDoubleQuote: true), null, false);
                            return true;
                        }

                        result = KeyEngineResult.CreateHandled(true, EmitSmartQuote(isDoubleQuote: false), null, false);
                        return true;
                    }
                case CompositionState.CnComposing:
                    {
                        string code = _inputBuffer.ToString();
                        List<string> allCandidates = ResolveCandidates(code);
                        List<string> candidates = GetCandidatePage(code, CompositionState.CnComposing, allCandidates, out _);
                        if (candidates != null && candidates.Count > 0)
                        {
                            string quoteOut = EmitSmartQuote(shift);

                            string output = GetCandidateOutputText(candidates[0]) + quoteOut;
                            _inputBuffer.Clear();
                            _compositionState = CompositionState.CnIdle;
                            ResetCandidatePageTracker();
                            result = KeyEngineResult.CreateHandled(true, output, string.Empty, false);
                            return true;
                        }

                        _inputBuffer.Clear();
                        _compositionState = CompositionState.CnIdle;
                        ResetCandidatePageTracker();
                        result = KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                        return true;
                    }
                case CompositionState.CnPinyin:
                    {
                        string code = _inputBuffer.ToString();
                        string pyCode = code.Length > 0 ? code.Substring(1) : string.Empty;
                        List<string> allCandidates = ResolvePinyinCandidates(pyCode);
                        List<string> candidates = GetCandidatePage(code, CompositionState.CnPinyin, allCandidates, out _);
                        if (candidates != null && candidates.Count > 0)
                        {
                            string quoteOut = EmitSmartQuote(shift);

                            string output = GetCandidateOutputText(candidates[0]) + quoteOut;
                            _inputBuffer.Clear();
                            _compositionState = CompositionState.CnIdle;
                            ResetCandidatePageTracker();
                            result = KeyEngineResult.CreateHandled(true, output, string.Empty, false);
                            return true;
                        }

                        _inputBuffer.Clear();
                        _compositionState = CompositionState.CnIdle;
                        ResetCandidatePageTracker();
                        result = KeyEngineResult.CreateHandled(true, null, string.Empty, false);
                        return true;
                    }
                default:
                    return false;
            }
        }

        private void ScheduleManualTimer(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 3)
            {
                return;
            }

            string minutesText = code.Substring(2).Replace(",", string.Empty).Replace(" ", string.Empty);
            if (minutesText.Length == 0)
            {
                return;
            }

            if (!double.TryParse(minutesText, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) &&
                !double.TryParse(minutesText, NumberStyles.Float, CultureInfo.CurrentCulture, out minutes))
            {
                return;
            }

            if (minutes <= 0)
            {
                return;
            }

            double dueMs = minutes * 60d * 1000d;
            if (double.IsNaN(dueMs) || double.IsInfinity(dueMs) || dueMs > int.MaxValue)
            {
                dueMs = int.MaxValue;
            }

            if (_manualTimer != null)
            {
                _manualTimer.Dispose();
                _manualTimer = null;
            }

            _manualTimer = new Timer(_ => ShowManualTimerPopup(), null, (int)dueMs, Timeout.Infinite);
        }

        private static void ShowManualTimerPopup()
        {
            try
            {
                Forms.MessageBox.Show(
                    "\u65F6\u95F4\u5DEE\u4E0D\u591A\u54AF\uFF01", // unicode: 时间差不多咯！
                    "\u8BA1\u65F6\u5668", // unicode: 计时器
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information,
                    Forms.MessageBoxDefaultButton.Button1,
                    Forms.MessageBoxOptions.DefaultDesktopOnly);
            }
            catch
            {
            }
        }

        private void AppendSendHistory(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            StringInfo info = new StringInfo(text);
            for (int i = 0; i < info.LengthInTextElements; i++)
            {
                _sendHistory.Push(info.SubstringByTextElements(i, 1));
            }
        }

        private void AppendGuessedPassThroughHistory(int vk, bool shift, bool ctrl, bool alt, bool win, bool capsLock)
        {
            if (ctrl || alt || win)
            {
                return;
            }

            if (TryGuessPassThroughText(vk, shift, capsLock, out string text))
            {
                AppendSendHistory(text);
            }
        }

        private static bool TryGuessPassThroughText(int vk, bool shift, bool capsLock, out string text)
        {
            text = null;

            if (vk == VK_SPACE)
            {
                text = " ";
                return true;
            }

            if (vk >= VK_A && vk <= VK_Z)
            {
                bool upper = shift ^ capsLock;
                text = ((char)((upper ? 'A' : 'a') + (vk - VK_A))).ToString();
                return true;
            }

            if (vk >= VK_0 && vk <= VK_9)
            {
                if (!shift)
                {
                    text = ((char)vk).ToString();
                    return true;
                }

                switch (vk)
                {
                    case VK_0: text = ")"; return true;
                    case VK_1: text = "!"; return true;
                    case VK_1 + 1: text = "@"; return true;
                    case VK_1 + 2: text = "#"; return true;
                    case VK_1 + 3: text = "$"; return true;
                    case VK_1 + 4: text = "%"; return true;
                    case VK_1 + 5: text = "^"; return true;
                    case VK_1 + 6: text = "&"; return true;
                    case VK_1 + 7: text = "*"; return true;
                    case VK_1 + 8: text = "("; return true;
                }
            }

            if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9)
            {
                text = ((char)('0' + (vk - VK_NUMPAD0))).ToString();
                return true;
            }

            if (shift)
            {
                if (ShiftEnSymbols.TryGetValue(vk, out string shiftText))
                {
                    text = shiftText;
                    return true;
                }
            }
            else
            {
                if (EnSymbols.TryGetValue(vk, out string plainText))
                {
                    text = plainText;
                    return true;
                }
            }

            return false;
        }

        private void PrintSendHistoryDebug(int vk, string action, KeyEngineResult result)
        {
#if !DEBUG
            return;
#else
            try
            {
                string[] snapshot = _sendHistory.ToArray();
                int take = Math.Min(snapshot.Length, 40);
                var sb = new StringBuilder(96);
                for (int i = 0; i < take; i++)
                {
                    string item = snapshot[i]
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n");
                    sb.Append(item);
                }

                if (snapshot.Length > take)
                {
                    sb.Append("...");
                }

                Console.WriteLine(
                    "[SendHistory] action={0} vk=0x{1:X2} handled={2} count={3} tail(top->old)={4}",
                    action ?? string.Empty,
                    vk,
                    result != null && result.Handled,
                    _sendHistory.Count,
                    sb.ToString());
            }
            catch
            {
            }
#endif
        }

        private bool HasSendHistoryLast(string value)
        {
            if (_sendHistory.Count <= 0)
            {
                return false;
            }

            return string.Equals(_sendHistory.Peek(), value, StringComparison.Ordinal);
        }

        private static bool IsDigitChar(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= '\uFF10' && ch <= '\uFF19');
        }

        private static bool IsTextEndingWithDigit(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string last = new StringInfo(text).SubstringByTextElements(new StringInfo(text).LengthInTextElements - 1, 1);
            return last.Length == 1 && IsDigitChar(last[0]);
        }

        private static bool IsPassThroughDigitKey(int vk, bool shift, bool ctrl, bool alt, bool win)
        {
            if (ctrl || alt || win || shift)
            {
                return false;
            }

            if (vk >= VK_0 && vk <= VK_9)
            {
                return true;
            }

            return vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9;
        }

        private bool ShouldForceLeftQuoteByColon()
        {
            return HasSendHistoryLast("\uFF1A") || HasSendHistoryLast(":"); // unicode: ：
        }

        private string EmitSmartQuote(bool isDoubleQuote)
        {
            bool leftFlag = isDoubleQuote ? _leftDoubleQuote : _leftSingleQuote;
            bool deletedArmed = isDoubleQuote ? _deletedDoubleQuoteArmed : _deletedSingleQuoteArmed;
            bool forceLeftByColon = ShouldForceLeftQuoteByColon() && !deletedArmed;

            bool emitLeft;
            if (forceLeftByColon)
            {
                emitLeft = true;
            }
            else if (deletedArmed)
            {
                emitLeft = !leftFlag;
            }
            else
            {
                emitLeft = leftFlag;
            }

            if (isDoubleQuote)
            {
                _leftDoubleQuote = !emitLeft;
                _deletedDoubleQuoteArmed = false;
                return emitLeft ? "\u201C" : "\u201D"; // unicode: “ ; ”
            }

            _leftSingleQuote = !emitLeft;
            _deletedSingleQuoteArmed = false;
            return emitLeft ? "\u2018" : "\u2019"; // unicode: ‘ ; ’
        }

        private static bool IsTimerOrCnum(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            if (code.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                return code.Length > 1 && double.TryParse(code.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }

            if (code.StartsWith("S", StringComparison.Ordinal))
            {
                return code.Length > 1 && double.TryParse(code.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }

            return false;
        }

        private static readonly Regex TimerRegex = new Regex(@"^D[sS]([0-9,]*[.])?[0-9,]+$", RegexOptions.Compiled);
        private static readonly Regex CnumRegex = new Regex(@"^S([0-9,]*[.])?[0-9,]+$", RegexOptions.Compiled);

        private string CommitUpperCaseCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.Empty;
            }

            if (TimerRegex.IsMatch(code))
            {
                ScheduleManualTimer(code);
                return string.Empty;
            }

            if (CnumRegex.IsMatch(code))
            {
                return ConvertToChineseCurrency(code);
            }

            return code;
        }

        private static string ConvertToChineseCurrency(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 2)
            {
                return "\u6570\u5b57\u683c\u5f0f\u9519\u8bef!"; // unicode: 数字格式错误!
            }

            string numberText = code.Substring(1).Replace(",", string.Empty).Replace(" ", string.Empty);
            if (!decimal.TryParse(numberText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal dec) || dec < 0)
            {
                return "\u6570\u5b57\u683c\u5f0f\u9519\u8bef!"; // unicode: 数字格式错误!
            }

            string s = dec.ToString("#L#E#D#C#K#E#D#C#J#E#D#C#I#E#D#C#H#E#D#C#G#E#D#C#F#E#D#C#.0B0A", CultureInfo.InvariantCulture);
            s = s.Replace("0B0A", "@");
            string d = Regex.Replace(
                s,
                @"(((?<=-)|(?!-)^)[^1-9]*)|((?'z'0)[0A-E]*((?=[1-9])|(?'-z'(?=[F-L\.]|$))))|((?'b'[F-L])(?'z'0)[0A-L]*((?=[1-9])|(?'-z'(?=[\.]|$))))",
                "${b}${z}");
            return Regex.Replace(d, ".", m => "\u8d1f\u5143\u7a7a\u96f6\u58f9\u8d30\u53c1\u8086\u4f0d\u9646\u67d2\u634c\u7396\u7a7a\u7a7a\u7a7a\u7a7a\u7a7a\u7a7a\u6574\u5206\u89d2\u62fe\u4f70\u4edf\u4e07\u4ebf\u5146\u4eac\u5793\u79ed\u7a70"[m.Value[0] - '-'].ToString()); // unicode: 负元空零壹贰叁肆伍陆柒捌玖空空空空空空整分角拾佰仟万亿兆京垓秭穰
        }

        private string CommitCodeBuffer()
        {
            if (_sentenceRawBuffer.Length > 0)
            {
                return _sentenceRawBuffer.ToString();
            }

            if (_mixedRawBuffer.Length > 0)
            {
                return MixedInputCommitComposer.ComposeRaw(_mixedDecodeResult);
            }

            if (_inputBuffer.Length == 0)
            {
                return string.Empty;
            }

            // Switching CN/EN should commit the raw input code, not first candidate.
            return _inputBuffer.ToString();
        }

        private bool HasCompositionInput()
        {
            return _inputBuffer.Length > 0 || _mixedRawBuffer.Length > 0 || _sentenceRawBuffer.Length > 0;
        }

        private void StartSentenceInput(char firstCodeChar)
        {
            RestartSentenceInput(char.ToLowerInvariant(firstCodeChar).ToString());
        }

        private void RestartSentenceInput(string rawCode)
        {
            ClearCompositionInput();
            _sentenceCommittedText = string.Empty;
            _sentenceCommittedRawLength = 0;
            _sentenceLastAutoCommitRawLength = 0;
            _sentenceAutoCommitSuspended = false;
            _sentenceNeuralAcceptedRaw = string.Empty;
            _sentenceNeuralTopText = string.Empty;
            _sentenceRawBuffer.Append((rawCode ?? string.Empty).ToLowerInvariant());
            RebuildSentenceInput();
        }

        private string AppendSentenceInput(char value)
        {
            if (_sentenceRawBuffer.Length - _sentenceCommittedRawLength >= 128)
            {
                return null;
            }

            char normalizedValue = char.ToLowerInvariant(value);
            bool isLetter = normalizedValue >= 'a' && normalizedValue <= 'z';
            SentenceCandidate emptyCodeCommitCandidate = isLetter && _sentenceEmptyCodePending == null
                ? GetEmptyCodeAutoCommitCandidate()
                : null;
            if (!isLetter)
            {
                ResetSentenceEmptyCodePending();
            }
            _sentenceRawBuffer.Append(normalizedValue);
            if (isLetter)
            {
                string emptyCodeCommit = ResolveEmptyCodeAutoCommit(emptyCodeCommitCandidate);
                if (emptyCodeCommit != null)
                {
                    return emptyCodeCommit;
                }
            }
            if (!_sentenceDecodeSynchronously)
            {
                // Runtime early commit consumes only an already completed
                // generation. Never synchronously complement Beam Search on
                // the TSF key path; the current key starts the next worker.
                string pendingCommit = TryAutoCommitSentencePrefix();
                RebuildSentenceInput();
                return pendingCommit;
            }

            RebuildSentenceInput();
            return TryAutoCommitSentencePrefix();
        }

        private SentenceCandidate GetEmptyCodeAutoCommitCandidate()
        {
            if (!_state.GetSentenceEmptyCodeAutoCommitEnabled() ||
                _sentenceAutoCommitSuspended ||
                _sentenceResultLexiconVersion != _state.LexiconVersion ||
                !string.Equals(
                    _sentenceDecodeResult.RawCode,
                    _sentenceRawBuffer.ToString(),
                    StringComparison.Ordinal))
            {
                return null;
            }

            SentenceCandidate[] candidates = _sentenceDecodeResult.Candidates ??
                Array.Empty<SentenceCandidate>();
            if (candidates.Length != 1 ||
                string.IsNullOrEmpty(candidates[0]?.Text) ||
                !candidates[0].Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal) ||
                candidates[0].Text.Length <= _sentenceCommittedText.Length)
            {
                return null;
            }

            return candidates[0];
        }

        private string ResolveEmptyCodeAutoCommit(SentenceCandidate capturedCandidate)
        {
            if (_sentenceEmptyCodePending == null && capturedCandidate == null)
            {
                return null;
            }

            if (!_state.GetSentenceEmptyCodeAutoCommitEnabled() || _sentenceInputDecoder == null)
            {
                ResetSentenceEmptyCodePending();
                return null;
            }

            string fullRaw = _sentenceRawBuffer.ToString();
            if (_sentenceInputDecoder.HasCompleteCandidate(fullRaw, _sentenceCommittedText))
            {
                ResetSentenceEmptyCodePending();
                return null;
            }

            if (_sentenceEmptyCodePending == null && capturedCandidate != null)
            {
                SentencePathBoundary boundary = capturedCandidate.Boundary;
                int lastSegmentStart = boundary?.Previous?.RawLength ?? 0;
                _sentenceEmptyCodePending = new SentenceEmptyCodePending
                {
                    CandidateText = capturedCandidate.Text,
                    CommittedText = _sentenceCommittedText,
                    BaseRawLength = fullRaw.Length - 1,
                    LastSegmentStart = lastSegmentStart
                };
            }

            SentenceEmptyCodePending pending = _sentenceEmptyCodePending;
            if (pending == null ||
                !string.Equals(pending.CommittedText, _sentenceCommittedText, StringComparison.Ordinal) ||
                pending.BaseRawLength < 0 || pending.BaseRawLength >= fullRaw.Length ||
                pending.LastSegmentStart < 0 || pending.LastSegmentStart >= fullRaw.Length)
            {
                ResetSentenceEmptyCodePending();
                return null;
            }

            string extendedLastSegment = fullRaw.Substring(pending.LastSegmentStart);
            if (_sentenceInputDecoder.IsProperCodePrefix(extendedLastSegment))
            {
                return null;
            }

            string commit = pending.CandidateText.Substring(pending.CommittedText.Length);
            string retainedRaw = fullRaw.Substring(pending.BaseRawLength);
            RestartSentenceInput(retainedRaw);
            return commit;
        }

        private void ResetSentenceEmptyCodePending()
        {
            _sentenceEmptyCodePending = null;
        }

        private string TryAutoCommitSentencePrefix()
        {
            if (!_state.GetSentenceAutoCommitEnabled() || _sentenceAutoCommitSuspended)
            {
                ResetSentenceAutoCommitEvidence();
                return null;
            }

            string fullRaw = _sentenceRawBuffer.ToString();
            string evidenceRaw = _sentenceDecodeResult.RawCode ?? string.Empty;
            bool currentGeneration = string.Equals(evidenceRaw, fullRaw, StringComparison.Ordinal);
            bool immediatelyPreviousGeneration = evidenceRaw.Length + 1 == fullRaw.Length &&
                fullRaw.StartsWith(evidenceRaw, StringComparison.Ordinal);
            if (_sentenceResultLexiconVersion != _state.LexiconVersion ||
                (!currentGeneration && !immediatelyPreviousGeneration))
            {
                ResetSentenceAutoCommitEvidence();
                return null;
            }

            // Use the authoritative decoded raw length. Display segmentation
            // spaces and the already committed prefix must not satisfy this
            // gate, and pending generations never count as evidence.
            if (evidenceRaw.Length <= 4)
            {
                ResetSentenceAutoCommitEvidence();
                return null;
            }

            SentenceEarlyCommitEvidence earlyCommitEvidence =
                _sentenceDecodeResult.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            if (earlyCommitEvidence.ConfidenceTruncated ||
                string.IsNullOrEmpty(earlyCommitEvidence.Proposal))
            {
                ResetSentenceAutoCommitEvidence();
                return null;
            }

            // The decoder has already summarized the complete retained
            // confidence mass. Temporal stability and commit policy remain in
            // the engine; candidate arrays no longer cross this boundary.
            string proposal = earlyCommitEvidence.Proposal;

            if (!earlyCommitEvidence.IgnoreNeuralConstraint &&
                string.Equals(_sentenceNeuralAcceptedRaw, evidenceRaw, StringComparison.Ordinal))
            {
                while (proposal.Length > _sentenceCommittedText.Length &&
                    !_sentenceNeuralTopText.StartsWith(proposal, StringComparison.Ordinal))
                {
                    proposal = RemoveLastTextElement(proposal);
                }
            }

            SentenceCandidate[] visibleCandidates = _sentenceDecodeResult.Candidates ??
                Array.Empty<SentenceCandidate>();
            if (visibleCandidates.Length > 0 && visibleCandidates[0].SupplementScore > 0.0)
            {
                string supplementTopText = visibleCandidates[0].Text ?? string.Empty;
                while (proposal.Length > _sentenceCommittedText.Length &&
                    !supplementTopText.StartsWith(proposal, StringComparison.Ordinal))
                {
                    proposal = RemoveLastTextElement(proposal);
                }
            }

            if (proposal.Length <= _sentenceCommittedText.Length)
            {
                ResetSentenceAutoCommitEvidence();
                return null;
            }

            bool extendsEvidence = _sentenceAutoCommitEvidence.Count > 0 &&
                evidenceRaw.Length == _sentenceAutoCommitEvidence[_sentenceAutoCommitEvidence.Count - 1].RawCode.Length + 1 &&
                evidenceRaw.StartsWith(
                    _sentenceAutoCommitEvidence[_sentenceAutoCommitEvidence.Count - 1].RawCode,
                    StringComparison.Ordinal);
            if (!extendsEvidence)
            {
                _sentenceAutoCommitEvidence.Clear();
            }
            _sentenceAutoCommitEvidence.Add(new SentenceAutoCommitEvidence
            {
                RawCode = evidenceRaw,
                Proposal = proposal,
                RawLengths = earlyCommitEvidence.RawLengths ??
                    new Dictionary<string, int>(StringComparer.Ordinal),
                Strong = earlyCommitEvidence.ProposalShare >= SentenceEarlyCommitStrongShare
            });
            if (_sentenceAutoCommitEvidence.Count > 3)
            {
                _sentenceAutoCommitEvidence.RemoveAt(0);
            }
            // Extremely strong evidence may confirm after two generations;
            // any weaker generation keeps the original three-generation guard.
            int requiredEvidenceCount = _sentenceAutoCommitEvidence.Count >= 2 &&
                _sentenceAutoCommitEvidence.All(item => item.Strong)
                ? 2
                : 3;
            if (_sentenceAutoCommitEvidence.Count < requiredEvidenceCount) return null;

            string stableProposal = LongestCommonTextElementPrefix(
                _sentenceAutoCommitEvidence.Select(item => item.Proposal));
            int committedRawLength = FindStableSentenceRawLength(
                stableProposal,
                requiredEvidenceCount);
            while (stableProposal.Length > _sentenceCommittedText.Length &&
                (committedRawLength == 0 || fullRaw.Length - committedRawLength < 1))
            {
                stableProposal = RemoveLastTextElement(stableProposal);
                committedRawLength = FindStableSentenceRawLength(
                    stableProposal,
                    requiredEvidenceCount);
            }
            if (committedRawLength <= _sentenceCommittedRawLength ||
                committedRawLength > evidenceRaw.Length ||
                fullRaw.Length - committedRawLength < 1)
            {
                return null;
            }
            string commit = stableProposal.Substring(_sentenceCommittedText.Length);
            if (new StringInfo(commit).LengthInTextElements < 1 ||
                evidenceRaw.Length - _sentenceLastAutoCommitRawLength < 3)
            {
                return null;
            }
            _sentenceCommittedText = stableProposal;
            _sentenceCommittedRawLength = committedRawLength;
            _sentenceLastAutoCommitRawLength = committedRawLength;
            ResetSentenceAutoCommitEvidence();
            _sentenceDecodeResult = FilterSentenceDecodeResultForCommittedPrefix(_sentenceDecodeResult);
            _sentenceGeneration++;
            if (currentGeneration)
            {
                RequestSentenceRerank(_sentenceGeneration, evidenceRaw, _sentenceDecodeResult.Candidates);
            }
            return commit;
        }

        private int FindStableSentenceRawLength(string text, int minimumEvidenceCount)
        {
            if (string.IsNullOrEmpty(text) ||
                _sentenceAutoCommitEvidence.Count < minimumEvidenceCount)
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

        private void ResetSentenceAutoCommitEvidence()
        {
            _sentenceAutoCommitEvidence.Clear();
        }

        private static string LongestCommonTextElementPrefix(IEnumerable<string> values)
        {
            string[] texts = (values ?? Enumerable.Empty<string>()).ToArray();
            if (texts.Length == 0 || texts.Any(string.IsNullOrEmpty))
            {
                return string.Empty;
            }

            string shortest = texts.OrderBy(text => new StringInfo(text).LengthInTextElements).First();
            int[] ends = GetTextElementEndOffsets(shortest);
            for (int index = ends.Length - 1; index >= 0; index--)
            {
                string prefix = shortest.Substring(0, ends[index]);
                if (texts.All(text => text.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return prefix;
                }
            }
            return string.Empty;
        }

        private static int[] GetTextElementEndOffsets(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<int>();
            }

            var ends = new List<int>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                ends.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);
            }
            return ends.ToArray();
        }

        private static string RemoveLastTextElement(string text)
        {
            int[] ends = GetTextElementEndOffsets(text);
            return ends.Length <= 1 ? string.Empty : text.Substring(0, ends[ends.Length - 2]);
        }

        private void RebuildSentenceInput()
        {
            EnsureSentenceDecoderCurrent();
            _sentenceSelectedIndex = 0;
            _sentenceGeneration++;
            _inputBuffer.Clear();
            _inputBuffer.Append(GetSentenceDisplayCode());
            ResetCandidatePageTracker();

            if (_sentenceDecodeSynchronously)
            {
                ApplySentenceDecodeResult(
                    _sentenceGeneration,
                    _sentenceRawBuffer.ToString(),
                    _state.LexiconVersion,
                    _sentenceInputDecoder?.Decode(
                        _sentenceRawBuffer.ToString(),
                        20,
                        _state.GetSentenceAutoCommitEnabled(),
                        _sentenceCommittedText) ?? SentenceDecodeResult.Empty);
                return;
            }

            StartSentenceDecodeWorker();
        }

        private void StartSentenceDecodeWorker()
        {
            if (_sentenceDecodeWorkerRunning)
            {
                return;
            }

            _sentenceDecodeWorkerRunning = true;
            Task.Run((Action)RunSentenceDecodeWorker);
        }

        private void RunSentenceDecodeWorker()
        {
            while (true)
            {
                long generation;
                string rawCode;
                int lexiconVersion;
                SentenceInputDecoder decoder;
                bool includeEarlyCommitEvidence;
                string requiredTextPrefix;

                lock (_lock)
                {
                    if (_compositionState != CompositionState.CnSentence || _sentenceRawBuffer.Length == 0)
                    {
                        _sentenceDecodeWorkerRunning = false;
                        return;
                    }

                    generation = _sentenceGeneration;
                    rawCode = _sentenceRawBuffer.ToString();
                    lexiconVersion = _state.LexiconVersion;
                    decoder = _sentenceInputDecoder;
                    includeEarlyCommitEvidence = _state.GetSentenceAutoCommitEnabled();
                    requiredTextPrefix = _sentenceCommittedText;
                }

                SentenceDecodeResult result;
                try
                {
                    result = decoder?.Decode(
                        rawCode,
                        20,
                        includeEarlyCommitEvidence,
                        requiredTextPrefix) ?? SentenceDecodeResult.Empty;
                }
                catch
                {
                    result = SentenceDecodeResult.Empty;
                }
                Action completedCallback = null;
                bool finished;
                lock (_lock)
                {
                    if (_compositionState == CompositionState.CnSentence &&
                        generation == _sentenceGeneration &&
                        string.Equals(rawCode, _sentenceRawBuffer.ToString(), StringComparison.Ordinal) &&
                        lexiconVersion == _state.LexiconVersion)
                    {
                        ApplySentenceDecodeResult(generation, rawCode, lexiconVersion, result);
                        completedCallback = _sentenceDecodeCompletedCallback;
                    }

                    finished = generation == _sentenceGeneration;
                    if (finished)
                    {
                        _sentenceDecodeWorkerRunning = false;
                    }
                }

                try
                {
                    completedCallback?.Invoke();
                }
                catch
                {
                }
                if (finished)
                {
                    return;
                }
            }
        }

        private void ApplySentenceDecodeResult(long generation, string rawCode, int lexiconVersion, SentenceDecodeResult result)
        {
            _sentenceDecodeResult = FilterSentenceDecodeResultForCommittedPrefix(
                result ?? SentenceDecodeResult.Empty);
            _sentenceResultLexiconVersion = lexiconVersion;
            _sentenceSelectedIndex = 0;

            RequestSentenceRerank(
                generation,
                rawCode,
                _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>());
        }

        private void RequestSentenceRerank(long generation, string rawCode, SentenceCandidate[] candidates)
        {
            if (candidates != null && candidates.Length > 0)
            {
                _sentenceRerankService?.Request(new SentenceRerankRequest
                {
                    Generation = generation,
                    RawCode = rawCode,
                    Candidates = candidates
                        .Take(5)
                        .Select(candidate => candidate.Text)
                        .ToArray()
                });
            }
        }

        private void EnsureSentenceDecodeCurrent()
        {
            string rawCode = _sentenceRawBuffer.ToString();
            if (_sentenceResultLexiconVersion == _state.LexiconVersion &&
                string.Equals(_sentenceDecodeResult.RawCode, rawCode, StringComparison.Ordinal))
            {
                return;
            }

            EnsureSentenceDecoderCurrent();
            ApplySentenceDecodeResult(
                _sentenceGeneration,
                rawCode,
                _state.LexiconVersion,
                _sentenceInputDecoder?.Decode(
                    rawCode,
                    20,
                    _state.GetSentenceAutoCommitEnabled(),
                    _sentenceCommittedText) ?? SentenceDecodeResult.Empty);
        }

        private void EnsureSentenceDecoderCurrent()
        {
            if (_sentenceDecoderExternallyProvided ||
                (_sentenceInputDecoder != null && _sentenceDecodedLexiconVersion == _state.LexiconVersion))
            {
                return;
            }

            ReloadSentenceResources();
        }

        private void MoveSentenceSelection(int delta)
        {
            int count = _sentenceDecodeResult.Candidates?.Length ?? 0;
            int visibleCount = Math.Min(count, Math.Min(Math.Max(_state.GetPageSize(), 1), 10));
            if (visibleCount > 0)
            {
                _sentenceSelectedIndex = (_sentenceSelectedIndex + delta + visibleCount) % visibleCount;
            }
        }

        private bool HasSentenceCandidates()
        {
            return _sentenceDecodeResult.Candidates != null && _sentenceDecodeResult.Candidates.Length > 0;
        }

        private KeyEngineResult CompleteSentenceCandidate(int index)
        {
            EnsureSentenceDecodeCurrent();
            SentenceCandidate[] candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            if (index < 0 || index >= candidates.Length)
            {
                return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
            }

            string output = candidates[index].Text;
            if (_sentenceCommittedText.Length > 0 &&
                !output.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
            {
                return KeyEngineResult.CreateHandled(true, null, GetSentenceDisplayCode(), true);
            }
            if (_sentenceCommittedText.Length > 0)
            {
                output = output.Substring(_sentenceCommittedText.Length);
            }
            ClearCompositionInput();
            _compositionState = CompositionState.CnIdle;
            ResetCandidatePageTracker();
            return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
        }

        private KeyEngineResult CompleteSentenceWithSuffix(string suffix)
        {
            EnsureSentenceDecodeCurrent();
            SentenceCandidate[] candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            string output = candidates.Length > 0 && _sentenceSelectedIndex < candidates.Length
                ? candidates[_sentenceSelectedIndex].Text
                : GetUncommittedSentenceRawCode();
            if (_sentenceCommittedText.Length > 0 &&
                !output.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
            {
                output = GetUncommittedSentenceRawCode();
            }
            else if (_sentenceCommittedText.Length > 0)
            {
                output = output.Substring(_sentenceCommittedText.Length);
            }
            output += suffix ?? string.Empty;
            ClearCompositionInput();
            _compositionState = CompositionState.CnIdle;
            ResetCandidatePageTracker();
            return KeyEngineResult.CreateHandled(true, output, string.Empty, false);
        }

        private string GetUncommittedSentenceRawCode()
        {
            string raw = _sentenceRawBuffer.ToString();
            return _sentenceCommittedRawLength > 0 &&
                   _sentenceCommittedRawLength <= raw.Length
                ? raw.Substring(_sentenceCommittedRawLength)
                : raw;
        }

        private bool IsMixedInputSession()
        {
            return _mixedRawBuffer.Length > 0;
        }

        private void StartMixedInput(char firstCodeChar)
        {
            ClearCompositionInput();
            _mixedRawBuffer.Append(firstCodeChar);
            RebuildMixedInput();
        }

        private void AppendMixedInput(char codeChar, List<string> currentPageCandidates)
        {
            int maxCodeLength = GetSafeMaxCodeLen();
            if (_inputBuffer.Length == maxCodeLength &&
                currentPageCandidates != null &&
                currentPageCandidates.Count > 0)
            {
                int segmentStart = _mixedRawBuffer.Length - _inputBuffer.Length;
                _mixedPreferredCandidateText[segmentStart] = GetCandidateOutputText(currentPageCandidates[0]);
            }

            _mixedRawBuffer.Append(codeChar);
            RebuildMixedInput();
        }

        private void BackspaceMixedInput()
        {
            if (_mixedRawBuffer.Length > 0)
            {
                _mixedRawBuffer.Length -= 1;
            }

            RebuildMixedInput();
        }

        private void RebuildMixedInput()
        {
            int maxCodeLength = GetSafeMaxCodeLen();
            int completedLength = _mixedRawBuffer.Length == 0
                ? 0
                : ((_mixedRawBuffer.Length - 1) / maxCodeLength) * maxCodeLength;
            int[] preferredStarts = _mixedPreferredCandidateText.Keys.ToArray();
            foreach (int start in preferredStarts)
            {
                if (start < 0 || start >= completedLength)
                {
                    _mixedPreferredCandidateText.Remove(start);
                }
            }

            _mixedDecodeResult = _mixedInputDecoder.Decode(new MixedInputDecodeRequest
            {
                RawCode = _mixedRawBuffer.ToString(),
                MaxCodeLength = maxCodeLength,
                LexiconVersion = _state.LexiconVersion,
                PreferredCandidateTextByStart = _mixedPreferredCandidateText
            });
            _mixedDecodedLexiconVersion = _state.LexiconVersion;
            _mixedDecodedMaxCodeLength = maxCodeLength;

            _inputBuffer.Clear();
            _inputBuffer.Append(_mixedDecodeResult.ActiveCode);
            ResetCandidatePageTracker();
        }

        private string GetMixedResolvedPrefixText()
        {
            return IsMixedInputSession() ? (_mixedDecodeResult.ResolvedPrefixText ?? string.Empty) : string.Empty;
        }

        private string CombineMixedCommit(string activeOutput, string suffix = null)
        {
            return IsMixedInputSession()
                ? MixedInputCommitComposer.ComposeChinese(_mixedDecodeResult, activeOutput, suffix)
                : (activeOutput ?? string.Empty) + (suffix ?? string.Empty);
        }

        private void ClearCompositionInput()
        {
            _inputBuffer.Clear();
            _mixedRawBuffer.Clear();
            _mixedPreferredCandidateText.Clear();
            _mixedDecodeResult = MixedInputDecodeResult.Empty;
            _mixedDecodedLexiconVersion = -1;
            _mixedDecodedMaxCodeLength = -1;
            _sentenceRawBuffer.Clear();
            _sentenceDecodeResult = SentenceDecodeResult.Empty;
            _sentenceResultLexiconVersion = -1;
            _sentenceSelectedIndex = 0;
            _sentenceCommittedText = string.Empty;
            _sentenceCommittedRawLength = 0;
            ResetSentenceAutoCommitEvidence();
            _sentenceLastAutoCommitRawLength = 0;
            _sentenceAutoCommitSuspended = false;
            ResetSentenceEmptyCodePending();
            _sentenceNeuralAcceptedRaw = string.Empty;
            _sentenceNeuralTopText = string.Empty;
            _sentenceGeneration++;
        }

        private KeyEngineResult CompleteCnComposition(string activeOutput, string suffix = null)
        {
            string output = CombineMixedCommit(activeOutput, suffix);
            ClearCompositionInput();
            _compositionState = CompositionState.CnIdle;
            ResetCandidatePageTracker();
            return KeyEngineResult.CreateHandled(_isChinese, output.Length > 0 ? output : null, string.Empty, false);
        }

        private string ResolveCommitTextForCurrentState(string code)
        {
            if (_compositionState == CompositionState.CnPinyin)
            {
                string pyCode = code != null && code.Length > 0 ? code.Substring(1) : string.Empty;
                List<string> py = ResolvePinyinCandidates(pyCode);
                if (py != null && py.Count > 0 && !string.IsNullOrEmpty(py[0]))
                {
                    return GetCandidateOutputText(py[0]);
                }

                return code ?? string.Empty;
            }

            return ResolveCommitText(code);
        }

        private List<string> ResolvePinyinCandidates(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }

            try
            {
                return _state.GetPinyinCandidates(code);
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCommitText(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.Empty;
            }

            List<string> candidates = ResolveCandidates(code);
            if (candidates != null && candidates.Count > 0 && !string.IsNullOrEmpty(candidates[0]))
            {
                return GetCandidateOutputText(candidates[0]);
            }

            return code;
        }

        private List<string> ResolveCandidates(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }

            try
            {
                return _state.GetCandidates(code);
            }
            catch
            {
                return null;
            }
        }

        private bool TryResolveCnSymbolOutput(int vk, out string output)
        {
            output = null;
            if (_state.GetUseEnPuncInCn())
            {
                if (vk == VK_OEM_7)
                {
                    return false;
                }

                if (vk == VK_OEM_PERIOD)
                {
                    _dotAfterDigitArmed = false;
                }

                return EnSymbols.TryGetValue(vk, out output);
            }

            if (vk == VK_OEM_PERIOD && _dotAfterDigitArmed)
            {
                output = ".";
                _dotAfterDigitArmed = false;
                return true;
            }

            if (vk == VK_OEM_PERIOD)
            {
                _dotAfterDigitArmed = false;
            }

            if (vk == VK_OEM_2 && !_state.GetSlashOutputsDunhao())
            {
                output = "/";
                return true;
            }

            return CnSymbols.TryGetValue(vk, out output);
        }

        private bool IsNextPageKey(int vk, bool shift)
        {
            string pageKeys = _state.GetPageKeys();
            if (string.Equals(pageKeys, "[ ]", StringComparison.Ordinal))
            {
                return vk == VK_OEM_6 && !shift;
            }
            if (string.Equals(pageKeys, "Shift Tab/Tab", StringComparison.Ordinal))
            {
                return vk == VK_TAB && !shift;
            }
            if (string.Equals(pageKeys, "PageUp/PageDown", StringComparison.Ordinal))
            {
                return vk == VK_NEXT && !shift;
            }

            return vk == VK_OEM_PLUS && !shift;
        }

        private bool IsPrevPageKey(int vk, bool shift)
        {
            string pageKeys = _state.GetPageKeys();
            if (string.Equals(pageKeys, "[ ]", StringComparison.Ordinal))
            {
                return vk == VK_OEM_4 && !shift;
            }
            if (string.Equals(pageKeys, "Shift Tab/Tab", StringComparison.Ordinal))
            {
                return vk == VK_TAB && shift;
            }
            if (string.Equals(pageKeys, "PageUp/PageDown", StringComparison.Ordinal))
            {
                return vk == VK_PRIOR && !shift;
            }

            return vk == VK_OEM_MINUS && !shift;
        }

        private void ResetCandidatePageTracker()
        {
            _candidatePageIndex = 0;
            _candidatePageCode = string.Empty;
            _candidatePageState = CompositionState.CnIdle;
        }

        private List<string> GetCandidatePage(string code, CompositionState state, List<string> allCandidates, out int pageIndex)
        {
            string normalizedCode = code ?? string.Empty;
            if (_candidatePageState != state || !string.Equals(_candidatePageCode, normalizedCode, StringComparison.Ordinal))
            {
                _candidatePageState = state;
                _candidatePageCode = normalizedCode;
                _candidatePageIndex = 0;
            }

            if (allCandidates == null || allCandidates.Count <= 0)
            {
                _candidatePageIndex = 0;
                pageIndex = 0;
                return new List<string>();
            }

            int pageSize = _state.GetPageSize();
            if (pageSize < 1)
            {
                pageSize = 1;
            }
            else if (pageSize > 10)
            {
                pageSize = 10;
            }

            int maxPage = (allCandidates.Count - 1) / pageSize;
            if (_candidatePageIndex < 0)
            {
                _candidatePageIndex = 0;
            }
            else if (_candidatePageIndex > maxPage)
            {
                _candidatePageIndex = maxPage;
            }

            int start = _candidatePageIndex * pageSize;
            int count = Math.Min(pageSize, allCandidates.Count - start);
            if (count <= 0)
            {
                _candidatePageIndex = 0;
                pageIndex = 0;
                return new List<string>();
            }

            pageIndex = _candidatePageIndex;
            return allCandidates.GetRange(start, count);
        }

        private void MoveCandidatePage(string code, CompositionState state, List<string> allCandidates, int delta)
        {
            if (delta == 0)
            {
                return;
            }

            GetCandidatePage(code, state, allCandidates, out int currentPage);
            if (allCandidates == null || allCandidates.Count <= 0)
            {
                return;
            }

            int pageSize = _state.GetPageSize();
            if (pageSize < 1)
            {
                pageSize = 1;
            }
            else if (pageSize > 10)
            {
                pageSize = 10;
            }

            int pageCount = (allCandidates.Count + pageSize - 1) / pageSize;
            int nextPage = currentPage + delta;
            if (nextPage < 0)
            {
                nextPage = 0;
            }
            else if (nextPage >= pageCount)
            {
                nextPage = pageCount - 1;
            }

            _candidatePageIndex = nextPage;
        }

        private void NormalizeResultTextActions(KeyEngineResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.TextToOutput))
            {
                return;
            }

            string text = result.TextToOutput;
            if (string.Equals(text, "{\u6dfb\u52a0}", StringComparison.Ordinal) || // unicode: {添加}
                string.Equals(text, "{\u52a0\u8bcd}", StringComparison.Ordinal)) // unicode: {加词}
            {
                result.SetTextToOutput(null);
                result.SetInputBuffer(string.Empty);
                result.SetIsComposing(false);
                result.SetOpenAddCiWindow(true);
                return;
            }

            if (string.Equals(text, "{\u9690\u85cf\u5019\u9009}", StringComparison.Ordinal)) // unicode: {隐藏候选}
            {
                _state.TryToggleHideCandidateItems(out _);
                result.SetTextToOutput(null);
                result.SetInputBuffer(string.Empty);
                result.SetIsComposing(false);
                return;
            }

            string converted = ConvertOutputText(text);
            if (!string.Equals(converted, text, StringComparison.Ordinal))
            {
                result.SetTextToOutput(converted);
            }
        }

        private string ConvertOutputText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (text.Length > 3 &&
                text[0] == '{' &&
                text[text.Length - 1] == '}' &&
                text.IndexOf('|') > 0)
            {
                string body = text.Substring(1, text.Length - 2);
                string[] randomSet = body.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                if (randomSet.Length > 0)
                {
                    return randomSet[_random.Next(randomSet.Length)];
                }
            }

            switch (text)
            {
                case "\u3002": // unicode: 。
                    return _dotAfterDigitArmed ? "." : text;
                case "{\u91cd\u590d\u4e0a\u5c4f}": // unicode: {重复上屏}
                    return _repeatBuffer;
                case "{\u65e5\u671f}": // unicode: {日期}
                    return DateTime.Now.ToString("yyyy\u5e74MM\u6708dd\u65e5", CultureInfo.InvariantCulture); // unicode: yyyy年MM月dd日
                case "{\u65e5\u671f.}": // unicode: {日期.}
                    return DateTime.Now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
                case "{\u65e5\u671f-}": // unicode: {日期-}
                    return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "{\u65e5\u671f/}": // unicode: {日期/}
                    return DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
                case "{\u65f6\u5206\u79d2}": // unicode: {时分秒}
                    return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                case "{\u65f6\u5206}": // unicode: {时分}
                    return DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                case "{\u661f\u671f}": // unicode: {星期}
                    return CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek);
                case "{\u5468}": // unicode: {周}
                    {
                        string[] weekNames = { "\u5468\u65e5", "\u5468\u4e00", "\u5468\u4e8c", "\u5468\u4e09", "\u5468\u56db", "\u5468\u4e94", "\u5468\u516d" }; // unicode: 周日 ; 周一 ; 周二 ; 周三 ; 周四 ; 周五 ; 周六
                        return weekNames[(int)DateTime.Now.DayOfWeek];
                    }
                default:
                    return text;
            }
        }

        private bool HandleShiftStateAndToggle(int vk, bool isDown, bool isUp, bool ctrl, bool alt, bool win, out KeyEngineResult result)
        {
            result = null;

            if (!IsShiftKey(vk))
            {
                return false;
            }

            if (isDown)
            {
                bool hadShiftDown = _leftShiftDown || _rightShiftDown;
                if (vk == VK_RSHIFT)
                {
                    _rightShiftDown = true;
                }
                else
                {
                    _leftShiftDown = true;
                }

                if (!hadShiftDown)
                {
                    _shiftChordUsed = false;
                }

                result = KeyEngineResult.Pass(_isChinese);
                return true;
            }

            if (isUp)
            {
                bool hadMatchingShiftDown;
                if (vk == VK_RSHIFT)
                {
                    hadMatchingShiftDown = _rightShiftDown;
                    _rightShiftDown = false;
                }
                else
                {
                    hadMatchingShiftDown = _leftShiftDown;
                    _leftShiftDown = false;
                }

                bool canToggle = hadMatchingShiftDown &&
                                 _state.GetShiftToggleEnabled() &&
                                 !_shiftChordUsed &&
                                 !ctrl &&
                                 !alt &&
                                 !win &&
                                 !_skipShiftToggleOnce;
                string committedByToggle = string.Empty;
                if (canToggle)
                {
                    ToggleChinese(out committedByToggle);
                }

                if (!_leftShiftDown && !_rightShiftDown)
                {
                    _shiftChordUsed = false;
                    _skipShiftToggleOnce = false;
                }

                if (!string.IsNullOrEmpty(committedByToggle))
                {
                    result = KeyEngineResult.CreateHandled(_isChinese, committedByToggle, string.Empty, false);
                    return true;
                }

                result = KeyEngineResult.Pass(_isChinese);
                return true;
            }

            result = KeyEngineResult.Pass(_isChinese);
            return true;
        }

        private bool TryHandleQuickSymbol(int vk, string currentCode, List<string> candidates, out KeyEngineResult result)
        {
            result = null;
            bool noCandidate = candidates == null || candidates.Count == 0;

            if (string.Equals(currentCode, ";", StringComparison.Ordinal) &&
                (vk == VK_OEM_1 || (vk == VK_SPACE && noCandidate)))
            {
                result = CompleteCnComposition("\uFF1B"); // unicode: ；
                return true;
            }

            if (string.Equals(currentCode, "/", StringComparison.Ordinal) &&
                (vk == VK_OEM_2 || (vk == VK_SPACE && noCandidate)))
            {
                result = CompleteCnComposition(_state.GetSlashOutputsDunhao() ? "\u3001" : "/"); // unicode: 、
                return true;
            }

            if (string.Equals(currentCode, "[", StringComparison.Ordinal) &&
                (vk == VK_OEM_4 || (vk == VK_SPACE && noCandidate)))
            {
                result = CompleteCnComposition("\u3010"); // unicode: 【
                return true;
            }

            return false;
        }

        private KeyEngineResult HandleCnSymbolAfterCandidateCommit(int vk, bool shift, string firstCandidate)
        {
            bool wasMixedInputSession = IsMixedInputSession();
            string resolvedPrefixText = GetMixedResolvedPrefixText();
            ClearCompositionInput();
            _compositionState = CompositionState.CnIdle;
            ResetCandidatePageTracker();

            if (!wasMixedInputSession && string.IsNullOrEmpty(firstCandidate) && string.IsNullOrEmpty(resolvedPrefixText))
            {
                return KeyEngineResult.CreateHandled(_isChinese, null, string.Empty, false);
            }

            string firstOutput = resolvedPrefixText + (string.IsNullOrEmpty(firstCandidate) ? string.Empty : GetCandidateOutputText(firstCandidate));
            KeyEngineResult idleResult = ProcessCnIdleKeyDown(vk, shift);
            if (idleResult != null && idleResult.Handled)
            {
                string suffixOutput = idleResult.TextToOutput ?? string.Empty;
                if (!string.IsNullOrEmpty(suffixOutput))
                {
                    // Old behavior is two-step send: candidate first, then punctuation.
                    // Preserve "digit + full-width period => dot" behavior on the 2nd step.
                    if (string.Equals(suffixOutput, "\u3002", StringComparison.Ordinal) && IsTextEndingWithDigit(firstOutput))
                    {
                        suffixOutput = ".";
                    }
                    else
                    {
                        suffixOutput = ConvertOutputText(suffixOutput);
                    }
                }

                string merged = firstOutput + suffixOutput;
                bool suppressComposingEcho =
                    !string.IsNullOrEmpty(firstOutput) &&
                    idleResult.IsComposing &&
                    !string.IsNullOrEmpty(idleResult.InputBuffer);

                return KeyEngineResult.CreateHandled(
                    idleResult.IsChinese,
                    merged.Length > 0 ? merged : null,
                    suppressComposingEcho ? string.Empty : (idleResult.InputBuffer ?? string.Empty),
                    suppressComposingEcho ? false : idleResult.IsComposing);
            }

            return KeyEngineResult.CreateHandled(_isChinese, firstOutput, string.Empty, false);
        }

        private int GetSafeMaxCodeLen()
        {
            try
            {
                int maxCodeLen = _state.GetMaxCodeLength();
                if (maxCodeLen < 1)
                {
                    return 1;
                }
                if (maxCodeLen > 16)
                {
                    return 16;
                }
                return maxCodeLen;
            }
            catch
            {
                return 4;
            }
        }

        private bool TryHandleSelectionKeyInCnComposing(int vk, List<string> candidates, out KeyEngineResult result)
        {
            result = null;
            if (!TryGetSelectionNumberByKey(vk, out int selectionNumber))
            {
                return false;
            }

            if (candidates != null && candidates.Count > 0)
            {
                string output;
                if (selectionNumber >= 1 && selectionNumber <= candidates.Count)
                {
                    output = GetCandidateOutputText(candidates[selectionNumber - 1]);
                }
                else if (!IsDigitSelectionKey(vk))
                {
                    result = CompleteCnComposition(null);
                    return true;
                }
                else
                {
                    output = GetCandidateOutputText(candidates[0]);
                    int digit = selectionNumber == 10 ? 0 : selectionNumber;
                    output += digit.ToString(CultureInfo.InvariantCulture);
                }

                result = CompleteCnComposition(output);
                return true;
            }

            result = CompleteCnComposition(null);
            return true;
        }

        private bool TryHandleModifierSelectionOnKeyDown(int vk, out KeyEngineResult result)
        {
            result = null;
            if (!IsModifierKey(vk))
            {
                return false;
            }

            if (_inputBuffer.Length == 0)
            {
                return false;
            }

            if (_compositionState != CompositionState.CnComposing &&
                _compositionState != CompositionState.CnPinyin)
            {
                return false;
            }

            string currentCode = _inputBuffer.ToString();
            List<string> allCandidates;
            List<string> candidates;
            if (_compositionState == CompositionState.CnPinyin)
            {
                string pinyinCode = currentCode.Length > 0 ? currentCode.Substring(1) : string.Empty;
                allCandidates = ResolvePinyinCandidates(pinyinCode);
                candidates = GetCandidatePage(currentCode, CompositionState.CnPinyin, allCandidates, out _);
            }
            else
            {
                allCandidates = ResolveCandidates(currentCode);
                candidates = GetCandidatePage(currentCode, CompositionState.CnComposing, allCandidates, out _);
            }

            if (!TryHandleSelectionKeyInCnComposing(vk, candidates, out result))
            {
                return false;
            }

            _handledModifierSelectionKeys.Add(vk);
            return true;
        }

        private static int ResolveSelectionVirtualKey(int vk, int scan, bool extended)
        {
            if (vk == VK_SHIFT)
            {
                if (scan == 0x2A)
                {
                    return VK_LSHIFT;
                }

                if (scan == 0x36)
                {
                    return VK_RSHIFT;
                }
            }
            else if (vk == VK_CONTROL)
            {
                return extended ? VK_RCONTROL : VK_LCONTROL;
            }
            else if (vk == VK_MENU)
            {
                return extended ? VK_RMENU : VK_LMENU;
            }

            return vk;
        }

        private static bool IsDigitSelectionKey(int vk)
        {
            return (vk >= VK_0 && vk <= VK_9) || (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9);
        }

        private bool TryGetSelectionNumberByGenericModifier(int vk, out int selectionNumber)
        {
            selectionNumber = 0;

            if (vk == VK_LSHIFT || vk == VK_RSHIFT)
            {
                return _customSelectionKeyMap.TryGetValue(VK_SHIFT, out selectionNumber);
            }

            if (vk == VK_LCONTROL || vk == VK_RCONTROL)
            {
                return _customSelectionKeyMap.TryGetValue(VK_CONTROL, out selectionNumber);
            }

            if (vk == VK_LMENU || vk == VK_RMENU)
            {
                return _customSelectionKeyMap.TryGetValue(VK_MENU, out selectionNumber);
            }

            if (vk == VK_RWIN)
            {
                return _customSelectionKeyMap.TryGetValue(VK_LWIN, out selectionNumber);
            }

            return false;
        }

        private bool TryGetSelectionNumberByKey(int vk, out int selectionNumber)
        {
            if (_customSelectionKeyMap.TryGetValue(vk, out selectionNumber))
            {
                return true;
            }

            return TryGetSelectionNumberByGenericModifier(vk, out selectionNumber);
        }

        private void LoadCustomSelectionKeyConfig()
        {
            Dictionary<int, List<int>> bindings = BuildDefaultSelectionKeyBindings();
            string path = _state.GetCustomSelectionKeyConfigPath();
            try
            {
                if (!File.Exists(path))
                {
                    WriteDefaultCustomSelectionKeyConfig(path);
                }

                if (TryParseCustomSelectionKeyConfigLines(
                    File.ReadAllLines(path, Encoding.UTF8),
                    bindings,
                    out Dictionary<int, List<int>> parsedBindings,
                    out _))
                {
                    bindings = parsedBindings;
                }
            }
            catch
            {
            }

            _customSelectionKeyMap.Clear();
            _customSelectionBindings = CloneSelectionKeyBindings(bindings);
            for (int number = 1; number <= 10; number++)
            {
                if (!bindings.TryGetValue(number, out List<int> keys))
                {
                    continue;
                }

                foreach (int vk in keys)
                {
                    if (!_customSelectionKeyMap.ContainsKey(vk))
                    {
                        _customSelectionKeyMap[vk] = number;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateConfigLines(string configText)
        {
            if (string.IsNullOrEmpty(configText))
            {
                yield break;
            }

            using (var reader = new StringReader(configText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        private static bool TryParseCustomSelectionKeyLines(IEnumerable<string> rawLines, Dictionary<int, List<int>> bindings)
        {
            return TryParseCustomSelectionKeyConfigLines(rawLines, bindings, out _, out _);
        }

        private static bool TryParseCustomSelectionKeyConfigLines(
            IEnumerable<string> rawLines,
            Dictionary<int, List<int>> baseBindings,
            out Dictionary<int, List<int>> parsedBindings,
            out string error)
        {
            parsedBindings = CloneSelectionKeyBindings(baseBindings);
            error = string.Empty;
            if (rawLines == null)
            {
                return true;
            }

            Dictionary<string, int> vkNameMap = BuildVirtualKeyNameMap();
            foreach (string rawLine in rawLines)
            {
                string line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    error = "\u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u5b58\u5728\u65e0\u6548\u884c\uff1a" + line; // unicode: 自定义选重键存在无效行：
                    return false;
                }

                if (!TryParseSelectionLabel(parts[0], out int selectionNumber))
                {
                    error = "\u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u5b58\u5728\u65e0\u6548\u6807\u7b7e\uff1a" + parts[0]; // unicode: 自定义选重键存在无效标签：
                    return false;
                }

                var parsedKeys = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    if (!TryParseVirtualKeyToken(parts[i], vkNameMap, out int keyVk))
                    {
                        error = "\u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u5b58\u5728\u65e0\u6548\u6309\u952e\uff1a" + parts[i]; // unicode: 自定义选重键存在无效按键：
                        return false;
                    }

                    if (!parsedKeys.Contains(keyVk))
                    {
                        parsedKeys.Add(keyVk);
                    }
                }

                parsedBindings[selectionNumber] = parsedKeys;
            }

            return true;
        }

        private static Dictionary<int, List<int>> CloneSelectionKeyBindings(Dictionary<int, List<int>> source)
        {
            var clone = new Dictionary<int, List<int>>();
            if (source == null)
            {
                return clone;
            }

            foreach (KeyValuePair<int, List<int>> item in source)
            {
                clone[item.Key] = item.Value == null ? new List<int>() : new List<int>(item.Value);
            }

            return clone;
        }

        private static string BuildSelectionKeyBindingsText(Dictionary<int, List<int>> bindings)
        {
            var lines = new List<string>();
            for (int number = 1; number <= 10; number++)
            {
                List<int> keys = null;
                bindings?.TryGetValue(number, out keys);
                var line = new StringBuilder();
                line.Append(number.ToString(CultureInfo.InvariantCulture));
                line.Append("\u9009"); // unicode: 选
                if (keys != null)
                {
                    foreach (int vk in keys.Distinct())
                    {
                        line.Append(' ');
                        line.AppendFormat(CultureInfo.InvariantCulture, "0x{0:X2}", vk);
                    }
                }

                lines.Add(line.ToString());
            }

            return string.Join("\r\n", lines);
        }

        private static string BuildSelectionKeyFileText(Dictionary<int, List<int>> bindings)
        {
            Dictionary<string, int> vkNameMap = BuildVirtualKeyNameMap();
            List<KeyValuePair<string, int>> ordered = vkNameMap.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
            var lines = new List<string>
            {
                "# TigerClaw \u81ea\u5b9a\u4e49\u9009\u91cd\u952e\u914d\u7f6e", // unicode: 自定义选重键配置
                "# \u683c\u5f0f\uff1a<n\u9009> <\u952e1> <\u952e2> ...", // unicode: 格式：<n选> <键1> <键2> ...
                "# \u952e\u53ef\u4ee5\u5199\u6210\uff1a\u5341\u8fdb\u5236\uff0849\uff09\u3001\u5341\u516d\u8fdb\u5236\uff080x31\uff09\u3001\u6216\u952e\u540d\uff08VK_1\uff09", // unicode: 键可以写成：十进制（49）、十六进制（0x31）、或键名（VK_1）
                "# \u4e0b\u9762\u662f\u53ef\u7528\u6309\u952e\u540d\u79f0\u4e0e\u952e\u503c\uff1a" // unicode: 下面是可用按键名称与键值：
            };

            foreach (KeyValuePair<string, int> item in ordered)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "# {0} 0x{1:X2} {1}", item.Key, item.Value));
            }

            lines.Add(string.Empty);
            lines.Add(BuildSelectionKeyBindingsText(bindings));
            return string.Join("\r\n", lines);
        }

        private static Dictionary<int, List<int>> BuildDefaultSelectionKeyBindings()
        {
            return new Dictionary<int, List<int>>
            {
                { 1, new List<int> { VK_0 + 1 } },
                { 2, new List<int> { VK_0 + 2 } },
                { 3, new List<int> { VK_0 + 3 } },
                { 4, new List<int> { VK_0 + 4 } },
                { 5, new List<int> { VK_0 + 5 } },
                { 6, new List<int> { VK_0 + 6 } },
                { 7, new List<int> { VK_0 + 7 } },
                { 8, new List<int> { VK_0 + 8 } },
                { 9, new List<int> { VK_0 + 9 } },
                { 10, new List<int> { VK_0 } }
            };
        }

        private static bool TryParseSelectionLabel(string token, out int selectionNumber)
        {
            selectionNumber = 0;
            if (string.IsNullOrWhiteSpace(token) || !token.EndsWith("\u9009", StringComparison.Ordinal)) // unicode: 选
            {
                return false;
            }

            string numPart = token.Substring(0, token.Length - 1);
            if (!int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out selectionNumber))
            {
                return false;
            }

            return selectionNumber >= 1 && selectionNumber <= 10;
        }

        private static bool TryParseVirtualKeyToken(string token, Dictionary<string, int> vkNameMap, out int vk)
        {
            vk = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk);
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out vk))
            {
                return true;
            }

            return vkNameMap.TryGetValue(trimmed, out vk);
        }

        private static void WriteDefaultCustomSelectionKeyConfig(string path)
        {
            File.WriteAllText(path, BuildSelectionKeyFileText(BuildDefaultSelectionKeyBindings()), new UTF8Encoding(true));
        }

        private static Dictionary<string, int> BuildVirtualKeyNameMap()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "VK_SHIFT", VK_SHIFT },
                { "VK_LSHIFT", VK_LSHIFT },
                { "VK_RSHIFT", VK_RSHIFT },
                { "VK_CONTROL", VK_CONTROL },
                { "VK_LCONTROL", VK_LCONTROL },
                { "VK_RCONTROL", VK_RCONTROL },
                { "VK_MENU", VK_MENU },
                { "VK_LMENU", VK_LMENU },
                { "VK_RMENU", VK_RMENU },
                { "VK_LWIN", VK_LWIN },
                { "VK_RWIN", VK_RWIN },
                { "VK_CAPITAL", VK_CAPITAL },
                { "VK_SPACE", VK_SPACE },
                { "VK_BACK", VK_BACK },
                { "VK_RETURN", VK_RETURN },
                { "VK_TAB", VK_TAB },
                { "VK_ESCAPE", VK_ESCAPE },
                { "VK_OEM_1", VK_OEM_1 },
                { "VK_OEM_2", VK_OEM_2 },
                { "VK_OEM_4", VK_OEM_4 },
                { "VK_OEM_7", VK_OEM_7 },
                { "VK_OEM_COMMA", VK_OEM_COMMA },
                { "VK_OEM_PERIOD", VK_OEM_PERIOD }
            };

            for (int i = 0; i <= 9; i++)
            {
                map["VK_" + i.ToString(CultureInfo.InvariantCulture)] = VK_0 + i;
            }

            for (int i = 0; i < 26; i++)
            {
                map["VK_" + ((char)('A' + i)).ToString()] = VK_A + i;
            }

            for (int i = 1; i <= 24; i++)
            {
                map["VK_F" + i.ToString(CultureInfo.InvariantCulture)] = 0x6F + i;
            }

            return map;
        }

        private bool TryMapStandalonePunctuation(int vk, bool shift, out string text)
        {
            text = null;
            bool useEnglish = _state.GetUseEnPuncInCn();

            if (shift)
            {
                return false;
            }

            switch (vk)
            {
                case VK_OEM_COMMA:
                    text = useEnglish ? "," : "\uFF0C"; // unicode: ，
                    return true;
                case VK_OEM_PERIOD:
                    text = useEnglish ? "." : "\u3002"; // unicode: 。
                    return true;
                case VK_OEM_1:
                    text = useEnglish ? ";" : "\uFF1B"; // unicode: ；
                    return true;
                case VK_OEM_2:
                    text = useEnglish ? "/" : (_state.GetSlashOutputsDunhao() ? "\u3001" : "/"); // unicode: 、
                    return true;
                case VK_OEM_7:
                    text = useEnglish ? "'" : "\u2019"; // unicode: ’
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryMapLetter(int vk, out char c)
        {
            c = '\0';
            if (vk >= VK_A && vk <= VK_Z)
            {
                c = (char)('a' + (vk - VK_A));
                return true;
            }

            return false;
        }

        private static bool TryMapIdleCodeChar(int vk, bool shift, out char c)
        {
            c = '\0';
            if (TryMapLetter(vk, out c))
            {
                return true;
            }

            if (shift)
            {
                return false;
            }

            if (vk == VK_OEM_1) { c = ';'; return true; }
            if (vk == VK_OEM_2) { c = '/'; return true; }
            if (vk == VK_OEM_4) { c = '['; return true; }
            return false;
        }

        private static bool IsShiftKey(int vk)
        {
            return vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT;
        }

        private static bool IsModifierKey(int vk)
        {
            return IsShiftKey(vk) ||
                   vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL ||
                   vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU ||
                   vk == VK_LWIN || vk == VK_RWIN ||
                   vk == VK_CAPITAL;
        }

        private bool TryHandleCtrlSpaceChord(int vk, bool isDown, bool isUp, int repeat, bool shift, bool alt, bool win, out KeyEngineResult result)
        {
            result = null;
            bool isCtrl = IsControlKey(vk);
            bool isSpace = vk == VK_SPACE;
            bool hasBlockingModifier = shift || alt || win;
            DateTime nowUtc = DateTime.UtcNow;

            if (!isCtrl && !isSpace)
            {
                // Any other key pressed while Ctrl is held means Ctrl is acting as a modifier for a
                // chord (Ctrl+M, Ctrl+=, Ctrl+1..9, ...), not as half of a Ctrl+Space toggle. Disarm
                // so a Space pressed right after (within CtrlSpaceGrace of Ctrl-up, e.g. to commit)
                // won't get mistaken for Ctrl+Space and flip CN/EN.
                if (isDown && _ctrlChordDown)
                {
                    ResetCtrlSpaceState();
                    return false;
                }
                CleanupCtrlSpaceState(nowUtc);
                return false;
            }

            if (!_state.GetCtrlSpaceToggleEnabled())
            {
                ResetCtrlSpaceState();
                return false;
            }

            bool handled = false;
            if (isDown)
            {
                if (isCtrl)
                {
                    if (hasBlockingModifier)
                    {
                        ResetCtrlSpaceState();
                        return false;
                    }

                    _ctrlChordDown = true;
                    _ctrlSpaceArmed = !_spaceChordDown;
                    _ctrlSpaceSwitched = false;
                    _lastCtrlUpUtc = DateTime.MinValue;
                }
                else if (isSpace)
                {
                    if (hasBlockingModifier)
                    {
                        ResetCtrlSpaceState();
                        return false;
                    }

                    _spaceChordDown = true;
                    if (_ctrlChordDown && repeat <= 1)
                    {
                        handled = TryToggleCtrlSpace(out result);
                    }
                }
            }
            else if (isUp)
            {
                if (isCtrl)
                {
                    if (hasBlockingModifier)
                    {
                        ResetCtrlSpaceState();
                        return false;
                    }

                    _ctrlChordDown = false;
                    _lastCtrlUpUtc = nowUtc;
                    if (_spaceChordDown)
                    {
                        handled = TryToggleCtrlSpace(out result);
                    }
                }
                else if (isSpace)
                {
                    if (hasBlockingModifier)
                    {
                        ResetCtrlSpaceState();
                        return false;
                    }

                    _spaceChordDown = false;
                    bool withinGrace = _lastCtrlUpUtc != DateTime.MinValue &&
                                       (nowUtc - _lastCtrlUpUtc) <= CtrlSpaceGrace;
                    if (_ctrlChordDown || withinGrace)
                    {
                        handled = TryToggleCtrlSpace(out result);
                    }
                }
            }

            CleanupCtrlSpaceState(nowUtc);
            return handled;
        }

        private bool TryToggleCtrlSpace(out KeyEngineResult result)
        {
            result = null;
            if (!_ctrlSpaceArmed || _ctrlSpaceSwitched)
            {
                return false;
            }

            ToggleChinese(out string committedByToggle);
            _ctrlSpaceSwitched = true;
            result = KeyEngineResult.CreateHandled(_isChinese, committedByToggle, string.Empty, false);
            return true;
        }

        private void CleanupCtrlSpaceState(DateTime nowUtc)
        {
            if (_ctrlChordDown || _spaceChordDown)
            {
                return;
            }

            if (_ctrlSpaceSwitched)
            {
                ResetCtrlSpaceState();
                return;
            }

            if (_ctrlSpaceArmed &&
                _lastCtrlUpUtc != DateTime.MinValue &&
                (nowUtc - _lastCtrlUpUtc) > CtrlSpaceGrace)
            {
                ResetCtrlSpaceState();
            }
        }

        private string GetCandidateOutputText(string candidate)
        {
            return ConvertOutputText(_state.GetCandidateCommitText(candidate));
        }

        private string GetCandidateDisplayText(string candidate)
        {
            if (_state.IsDisplayCommitSeparatedCandidate(candidate))
            {
                return _state.GetCandidateDisplayText(candidate);
            }

            return ConvertOutputText(_state.GetCandidateCommitText(candidate));
        }

        private void CancelCompositionStateForPassShortcut()
        {
            ClearCompositionInput();
            _compositionState = _isChinese ? CompositionState.CnIdle : CompositionState.En;
            ResetCandidatePageTracker();
        }

        private void ResetCtrlSpaceState()
        {
            _ctrlChordDown = false;
            _spaceChordDown = false;
            _ctrlSpaceArmed = false;
            _ctrlSpaceSwitched = false;
            _lastCtrlUpUtc = DateTime.MinValue;
        }

        private void ResetShiftToggleState()
        {
            _leftShiftDown = false;
            _rightShiftDown = false;
            _shiftChordUsed = false;
            _skipShiftToggleOnce = false;
        }

        private static bool IsControlKey(int vk)
        {
            return vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
        }

        public int GetSendHistoryCount()
        {
            lock (_lock)
            {
                return Math.Min(_sendHistory.Count, 20);
            }
        }

        public void GetCompositionDisplayParts(out string prefix, out string activeCode)
        {
            lock (_lock)
            {
                EnsureMixedDecodeCurrent();
                prefix = GetMixedResolvedPrefixText();
                activeCode = _compositionState == CompositionState.CnSentence
                    ? GetSentenceDisplayCode()
                    : _inputBuffer.ToString();
            }
        }

        public string GetLastCi(int historyLen)
        {
            lock (_lock)
            {
                int len = Math.Min(Math.Min(_sendHistory.Count, historyLen), 20);
                if (len <= 0)
                {
                    return string.Empty;
                }

                string[] arr = _sendHistory.ToArray();
                var sb = new StringBuilder(len * 2);
                for (int i = len - 1; i >= 0; i--)
                {
                    sb.Append(arr[i]);
                }

                return sb.ToString();
            }
        }

        public EngineUiSnapshot GetUiSnapshot(int pageSize)
        {
            lock (_lock)
            {
                EnsureMixedDecodeCurrent();

                string inputCode = _compositionState == CompositionState.CnSentence
                    ? GetSentenceDisplayCode()
                    : _inputBuffer.ToString();
                bool isComposing = _compositionState == CompositionState.CnComposing ||
                                   _compositionState == CompositionState.CnPinyin ||
                                   _compositionState == CompositionState.CnUpperCase ||
                                   _compositionState == CompositionState.CnSentence;

                List<string> allList = null;
                List<string> list = null;
                if (_compositionState == CompositionState.CnComposing)
                {
                    allList = ResolveCandidates(inputCode);
                    list = GetCandidatePage(inputCode, CompositionState.CnComposing, allList, out _);
                }
                else if (_compositionState == CompositionState.CnPinyin)
                {
                    string pinyinCode = inputCode.Length > 0 ? inputCode.Substring(1) : string.Empty;
                    allList = ResolvePinyinCandidates(pinyinCode);
                    list = GetCandidatePage(inputCode, CompositionState.CnPinyin, allList, out _);
                }
                else if (_compositionState == CompositionState.CnSentence)
                {
                    if (_sentenceResultLexiconVersion != _state.LexiconVersion && !_sentenceDecodeWorkerRunning)
                    {
                        RebuildSentenceInput();
                        inputCode = GetSentenceDisplayCode();
                    }
                    string sentenceRawCode = _sentenceRawBuffer.ToString();
                    SentenceCandidate[] sentenceCandidates = GetPublishedSentenceCandidates(sentenceRawCode);
                    allList = sentenceCandidates.Select(candidate => candidate.Text).ToList();
                    list = allList;
                }

                if (list == null)
                {
                    list = new List<string>();
                }

                int size = pageSize;
                if (size < 1)
                {
                    size = 1;
                }
                else if (size > 10)
                {
                    size = 10;
                }

                string[] page = list.Take(size).ToArray();
                string[] displayPage = new string[page.Length];
                for (int i = 0; i < page.Length; i++)
                {
                    // Keep raw candidate for selection/annotation lookup, but show
                    // converted runtime text in candidate window (align old behavior).
                    displayPage[i] = GetCandidateDisplayText(page[i]);
                }
                string[] annotations = Array.Empty<string>();
                if (page.Length > 0)
                {
                    bool pinyinMode = _compositionState == CompositionState.CnPinyin;
                    annotations = new string[page.Length];
                    for (int i = 0; i < page.Length; i++)
                    {
                        annotations[i] = _state.GetCandidateAnnotation(page[i], pinyinMode);
                    }
                }

                return new EngineUiSnapshot
                {
                    IsChinese = _isChinese,
                    IsComposing = isComposing && inputCode.Length > 0,
                    InputCode = GetMixedResolvedPrefixText() + inputCode,
                    CompositionPrefix = GetMixedResolvedPrefixText(),
                    ActiveInputCode = inputCode,
                    Candidates = displayPage,
                    CandidateAnnotations = annotations,
                    SelectedCandidateIndex = _compositionState == CompositionState.CnSentence &&
                                             _sentenceSelectedIndex >= 0 &&
                                             _sentenceSelectedIndex < displayPage.Length
                        ? _sentenceSelectedIndex
                        : -1,
                    CompositionState = (int)_compositionState
                };
            }
        }

        private SentenceCandidate[] GetPublishedSentenceCandidates(string sentenceRawCode)
        {
            SentenceCandidate[] current = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            if (_sentenceResultLexiconVersion != _state.LexiconVersion)
            {
                return Array.Empty<SentenceCandidate>();
            }

            if (string.Equals(_sentenceDecodeResult.RawCode, sentenceRawCode, StringComparison.Ordinal))
            {
                if (_sentenceCommittedText.Length == 0)
                {
                    return current;
                }

                return current
                    .Where(candidate => candidate.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
                    .Select(candidate => new SentenceCandidate
                    {
                        Text = candidate.Text.Substring(_sentenceCommittedText.Length),
                        SegmentedCode = candidate.SegmentedCode,
                        BaseScore = candidate.BaseScore,
                        FinalScore = candidate.FinalScore,
                        ConfidenceScore = candidate.ConfidenceScore,
                        SupplementScore = candidate.SupplementScore,
                        Boundary = candidate.Boundary,
                        MaxLexiconRank = candidate.MaxLexiconRank
                    })
                    .Where(candidate => candidate.Text.Length > 0)
                    .ToArray();
            }

            // Decode is still catching up. Keep the last list so Overlay does not
            // collapse to a one-row code window between keys.
            if (sentenceRawCode.Length > 0 && current.Length > 0)
            {
                if (_sentenceCommittedText.Length == 0)
                {
                    return current;
                }

                return current
                    .Where(candidate => candidate.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
                    .Select(candidate => new SentenceCandidate
                    {
                        Text = candidate.Text.Substring(_sentenceCommittedText.Length),
                        SegmentedCode = candidate.SegmentedCode,
                        BaseScore = candidate.BaseScore,
                        FinalScore = candidate.FinalScore,
                        ConfidenceScore = candidate.ConfidenceScore,
                        SupplementScore = candidate.SupplementScore,
                        Boundary = candidate.Boundary,
                        MaxLexiconRank = candidate.MaxLexiconRank
                    })
                    .Where(candidate => candidate.Text.Length > 0)
                    .ToArray();
            }

            return Array.Empty<SentenceCandidate>();
        }

        private string GetSentenceDisplayCode()
        {
            string fullRawCode = _sentenceRawBuffer.ToString();
            string rawCode = fullRawCode;
            if (_sentenceCommittedRawLength > 0 && _sentenceCommittedRawLength <= fullRawCode.Length)
            {
                rawCode = fullRawCode.Substring(_sentenceCommittedRawLength);
            }
            if (_sentenceResultLexiconVersion != _state.LexiconVersion)
            {
                return rawCode;
            }

            SentenceCandidate[] candidates = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            int index = _sentenceSelectedIndex >= 0 && _sentenceSelectedIndex < candidates.Length
                ? _sentenceSelectedIndex
                : 0;
            string segmented = index < candidates.Length ? candidates[index].SegmentedCode : null;
            if (string.IsNullOrEmpty(segmented))
            {
                return rawCode;
            }

            string decodedRawCode = _sentenceDecodeResult.RawCode ?? string.Empty;
            string fullDisplayCode;
            if (string.Equals(decodedRawCode, fullRawCode, StringComparison.Ordinal))
            {
                fullDisplayCode = segmented;
            }
            else if (fullRawCode.StartsWith(decodedRawCode, StringComparison.Ordinal))
            {
                fullDisplayCode = segmented + fullRawCode.Substring(decodedRawCode.Length);
            }
            else if (decodedRawCode.StartsWith(fullRawCode, StringComparison.Ordinal))
            {
                fullDisplayCode = TrimSegmentedCodeToRawPrefix(segmented, fullRawCode);
            }
            else
            {
                return rawCode;
            }

            if (_sentenceCommittedRawLength > 0)
            {
                string trimmed = TrimSegmentedCodeAfterRawPrefix(fullDisplayCode, _sentenceCommittedRawLength);
                return string.IsNullOrEmpty(trimmed) && rawCode.Length > 0 ? rawCode : trimmed;
            }

            return fullDisplayCode;
        }

        internal EngineDifferentialSnapshot GetDifferentialSnapshot(int pageSize)
        {
            lock (_lock)
            {
                EngineUiSnapshot ui = GetUiSnapshot(pageSize);
                string rawInput;
                if (_compositionState == CompositionState.CnSentence)
                {
                    rawInput = _sentenceRawBuffer.ToString();
                }
                else if (_mixedRawBuffer.Length > 0)
                {
                    rawInput = _mixedRawBuffer.ToString();
                }
                else
                {
                    rawInput = _inputBuffer.ToString();
                }

                return new EngineDifferentialSnapshot
                {
                    Ui = ui,
                    RawInput = rawInput,
                    SentenceCommittedText = _sentenceCommittedText,
                    SentenceCommittedRawLength = _sentenceCommittedRawLength,
                    CandidatePageIndex = _candidatePageIndex,
                    SentenceGeneration = _sentenceGeneration
                };
            }
        }

        internal bool ShouldExpectKeyUp(int vk, int scan, bool extended)
        {
            lock (_lock)
            {
                int resolvedVk = ResolveSelectionVirtualKey(vk, scan, extended);
                if (IsShiftKey(resolvedVk) ||
                    resolvedVk == VK_OEM_7 ||
                    resolvedVk == VK_CAPITAL)
                {
                    return true;
                }

                if (_state.GetCtrlSpaceToggleEnabled() &&
                    (IsControlKey(resolvedVk) || resolvedVk == VK_SPACE))
                {
                    return true;
                }

                return _handledModifierSelectionKeys.Contains(resolvedVk) ||
                       _oneShotActionKey == resolvedVk;
            }
        }

        private SentenceDecodeResult FilterSentenceDecodeResultForCommittedPrefix(SentenceDecodeResult result)
        {
            if (_sentenceCommittedText.Length == 0 || result == null)
            {
                return result ?? SentenceDecodeResult.Empty;
            }

            SentenceCandidate[] candidates = result.Candidates ?? Array.Empty<SentenceCandidate>();
            SentenceCandidate[] filtered = candidates
                .Where(candidate => candidate != null &&
                    candidate.Text != null &&
                    candidate.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal))
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
                ExpandedStates = result.ExpandedStates
            };
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

            return index < segmented.Length ? segmented.Substring(index) : string.Empty;
        }

        private static string TrimSegmentedCodeToRawPrefix(string segmented, string rawPrefix)
        {
            if (string.IsNullOrEmpty(segmented) || string.IsNullOrEmpty(rawPrefix))
            {
                return rawPrefix ?? string.Empty;
            }

            int kept = 0;
            int rawCount = 0;
            for (int i = 0; i < segmented.Length && rawCount < rawPrefix.Length; i++)
            {
                if (segmented[i] == ' ')
                {
                    kept = i + 1;
                    continue;
                }

                if (segmented[i] != rawPrefix[rawCount])
                {
                    return rawPrefix;
                }

                rawCount++;
                kept = i + 1;
            }

            return rawCount == rawPrefix.Length ? segmented.Substring(0, kept) : rawPrefix;
        }

        private void EnsureMixedDecodeCurrent()
        {
            if (IsMixedInputSession() &&
                (_mixedDecodedLexiconVersion != _state.LexiconVersion ||
                 _mixedDecodedMaxCodeLength != GetSafeMaxCodeLen()))
            {
                RebuildMixedInput();
            }
        }
    }

    internal sealed class EngineUiSnapshot
    {
        public bool IsChinese { get; set; }
        public bool IsComposing { get; set; }
        public string InputCode { get; set; }
        public string CompositionPrefix { get; set; }
        public string ActiveInputCode { get; set; }
        public string[] Candidates { get; set; }
        public string[] CandidateAnnotations { get; set; }
        public int SelectedCandidateIndex { get; set; }
        public int CompositionState { get; set; }
    }

    internal sealed class EngineDifferentialSnapshot
    {
        public EngineUiSnapshot Ui { get; set; }
        public string RawInput { get; set; }
        public string SentenceCommittedText { get; set; }
        public int SentenceCommittedRawLength { get; set; }
        public int CandidatePageIndex { get; set; }
        public long SentenceGeneration { get; set; }
    }

    internal sealed class KeyEngineResult
    {
        public bool Handled { get; private set; }
        public bool IsChinese { get; private set; }
        public bool IsComposing { get; private set; }
        public string TextToOutput { get; private set; }
        public string InputBuffer { get; private set; }
        public bool OpenAddCiWindow { get; private set; }
        public bool CancelComposition { get; private set; }

        public void SetTextToOutput(string textToOutput)
        {
            TextToOutput = textToOutput;
        }

        public void SetInputBuffer(string inputBuffer)
        {
            InputBuffer = inputBuffer;
        }

        public void SetIsComposing(bool isComposing)
        {
            IsComposing = isComposing;
        }

        public void SetOpenAddCiWindow(bool openAddCiWindow)
        {
            OpenAddCiWindow = openAddCiWindow;
        }

        public static KeyEngineResult Pass(bool isChinese)
        {
            return new KeyEngineResult
            {
                Handled = false,
                IsChinese = isChinese,
                IsComposing = false,
                TextToOutput = null,
                InputBuffer = null,
                OpenAddCiWindow = false,
                CancelComposition = false
            };
        }

        public static KeyEngineResult Pass(bool isChinese, string textToOutput, string inputBuffer, bool isComposing)
        {
            return new KeyEngineResult
            {
                Handled = false,
                IsChinese = isChinese,
                IsComposing = isComposing,
                TextToOutput = textToOutput,
                InputBuffer = inputBuffer,
                OpenAddCiWindow = false,
                CancelComposition = false
            };
        }

        public static KeyEngineResult Pass(bool isChinese, string textToOutput, string inputBuffer, bool isComposing, bool cancelComposition)
        {
            return new KeyEngineResult
            {
                Handled = false,
                IsChinese = isChinese,
                IsComposing = isComposing,
                TextToOutput = textToOutput,
                InputBuffer = inputBuffer,
                OpenAddCiWindow = false,
                CancelComposition = cancelComposition
            };
        }

        public static KeyEngineResult CreateHandled(bool isChinese, string textToOutput = null, string inputBuffer = null, bool isComposing = false, bool openAddCiWindow = false)
        {
            return new KeyEngineResult
            {
                Handled = true,
                IsChinese = isChinese,
                IsComposing = isComposing,
                TextToOutput = textToOutput,
                InputBuffer = inputBuffer,
                OpenAddCiWindow = openAddCiWindow,
                CancelComposition = false
            };
        }
    }
}
