using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TigerClaw.Pinyin;
using PinyinDecoder = TigerClaw.Pinyin.Decoder;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private readonly PinyinSession _pinyinSession = new();
        private readonly object _pinyinDecodeGate = new();
        private readonly LatestPinyinWorker _pinyinWorker = new();
        private Task<PinyinSession> _pinyinPrepared;
        private Action<PinyinPerformanceSample> _pinyinPerformance;
        internal void SetPinyinPerformanceCallback(Action<PinyinPerformanceSample> callback)
        { lock (_lock) _pinyinPerformance = callback; }
        private readonly Dictionary<string, Task<PinyinResources>> _pinyinResources = new(StringComparer.OrdinalIgnoreCase);
        private Task<PinyinResources> _pinyinLoading;
        private PinyinDecoder _pinyinDecoder;
        private PinyinSpellingOptions _pinyinSpelling;
        private PinyinResources _pinyinDecoderResources;
        private CancellationTokenSource _pinyinCancellation;
        private string _pinyinDirectory, _pinyinError;
        private string _pinyinMenuToken = Guid.NewGuid().ToString("N");
        private SentenceLearningStore _pinyinLearning;
        private readonly Dictionary<string, SentenceLearningStore> _pinyinLearningStores = new(StringComparer.OrdinalIgnoreCase);
        private SentenceLearningEvent[] _pinyinReadyLearning = Array.Empty<SentenceLearningEvent>();
        private string _pinyinLearningOutput;
        private const string PinyinLearningMode = SentenceLearning.PinyinPhraseMode;
        private bool FullPinyinPending => _pinyinError == null && _pinyinSession.AppliedGeneration != _pinyinSession.Generation;

        private void ConfigureFullPinyin()
        {
            string directory = _state.GetFullPinyinDirectory();
            if (directory == _pinyinDirectory) return;
            _pinyinCancellation?.Cancel();
            _pinyinDirectory = directory; _pinyinError = null;
            _pinyinReadyLearning = Array.Empty<SentenceLearningEvent>();
            if (directory == null) { _pinyinLoading = null; _pinyinLearning = null; return; }
            if (!_pinyinResources.TryGetValue(directory, out _pinyinLoading))
                _pinyinResources[directory] = _pinyinLoading = Task.Run(() => new PinyinResources(directory));
            if (!_pinyinLearningStores.TryGetValue(directory, out _pinyinLearning))
                _pinyinLearningStores[directory] = _pinyinLearning = new SentenceLearningStore(Path.Combine(directory, ".tigerclaw-learning-pinyin-v1.log"));
            _pinyinLearning.RefreshAsync();
        }
        internal bool TryEditFullPinyinWord(string text, string reading, bool delete, out string error)
        {
            lock (_lock)
            {
                error = null;
                ConfigureFullPinyin();
                if (_pinyinLoading?.IsCompletedSuccessfully != true)
                { error = "全拼资源尚未就绪，请稍后重试。"; return false; }
                try
                {
                    var word = PinyinUserWords.Validate(text, reading);
                    var resources = _pinyinLoading.Result;
                    var words = PinyinUserWords.Load(_pinyinDirectory).Where(w => w.Text != word.Text || !w.Readings.SequenceEqual(word.Readings)).ToList();
                    if (!delete) words.Add(word);
                    var lexicon = PinyinUserWords.Merge(resources.Baseline, words);
                    PinyinUserWords.Save(_pinyinDirectory, words.ToArray());
                    _pinyinCancellation?.Cancel();
                    lock (_pinyinDecodeGate)
                    { resources.ReplaceLexicon(lexicon); _pinyinDecoderResources = null; }
                    if (_compositionState == CompositionState.FullPinyin)
                    { _pinyinSession.Reset(_pinyinSession.Raw); RebuildFullPinyin(); }
                    return true;
                }
                catch (Exception e) { error = e.Message; return false; }
            }
        }
        private void DisposeFullPinyin()
        {
            _pinyinCancellation?.Cancel();
            _pinyinWorker.Stop();
            // Free only after any native query has left the gate. Never wait on
            // a worker that needs the engine lock to publish its final snapshot.
            var loads = _pinyinResources.Values.ToArray();
            _ = Task.Run(async () =>
            {
                lock (_pinyinDecodeGate) { _pinyinDecoder?.Dispose(); _pinyinDecoder = null; }
                foreach (var load in loads)
                {
                    try { var resources = await load.ConfigureAwait(false); lock (_pinyinDecodeGate) resources.Dispose(); }
                    catch (Exception) { }
                }
            });
        }
        private void ClearFullPinyin()
        {
            _pinyinCancellation?.Cancel();
            _pinyinSession.Reset(); _pinyinError = null; _pinyinLiteralMode = false; _pinyinAuxCode = null;
            _pinyinMenuToken = Guid.NewGuid().ToString("N");
        }
        private void StartFullPinyin(string raw)
        {
            ConfigureFullPinyin(); _compositionState = CompositionState.FullPinyin;
            _pinyinLiteralMode = false; _pinyinAuxCode = null;
            _pinyinSession.Reset(raw); RebuildFullPinyin();
        }
        private void RebuildFullPinyin()
        {
            _pinyinMenuToken = Guid.NewGuid().ToString("N");
            _inputBuffer.Clear(); _inputBuffer.Append(_pinyinSession.Raw);
            if (_pinyinAuxCode != null) _inputBuffer.Append("`").Append(_pinyinAuxCode);
            _pinyinCancellation?.Cancel();
            _pinyinCancellation?.Dispose();
            _pinyinCancellation = new CancellationTokenSource();
            if (RebuildSpecialPinyin()) { _pinyinError = null; return; }
            var auxiliary = _pinyinAuxCode;
            var cancellation = _pinyinCancellation.Token;
            string raw = _pinyinSession.Raw;
            var prefix = _pinyinSession.Prefix;
            long generation = _pinyinSession.Generation;
            var loading = _pinyinLoading;
            var spelling = _state.GetPinyinSpellingOptions();
            _pinyinError = null;
            _pinyinLearning?.RefreshAsync();
            var prepared = _pinyinSession.ForkForDecode();
            var learningStore = _pinyinLearning;
            bool learn = _state.GetFullPinyinLearningEnabled();
            bool english = _state.GetPinyinEnglishEnabled(), emoji = _state.GetPinyinEmojiEnabled();
            var diagnostic = _pinyinPerformance;
            long Stamp() => diagnostic == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            long submitted = Stamp();
            var completion = new TaskCompletionSource<PinyinSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pinyinPrepared = completion.Task;
            cancellation.Register(() => completion.TrySetCanceled(cancellation));
            _pinyinWorker.Submit(async () =>
            {
                string error = null;
                long started = Stamp(), searchStarted = 0, decodedAt = 0, rankedAt = 0, preparedAt = 0;
                try
                {
                    if (loading == null) throw new InvalidDataException("全拼方案资源未配置");
                    var resources = await loading.WaitAsync(cancellation).ConfigureAwait(false);
                    // Faster/shared resource loading must not outrun the first
                    // journal replay. Await queued learning IO off the key lock,
                    // then keep one immutable snapshot for this generation.
                    if (learn && learningStore != null)
                        await learningStore.FlushAsync().WaitAsync(cancellation).ConfigureAwait(false);
                    var learning = learningStore?.Snapshot;
                    cancellation.ThrowIfCancellationRequested();
                    lock (_pinyinDecodeGate)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (_pinyinDecoderResources != resources || _pinyinSpelling != spelling || _pinyinDecoderAux != auxiliary)
                        {
                            _pinyinDecoder?.Dispose();
                            _pinyinDecoder = new PinyinDecoder(resources.Lexicon, resources.Model, 200, spellingOptions: spelling, firstCharacters: resources.Auxiliary.Match(auxiliary));
                            _pinyinDecoderResources = resources; _pinyinSpelling = spelling; _pinyinDecoderAux = auxiliary;
                        }
                        searchStarted = Stamp();
                        DecodeResult decoded;
                        try { decoded = _pinyinDecoder.Decode(raw, resources.CandidateLimit, prefix: prefix,
                            completeLastSyllable: true, cancellation: cancellation); }
                        catch (ArgumentException) { decoded = new DecodeResult([], 0, raw, 0); }
                        decoded = RetainLearnedPinyin(resources, raw, prefix, decoded, cancellation, spelling, auxiliary, learning, learn);
                        decodedAt = Stamp();
                        decoded = resources.Rank(decoded, cancellation);
                        rankedAt = Stamp();
                        PrepareFullPinyin(prepared, decoded, resources, spelling, auxiliary, learning, learn, english, emoji);
                        preparedAt = Stamp();
                    }
                    cancellation.ThrowIfCancellationRequested();
                    completion.TrySetResult(prepared);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled();
                    diagnostic?.Invoke(new(generation, submitted, started, searchStarted, decodedAt, rankedAt, preparedAt, Stamp(), true));
                    return;
                }
                catch (Exception e) { error = e.Message; completion.TrySetException(e); _ = completion.Task.Exception; }
                Action publish;
                lock (_lock)
                {
                    if (_engineDisposed || cancellation.IsCancellationRequested ||
                        _compositionState != CompositionState.FullPinyin || generation != _pinyinSession.Generation || loading != _pinyinLoading) return;
                    _pinyinError = error;
                    if (error == null && _pinyinSession.AppliedGeneration != generation)
                    { _pinyinSession.AdoptPrepared(prepared); _pinyinMenuToken = Guid.NewGuid().ToString("N"); }
                    publish = _sentenceDecodeCompletedCallback;
                }
                publish?.Invoke();
                diagnostic?.Invoke(new(generation, submitted, started, searchStarted, decodedAt, rankedAt, preparedAt, Stamp(), false));
            });
        }
        private DecodeResult RetainLearnedPinyin(PinyinResources resources, string raw, Candidate prefix, DecodeResult baseline, CancellationToken cancellation, PinyinSpellingOptions spelling, string auxiliary, SentenceLearningSnapshot learning, bool learn)
        {
            var matches = (learn && learning != null
                ? learning.PinyinMatches(PinyinLearningMode, Lexicon.NormalizeCode(raw))
                    .Concat(learning.PinyinCharacterMatches(Lexicon.NormalizeCode(raw))).ToArray()
                : Array.Empty<(string Code, string Text, double Score)>())
                .Concat(resources.Preferences.MatchingPins(Lexicon.NormalizeCode(raw)).Select(p => (p.Code, p.Text, 0.0)))
                .DistinctBy(m => (m.Code, m.Text)).Take(32).ToArray();
            if (matches.Length == 0) return baseline;
            var candidates = baseline.Candidates.ToList();
            foreach (var match in matches)
            {
                cancellation.ThrowIfCancellationRequested();
                if (prefix != null && (!match.Text.StartsWith(prefix.Text, StringComparison.Ordinal) || match.Code.Length < (prefix.Segments.LastOrDefault()?.End ?? 0))) continue;
                using var legal = new PinyinDecoder(resources.Lexicon, resources.Model, 200, requiredText: match.Text, spellingOptions: spelling, firstCharacters: resources.Auxiliary.Match(auxiliary));
                var retained = legal.Decode(match.Code, 1, false, prefix: prefix, cancellation: cancellation);
                if (retained.Consumed != match.Code.Length || retained.Candidates.Length == 0) continue;
                if (match.Code.Length == raw.Length) candidates.AddRange(retained.Candidates.Where(c =>
                    SentenceLearning.Characters(match.Text) != 1 || c.Segments.All(s => !s.Incomplete)));
                else
                {
                    using var continuation = new PinyinDecoder(resources.Lexicon, resources.Model, 200, spellingOptions: spelling, firstCharacters: resources.Auxiliary.Match(auxiliary));
                    var extended = continuation.Decode(raw, 1, false, prefix: retained.Candidates[0], cancellation: cancellation);
                    if (extended.Consumed == raw.Length) candidates.AddRange(extended.Candidates);
                }
            }
            // Retention affects manual candidates only. Full-pinyin never uses
            // learned paths as automatic-commit confidence evidence.
            return baseline with { Candidates = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Frequency).ThenBy(c => c.Text, StringComparer.Ordinal).DistinctBy(c => c.Text).ToArray() };
        }
        private static void PrepareFullPinyin(PinyinSession session, DecodeResult decoded, PinyinResources resources, PinyinSpellingOptions spelling, string auxiliary, SentenceLearningSnapshot snapshot, bool learn, bool english, bool emoji)
        {
            var matches = learn && snapshot != null
                ? snapshot.PinyinMatches(PinyinLearningMode, Lexicon.NormalizeCode(session.Raw)) : Array.Empty<(string Code, string Text, double Score)>();
            var characterMatches = learn && snapshot != null
                ? snapshot.PinyinCharacterMatches(Lexicon.NormalizeCode(session.Raw)) : Array.Empty<(string Code, string Text, double Score)>();
            var rewards = decoded.Candidates.ToDictionary(c => c.Text, c => matches.Where(m =>
                (m.Code.Length == session.Raw.Length && c.Text == m.Text) ||
                c.Segments.Any(s => s.End == m.Code.Length) && string.Concat(c.Segments.Where(s => s.End <= m.Code.Length).Select(s => s.Text)) == m.Text)
                .Select(m => m.Score).Concat(characterMatches.Where(m => c.Text == m.Text &&
                    decoded.Consumed == session.Raw.Length && c.Segments.All(s => !s.Incomplete)).Select(m => m.Score)).DefaultIfEmpty(0).Max());
            session.Apply(session.Generation, decoded, (raw, text) => rewards.GetValueOrDefault(text), resources.Lexicon, spelling, resources.Preferences,
                resources.Auxiliary.Match(auxiliary), deferMenu: true);
            ApplyPinyinExtras(session, resources, auxiliary, english, emoji);
        }
        private bool EnsureFullPinyinCurrent()
        {
            if (_pinyinSession.AppliedGeneration == _pinyinSession.Generation) return true;
            var pending = _pinyinPrepared;
            long generation = _pinyinSession.Generation;
            if (pending == null) return false;
            PinyinSession prepared = null; Exception failure = null;
            // An explicit confirmation joins the existing generation; it neither
            // cancels it nor repeats Beam. The worker never waits for this lock.
            bool held = Monitor.IsEntered(_lock);
            if (held) Monitor.Exit(_lock);
            try { prepared = pending.GetAwaiter().GetResult(); }
            catch (Exception e) { failure = e; }
            finally { if (held) Monitor.Enter(_lock); }
            if (_engineDisposed || generation != _pinyinSession.Generation || pending != _pinyinPrepared ||
                _compositionState != CompositionState.FullPinyin) return false;
            if (failure != null)
            { if (failure is not OperationCanceledException) _pinyinError = failure.Message; return false; }
            if (_pinyinSession.AppliedGeneration != generation)
            { _pinyinSession.AdoptPrepared(prepared); _pinyinMenuToken = Guid.NewGuid().ToString("N"); }
            return true;
        }
        private KeyEngineResult FullPinyinResult() => KeyEngineResult.CreateHandled(true, null, PinyinLiveDisplay, true);
        private KeyEngineResult FinishFullPinyin(string text)
        {
            ClearCompositionInput(); _compositionState = CompositionState.CnIdle;
            return KeyEngineResult.CreateHandled(true, string.IsNullOrEmpty(text) ? null : text, "", false);
        }
        private KeyEngineResult ConfirmFullPinyin(int index, string suffix = "")
        {
            if (!EnsureFullPinyinCurrent() || index < 0 || index >= _pinyinSession.Choices.Length) return FullPinyinResult();
            string output = _pinyinSession.Confirm(index, _state.GetFullPinyinLearningEnabled());
            if (output == null) { RebuildFullPinyin(); return FullPinyinResult(); }
            _pinyinReadyLearning = _pinyinSession.Corrections.Where(c => SentenceLearning.StaticText(c.Text) && output.StartsWith(c.Text, StringComparison.Ordinal))
                .Select(c => new SentenceLearningEvent { Mode = SentenceLearning.EffectiveMode(PinyinLearningMode, c.Text), Code = c.Raw, Text = c.Text, RawEnd = c.Raw.Length, TextEnd = c.Text.Length }).ToArray();
            _pinyinLearningOutput = PinyinOutput(output + suffix);
            return FinishFullPinyin(_pinyinLearningOutput);
        }
        private KeyEngineResult ProcessFullPinyinKeyDown(int vk, bool shift)
        {
            if (ProcessPinyinExtraKey(vk, shift, out var extra)) return extra;
            if (vk >= VK_A && vk <= VK_Z) _pinyinSession.Insert((char)((shift ? 'A' : 'a') + vk - VK_A));
            else if (vk == VK_OEM_7 && !shift) _pinyinSession.Insert('\'');
            else if (vk == VK_BACK) _pinyinSession.Backspace();
            else if (vk == 0x2e) _pinyinSession.Delete();
            else if (vk == VK_ESCAPE) return FinishFullPinyin("");
            else if (vk == VK_RETURN) return FinishFullPinyin(_inputBuffer.ToString());
            else if (vk == 0x25 || vk == 0x27 || vk == 0x24 || vk == 0x23)
            {
                _pinyinSession.MoveCursor(vk == 0x24 ? _pinyinSession.LockedEnd : vk == 0x23 ? _pinyinSession.Raw.Length : vk == 0x25 ? -1 : 1, vk == 0x24 || vk == 0x23);
                return FullPinyinResult();
            }
            else if (vk == VK_TAB || vk == VK_UP || vk == VK_DOWN || vk == VK_PRIOR || vk == VK_NEXT || vk == 0x71)
            {
                if (EnsureFullPinyinCurrent())
                {
                    _pinyinMenuToken = Guid.NewGuid().ToString("N");
                    if (vk == 0x71) _pinyinSession.ToggleMenu();
                    else _pinyinSession.MoveSelection(vk == VK_PRIOR ? -PinyinPageSize : vk == VK_NEXT ? PinyinPageSize : (vk == VK_UP || (vk == VK_TAB && shift)) ? -1 : 1);
                }
                return FullPinyinResult();
            }
            else if (vk == VK_SPACE) return ConfirmFullPinyin(_pinyinSession.Selected);
            else if (!shift && ((vk >= VK_0 && vk <= VK_9) || (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9)))
            {
                if (!EnsureFullPinyinCurrent()) return FullPinyinResult();
                int digit = vk >= VK_NUMPAD0 ? vk - VK_NUMPAD0 : vk - VK_0;
                int index = _pinyinSession.Selected / PinyinPageSize * PinyinPageSize + (digit == 0 ? 9 : digit - 1);
                return index % PinyinPageSize == (digit == 0 ? 9 : digit - 1) ? ConfirmFullPinyin(index) : FullPinyinResult();
            }
            else if (TryFullPinyinPunctuation(vk, shift, out string punctuation))
            {
                if (EnsureFullPinyinCurrent() && (_pinyinSession.ExactComplete || _pinyinSession.Choices.FirstOrDefault()?.Literal == true))
                {
                    if (vk == VK_OEM_7 && shift && !_state.GetUseEnPuncInCn()) punctuation = EmitSmartQuote(isDoubleQuote: true);
                    return ConfirmFullPinyin(0, punctuation);
                }
                return FullPinyinResult();
            }
            else return KeyEngineResult.Pass(true, null, PinyinLiveDisplay, true);
            if (_pinyinSession.Raw.Length == 0) return FinishFullPinyin("");
            RebuildFullPinyin(); return FullPinyinResult();
        }
        private bool TryFullPinyinPunctuation(int vk, bool shift, out string text)
        {
            if (TryMapStandalonePunctuation(vk, shift, out text)) return true;
            bool english = _state.GetUseEnPuncInCn();
            text = vk switch
            {
                VK_OEM_1 when shift => english ? ":" : "：",
                VK_OEM_2 when shift => english ? "?" : "？",
                VK_OEM_7 when shift => "\"",
                VK_OEM_COMMA when shift => english ? "<" : "《",
                VK_OEM_PERIOD when shift => english ? ">" : "》",
                VK_OEM_4 => shift ? "{" : english ? "[" : "【",
                VK_OEM_6 => shift ? "}" : english ? "]" : "】",
                VK_OEM_5 => shift ? "|" : "\\",
                VK_OEM_MINUS => shift ? "_" : "-",
                VK_OEM_PLUS => shift ? "+" : "=",
                VK_OEM_3 => shift ? "~" : "`",
                VK_1 when shift => english ? "!" : "！",
                VK_0 when shift => english ? ")" : "）",
                VK_9 when shift => english ? "(" : "（",
                _ => null
            };
            if (text == null && shift && vk >= 0x32 && vk <= 0x38) text = "@#$%^&*"[vk - 0x32].ToString();
            return text != null;
        }
        internal KeyEngineResult SelectFullPinyinCandidate(string token, int index)
        {
            lock (_lock)
            {
                if (_compositionState != CompositionState.FullPinyin || FullPinyinPending ||
                    token != _pinyinMenuToken || index < 0 || index >= 64 || index % 16 >= PinyinPageSize)
                    return KeyEngineResult.CreateHandled(_isChinese, null, null, HasCompositionInput());
                int pageIndex = _pinyinSession.Selected / PinyinPageSize * PinyinPageSize + index % 16;
                return index < 16 ? ConfirmFullPinyin(pageIndex) : ManagePinyinCandidate(index < 32 ? "pin" : index < 48 ? "unpin" : "forget", pageIndex);
            }
        }
        internal int FullPinyinCursor
        {
            get { lock (_lock) return _compositionState == CompositionState.FullPinyin ? (_pinyinAuxCode == null ? _pinyinSession.DisplayCursor : PinyinLiveDisplay.Length) : -1; }
        }
        private int PinyinPageSize => Math.Clamp(_state.GetPageSize(), 1, 10);
        private EngineUiSnapshot GetFullPinyinUi(int pageSize)
        {
            int size = Math.Clamp(pageSize, 1, 10), offset = _pinyinSession.Selected / size * size;
            var choices = _pinyinSession.Choices.Skip(offset).Take(size).ToArray();
            return new EngineUiSnapshot
            {
                IsChinese = true, IsComposing = true, CompositionState = (int)CompositionState.FullPinyin,
                InputCode = PinyinLiveDisplay + (_pinyinError != null ? " [全拼资源/编码错误]" : ""),
                CompositionPrefix = PinyinOutput(_pinyinSession.LockedText), ActiveInputCode = _pinyinSession.Raw[_pinyinSession.LockedEnd..] + (_pinyinAuxCode == null ? "" : "`" + _pinyinAuxCode) + (_pinyinError != null ? " [全拼资源不可用；Enter 输出拼音]" : ""),
                CandidateSelectionToken = FullPinyinPending ? "" : _pinyinMenuToken,
                Candidates = choices.Select(c => PinyinOutput(c.Text)).ToArray(),
                CandidateAnnotations = choices.Select(c =>
                    (_pinyinDecoderResources?.Preferences.IsPinned(Lexicon.NormalizeCode(_pinyinSession.Raw[..c.Consumed]), c.Path.Text) == true ? "置顶 · " : "") +
                    (c.Annotation.Length > 0 ? c.Annotation : c.Whole ? "整句 · F2 切换" : "选词")).ToArray(),
                SelectedCandidateIndex = _pinyinSession.Selected - offset
            };
        }
    }
}
