using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal enum CoreUiCommand
    {
        ShowConfig = 1,
        ShowAddCi = 2,
        ExitCore = 3,
        ShowMenu = 4
    }

    internal sealed class ProtocolHandler : IDisposable
    {
        private readonly Action<CoreUiCommand> _uiCommandCallback;
        private readonly CoreRuntimeState _state;
        private readonly InputMethodEngine _engine;
        private readonly UiStatePublisher _uiStatePublisher;
        private readonly SentenceRerankClient _sentenceRerankClient;
        private readonly object _publishLock = new object();
        private const int FreshCaretAwaitWindowMs = 30;
        private bool _hookNativeDisabled;
        // TSF 是否处于激活态（本 IME 被选中且焦点在可编辑文档）。默认 false → 启动即隐藏状态窗，直到首个 ime_active:true。
        private bool _imeActive;
        private bool _isNativeHookStatus;
        private long _soundSeq;
        private int _soundVk;
        private int _soundVolumePercent;
        private bool _awaitingFreshCaretForComposition;
        private long _awaitingFreshCaretDeadlineTick;
        private bool _pendingFrontendCompositionReset;

        public ProtocolHandler(Action<CoreUiCommand> uiCommandCallback, CoreRuntimeState state, UiStatePublisher uiStatePublisher)
        {
            _uiCommandCallback = uiCommandCallback;
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _uiStatePublisher = uiStatePublisher;
            _engine = new InputMethodEngine(_state);
            _sentenceRerankClient = new SentenceRerankClient(
                _state,
                new ProcessLauncher(),
                OnSentenceRerankResult);
            _engine.SetSentenceRerankService(_sentenceRerankClient);
            _engine.SetChinese(_state.GetDefaultChinese(), out _);
            PublishUiState();
        }

        public void Dispose()
        {
            _sentenceRerankClient?.Dispose();
        }

        private void OnSentenceRerankResult(long generation, string rawCode, double[] scores)
        {
            if (_engine.ApplySentenceNeuralScores(generation, rawCode, scores))
            {
                PublishUiState();
            }
        }

        public string Handle(string json)
        {
            SimpleJsonObject msg = SimpleJson.Parse(json);
            if (msg == null)
            {
                return BuildResponseWithUiState(0, false, false);
            }

            string type = ConvertToString(msg.GetValue("type"));
            int seq = ConvertToInt(msg.GetValue("seq"), 0);

            switch (type)
            {
                case "hello":
                    MarkFrontendMode(ConvertToString(msg.GetValue("frontend")));
                    PublishUiState();
                    return BuildHelloResponse(seq);

                case "query_state":
                    {
                        string frontend = ConvertToString(msg.GetValue("frontend"));
                        MarkFrontendMode(frontend);
                        string hookNativeExtra = BuildHookNativeConfigExtraJson(frontend);
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            false,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"config_version\":" + _state.ConfigVersion + ",\"lexicon_version\":" + _state.LexiconVersion + hookNativeExtra);
                    }

                case "ctrl_space":
                    if (_state.GetCtrlSpaceToggleEnabled())
                    {
                        _engine.ToggleChinese(out string committedByToggle);
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            textToOutput: string.IsNullOrEmpty(committedByToggle) ? null : committedByToggle,
                            inputBuffer: string.Empty,
                            keyboardOpen: _engine.IsChinese);
                    }

                    return BuildResponseWithUiState(
                        seq,
                        true,
                        false,
                        keyboardOpen: _engine.IsChinese);

                case "show_menu":
                    _uiCommandCallback?.Invoke(CoreUiCommand.ShowMenu);
                    return BuildResponseWithUiState(seq, true, true, keyboardOpen: _engine.IsChinese);

                case "show_config":
                    _uiCommandCallback?.Invoke(CoreUiCommand.ShowConfig);
                    return BuildResponseWithUiState(seq, true, true, keyboardOpen: _engine.IsChinese);

                case "show_addci":
                    _uiCommandCallback?.Invoke(CoreUiCommand.ShowAddCi);
                    return BuildResponseWithUiState(seq, true, true, keyboardOpen: _engine.IsChinese);

                case "reload_mb":
                    {
                        bool ok = _state.ReloadLexicon();
                        _engine.ReloadCustomSelectionKeyConfig();
                        if (ok)
                        {
                            _engine.ReloadSentenceResources();
                        }
                        return BuildResponseWithUiState(
                            seq,
                            ok,
                            ok,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"lexicon_version\":" + _state.LexiconVersion);
                    }

                case "open_official":
                    {
                        bool ok = TryOpenTarget("https://github.com/lvyww/bime");
                        return BuildResponseWithUiState(seq, ok, ok, keyboardOpen: _engine.IsChinese);
                    }

                case "open_mb_folder":
                    {
                        string path = _state.GetCurrentCodeTablePath();
                        bool ok = !string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path));
                        string extra = ok ? ",\"path\":" + Quote(path) : null;
                        return BuildResponseWithUiState(seq, ok, ok, keyboardOpen: _engine.IsChinese, extraJsonPairs: extra);
                    }

                case "export_mb":
                    {
                        bool ok = _state.TryExportCurrentLexicon(out string exportPath, out string exportError);
                        if (ok)
                        {
                            ok = TryOpenAndSelectFile(exportPath) || TryOpenTarget(Path.GetDirectoryName(exportPath));
                        }

                        return BuildResponseWithUiState(
                            seq,
                            ok,
                            ok,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: string.IsNullOrWhiteSpace(exportError) ? null : ",\"error\":" + Quote(exportError));
                    }

                case "exit_core":
                    _uiCommandCallback?.Invoke(CoreUiCommand.ExitCore);
                    return BuildResponseWithUiState(seq, true, true, keyboardOpen: _engine.IsChinese);

                case "reload_config":
                    {
                        bool hadComposition = _engine.ResetCompositionForConfigChange();
                        bool cfgOk = _state.ReloadConfig();
                        bool lexOk = _state.ReloadLexicon();
                        _engine.ReloadCustomSelectionKeyConfig();
                        if (lexOk)
                        {
                            _engine.ReloadSentenceResources();
                        }
                        _engine.SetChinese(_state.GetDefaultChinese(), out _);
                        _pendingFrontendCompositionReset |= hadComposition;
                        bool ok = cfgOk && lexOk;
                        return BuildResponseWithUiState(
                            seq,
                            ok,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"config_version\":" + _state.ConfigVersion + ",\"lexicon_version\":" + _state.LexiconVersion);
                    }

                case "get_config":
                    {
                        string configText = _state.GetConfigText() ?? string.Empty;
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"config_text\":" + Quote(configText) + ",\"config_version\":" + _state.ConfigVersion);
                    }

                case "get_schema_list":
                    {
                        string[] schemas = _state.GetSchemaList();
                        string schemaText = string.Join("\n", schemas ?? Array.Empty<string>());
                        string currentSchema = _state.GetCurrentSchema() ?? string.Empty;
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"schema_list\":" + Quote(schemaText) + ",\"current_schema\":" + Quote(currentSchema));
                    }

                case "construct_ci":
                    {
                        string text = ConvertToString(msg.GetValue("text"));
                        string code = _state.ConstructCi(text);
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"code\":" + Quote(code));
                    }

                case "get_last_ci":
                    {
                        int historyLen = ConvertToInt(msg.GetValue("history_len"), 0);
                        if (historyLen < 0)
                        {
                            historyLen = 0;
                        }

                        string text = _engine.GetLastCi(historyLen);
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"text\":" + Quote(text));
                    }

                case "get_selection_key_config":
                    {
                        string configText = _engine.GetCustomSelectionKeyConfigText();
                        string defaultText = _engine.GetDefaultCustomSelectionKeyConfigText();
                        string configPath = _state.GetCustomSelectionKeyConfigPath();
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"config_text\":" + Quote(configText) +
                                            ",\"default_text\":" + Quote(defaultText) +
                                            ",\"config_path\":" + Quote(configPath));
                    }

                case "set_selection_key_config":
                    {
                        string configText = ConvertToString(msg.GetValue("config_text"));
                        bool ok = _engine.TrySaveCustomSelectionKeyConfig(configText, out string error);
                        string extra = ",\"config_text\":" + Quote(_engine.GetCustomSelectionKeyConfigText());
                        if (!ok && !string.IsNullOrWhiteSpace(error))
                        {
                            extra += ",\"error\":" + Quote(error);
                        }

                        return BuildResponseWithUiState(
                            seq,
                            ok,
                            ok,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: extra);
                    }

                case "get_send_history_count":
                    {
                        int count = _engine.GetSendHistoryCount();
                        return BuildResponseWithUiState(
                            seq,
                            true,
                            true,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: ",\"count\":" + count);
                    }

                case "set_config":
                    {
                        string key = ConvertToString(msg.GetValue("key"));
                        string value = ConvertToString(msg.GetValue("value"));
                        bool ok = _state.TrySetConfigValue(key, value, out bool changed, out string reason);
                        bool lexOk = true;
                        if (ok && changed && IsLexiconConfigKey(key))
                        {
                            lexOk = _state.ReloadLexicon();
                        }

                        bool success = ok && lexOk;
                        if (success && changed &&
                            (IsUnlimitedMixedInputConfigKey(key) || IsSentenceInputConfigKey(key)))
                        {
                            _pendingFrontendCompositionReset |= _engine.ResetCompositionForConfigChange();
                            ClearFreshCaretAwaitState();
                        }
                        if (success && changed &&
                            (IsSentenceInputConfigKey(key) || IsLexiconConfigKey(key)))
                        {
                            _engine.ReloadSentenceResources();
                        }
                        string extra = ",\"changed\":" + (changed ? "true" : "false") + ",\"config_version\":" + _state.ConfigVersion + ",\"lexicon_version\":" + _state.LexiconVersion;
                        if (!success && !string.IsNullOrWhiteSpace(reason))
                        {
                            extra += ",\"error\":" + Quote(reason);
                        }

                        return BuildResponseWithUiState(
                            seq,
                            success,
                            success,
                            keyboardOpen: _engine.IsChinese,
                            extraJsonPairs: extra);
                    }

                case "add_ci":
                    {
                        string code = ConvertToString(msg.GetValue("code"));
                        string text = ConvertToString(msg.GetValue("text"));
                        bool ok = _state.TryAddCi(code, text, out string reason);
                        string extra = string.IsNullOrEmpty(reason) ? null : ",\"error\":" + Quote(reason);
                        return BuildResponseWithUiState(seq, ok, ok, keyboardOpen: _engine.IsChinese, extraJsonPairs: extra);
                    }

                case "key":
                    return HandleKeyMessage(msg, seq);

                case "caret":
                    MarkFrontendMode(ConvertToString(msg.GetValue("frontend")));
                    HandleCaretMessage(msg);
                    PublishUiState();
                    return null;

                case "focus":
                    MarkFrontendMode(ConvertToString(msg.GetValue("frontend")));
                    HandleFocusMessage(msg);
                    PublishUiState();
                    return null;

                case "composition_canceled":
                    MarkFrontendMode(ConvertToString(msg.GetValue("frontend")));
                    _engine.OnExternalCompositionCanceled();
                    ClearFreshCaretAwaitState();
                    PublishUiState();
                    return null;

                case "hook_native_disabled":
                    _isNativeHookStatus = true;
                    _hookNativeDisabled = ConvertToBool(msg.GetValue("disabled"), false);
                    if (_hookNativeDisabled)
                    {
                        _engine.OnExternalCompositionCanceled();
                        ClearFreshCaretAwaitState();
                    }
                    PublishUiState();
                    return null;

                case "ime_active":
                    _isNativeHookStatus = false;
                    _imeActive = ConvertToBool(msg.GetValue("active"), false);
                    PublishUiState();
                    return null;

                default:
                    return BuildResponseWithUiState(seq, false, false, keyboardOpen: _engine.IsChinese);
            }
        }

        private string HandleKeyMessage(SimpleJsonObject msg, int seq)
        {
            string frontend = ConvertToString(msg.GetValue("frontend"));
            MarkFrontendMode(frontend);
            EngineUiSnapshot beforeState = _engine.GetUiSnapshot(_state.GetPageSize());
            int vk = ConvertToInt(msg.GetValue("vk"), 0);
            int scan = ConvertToInt(GetFirstValue(msg, "scan", "scan_code"), 0);
            string action = ConvertToString(msg.GetValue("action"));
            bool shift = ConvertToBool(msg.GetValue("shift"), false);
            bool ctrl = ConvertToBool(msg.GetValue("ctrl"), false);
            bool alt = ConvertToBool(msg.GetValue("alt"), false);
            bool win = ConvertToBool(msg.GetValue("win"), false);
            bool capsLock = ConvertToBool(GetFirstValue(msg, "capsLock", "caps_lock"), false);
            bool numLock = ConvertToBool(GetFirstValue(msg, "numLock", "num_lock"), false);
            int repeat = ConvertToInt(msg.GetValue("repeat"), 1);
            bool extended = ConvertToBool(msg.GetValue("extended"), false);
            string tsfStage = ConvertToString(msg.GetValue("tsf_stage"));

            object caretXRaw = msg.GetValue("caret_x");
            object caretYRaw = msg.GetValue("caret_y");
            bool isKeyDown = string.Equals(action, "down", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "key_down", StringComparison.OrdinalIgnoreCase);


            System.Diagnostics.Debug.WriteLine($"vk:{vk} {(isKeyDown ? "down":"up")} stage:{tsfStage}");
            bool hasKeyCaret = caretXRaw != null && caretYRaw != null;
            if (isKeyDown && _state.GetKeySoundEnabled())
            {
                _soundVk = vk;
                _soundVolumePercent = _state.GetKeySoundVolumePercent();
                Interlocked.Increment(ref _soundSeq);
            }

            if (isKeyDown && hasKeyCaret)
            {
                int caretX = ConvertToInt(caretXRaw, 0);
                int caretY = ConvertToInt(caretYRaw, 0);
                _state.GetCaret(out _, out _, out int previousWidth, out int previousHeight);
                int width = ConvertToInt(msg.GetValue("width"), previousWidth);
                int height = ConvertToInt(msg.GetValue("height"), previousHeight);
                _state.UpdateCaret(caretX, caretY, width, height);
           //     Console.WriteLine($"key: x:{caretX}, y:{caretY}");
            }

            KeyEngineResult result = _engine.ProcessKey(vk, scan, action, shift, ctrl, alt, win, capsLock, numLock, repeat, extended);
            _engine.PostProcessKey(vk, action, result, shift, ctrl, alt, win, capsLock);
            UpdateFreshCaretAwaitState(beforeState, result, isKeyDown, hasKeyCaret);
            if (result.OpenAddCiWindow)
            {
                _uiCommandCallback?.Invoke(CoreUiCommand.ShowAddCi);
            }
            _engine.GetCompositionDisplayParts(out string compositionPrefix, out string activeInputCode);
            string inputCode = BuildDisplayComposition(compositionPrefix, activeInputCode);
            bool cancelComposition = result.CancelComposition || _pendingFrontendCompositionReset;
            _pendingFrontendCompositionReset = false;
            bool languageStateChanged = beforeState != null && beforeState.IsChinese != result.IsChinese;
            string extraJsonPairs = BuildHookNativeConfigExtraJson(frontend);
            if (IsHookNativeFrontend(frontend) &&
                languageStateChanged &&
                _state.GetAutoSwitchSystemLanguageEnabled())
            {
                extraJsonPairs += ",\"ensure_system_layout_en\":true";
            }
            return BuildResponseWithUiState(
                seq,
                true,
                result.Handled,
                textToOutput: result.TextToOutput,
                inputBuffer: inputCode,
                keyboardOpen: result.IsChinese,
                cancelComposition: cancelComposition,
                extraJsonPairs: extraJsonPairs);
        }

        private void HandleCaretMessage(SimpleJsonObject msg)
        {
            int x = ConvertToInt(msg.GetValue("x"), 0);
            int y = ConvertToInt(msg.GetValue("y"), 0);
            int width = ConvertToInt(msg.GetValue("width"), 2);
            int height = ConvertToInt(msg.GetValue("height"), 20);
            ClearFreshCaretAwaitState();
            _state.UpdateCaret(x, y, width, height);
        //    Console.WriteLine($"caret: x:{x}, y:{y}");
        }

        private void HandleFocusMessage(SimpleJsonObject msg)
        {
            long hwnd = ConvertToLong(msg.GetValue("hwnd"), 0L);
            int processId = ConvertToInt(msg.GetValue("processId"), 0);
            string processName = ConvertToString(msg.GetValue("processName"));
            string className = ConvertToString(msg.GetValue("className"));
            string windowTitle = ConvertToString(msg.GetValue("windowTitle"));

            _state.GetFocus(out long previousHwnd, out int previousProcessId, out string previousProcessName, out string previousClassName, out string previousWindowTitle);
            _state.UpdateFocus(hwnd, processId, processName, className, windowTitle);

            bool focusChanged =
                previousHwnd != hwnd ||
                previousProcessId != processId ||
                !string.Equals(previousProcessName, processName, StringComparison.Ordinal) ||
                !string.Equals(previousClassName, className, StringComparison.Ordinal) ||
                !string.Equals(previousWindowTitle, windowTitle, StringComparison.Ordinal);

            if (focusChanged)
            {
                _engine.OnFocusChanged();
            }

            ClearFreshCaretAwaitState();
        }

        private string BuildHelloResponse(int seq)
        {
            string corePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            return "{" +
                   "\"type\":\"response\"," +
                   "\"seq\":" + seq + "," +
                   "\"success\":true," +
                   "\"handled\":false," +
                   "\"protocol_version\":1," +
                   "\"core_build\":\"next-dev\"," +
                   "\"core_commit\":\"next\"," +
                   "\"core_branch\":\"next\"," +
                   "\"ensure_system_layout_en\":" + (_state.GetAutoSwitchSystemLanguageEnabled() ? "true" : "false") + "," +
                   "\"native_hook_alt_backslash_toggle_enabled\":" + (_state.GetNativeHookAltBackslashToggleEnabled() ? "true" : "false") + "," +
                   "\"auto_switch_system_layout_enabled\":" + (_state.GetAutoSwitchSystemLanguageEnabled() ? "true" : "false") + "," +
                   "\"use_clipboard_commit\":" + (_state.GetUseClipboardCommit() ? "true" : "false") + "," +
                   "\"clipboard_commit_whitelist\":" + Quote(_state.GetClipboardCommitWhitelist()) + "," +
                   "\"core_path\":" + Quote(corePath) +
                   "}";
        }

        private string BuildHookNativeConfigExtraJson(string frontend)
        {
            if (!IsHookNativeFrontend(frontend))
            {
                return string.Empty;
            }

            return ",\"native_hook_alt_backslash_toggle_enabled\":" + (_state.GetNativeHookAltBackslashToggleEnabled() ? "true" : "false") +
                   ",\"auto_switch_system_layout_enabled\":" + (_state.GetAutoSwitchSystemLanguageEnabled() ? "true" : "false") +
                   ",\"use_clipboard_commit\":" + (_state.GetUseClipboardCommit() ? "true" : "false") +
                   ",\"clipboard_commit_whitelist\":" + Quote(_state.GetClipboardCommitWhitelist());
        }

        private void MarkFrontendMode(string frontend)
        {
            if (IsHookNativeFrontend(frontend))
            {
                _isNativeHookStatus = true;
            }
        }

        private static bool IsHookNativeFrontend(string frontend)
        {
            return string.Equals(frontend, "hook_native", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildResponseWithUiState(int seq, bool success, bool handled, string textToOutput = null, string inputBuffer = null, bool? keyboardOpen = null, bool cancelComposition = false, string extraJsonPairs = null)
        {
            string response = BuildResponse(seq, success, handled, textToOutput, inputBuffer, keyboardOpen, cancelComposition, extraJsonPairs);
            PublishUiState();
            return response;
        }

        private static string BuildResponse(int seq, bool success, bool handled, string textToOutput = null, string inputBuffer = null, bool? keyboardOpen = null, bool cancelComposition = false, string extraJsonPairs = null)
        {
            string json = "{" +
                          "\"type\":\"response\"," +
                          "\"seq\":" + seq + "," +
                          "\"success\":" + (success ? "true" : "false") + "," +
                          "\"handled\":" + (handled ? "true" : "false");

            if (textToOutput != null)
            {
                json += ",\"commit_text\":" + Quote(textToOutput);
            }
            if (inputBuffer != null)
            {
                json += ",\"input_buffer\":" + Quote(inputBuffer);
            }
            if (keyboardOpen.HasValue)
            {
                json += ",\"keyboard_open\":" + (keyboardOpen.Value ? "true" : "false");
            }
            if (cancelComposition)
            {
                json += ",\"cancel_composition\":true";
            }
            if (!string.IsNullOrEmpty(extraJsonPairs))
            {
                json += extraJsonPairs;
            }

            json += "}";
            return json;
        }

        private static bool IsLexiconConfigKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            string k = key.Trim();
            return string.Equals(k, "\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(k, "\u5f53\u524d\u7801\u8868", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnlimitedMixedInputConfigKey(string key)
        {
            return string.Equals(
                key?.Trim(),
                "\u4e2d\u82f1\u6587\u4e0d\u9650\u957f\u6df7\u5408\u8f93\u5165",
                StringComparison.OrdinalIgnoreCase); // unicode: 中英文不限长混合输入
        }

        private static bool IsSentenceInputConfigKey(string key)
        {
            return string.Equals(
                key?.Trim(),
                "\u6574\u53e5\u8f93\u5165",
                StringComparison.OrdinalIgnoreCase); // unicode: 整句输入
        }

        private static object GetFirstValue(SimpleJsonObject msg, params string[] keys)
        {
            if (msg == null || keys == null)
            {
                return null;
            }

            foreach (string key in keys)
            {
                object value = msg.GetValue(key);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private void PublishUiState()
        {
            if (_uiStatePublisher == null)
            {
                return;
            }

            try
            {
                lock (_publishLock)
                {
                    int pageSize = _state.GetPageSize();
                    EngineUiSnapshot engineState = _engine.GetUiSnapshot(pageSize);
                    _state.GetCaret(out int caretX, out int caretY, out _, out _);
                    bool hideStatusBar = _state.GetHideStatusBar() || (!_isNativeHookStatus && !_imeActive);

                    var state = new OverlayUiState
                    {
                        IsOff = _hookNativeDisabled,
                        IsNativeHook = _isNativeHookStatus,
                        IsChinese = engineState.IsChinese,
                        StatusText = _hookNativeDisabled ? "\u7981" : (engineState.IsChinese ? "\u4e2d" : "EN"),
                        CandidateVisible = ShouldShowCandidate(engineState),
                        InputCode = BuildDisplayComposition(engineState),
                        Candidates = engineState.Candidates ?? Array.Empty<string>(),
                        CandidateAnnotations = engineState.CandidateAnnotations ?? Array.Empty<string>(),
                        CompositionState = engineState.CompositionState,
                        CaretX = caretX,
                        CaretY = caretY,
                        VerticalCandidates = _state.GetVerticalCandidates(),
                        ShowCandidateIndex = _state.GetShowCandidateIndex(),
                        HideCandidateItems = _state.GetHideCandidateItems(),
                        ShowInputCodeInCandidateWindow = _state.GetShowInputCodeInCandidateWindow(),
                        CandidateExpandDelayMs = _state.GetCandidateExpandDelayMs(),
                        AnnotationExpandDelayMs = _state.GetAnnotationExpandDelayMs(),
                        // Native Hook owns its status visibility; TSF mode still follows ime_active.
                        HideStatusBar = hideStatusBar,
                        CodeMasking = _state.GetCodeMasking(),
                        ThemeName = _state.GetThemeName(),
                        FontName = _state.GetFontName(),
                        FontSize = _state.GetFontSize(),
                        SoundSeq = Interlocked.Read(ref _soundSeq),
                        SoundVk = _soundVk,
                        SoundVolumePercent = _soundVolumePercent
                    };

                    _uiStatePublisher.Publish(state);
                }
            }
            catch
            {
            }
        }

        private bool ShouldShowCandidate(EngineUiSnapshot engineState)
        {
            if (!engineState.IsComposing)
            {
                ClearFreshCaretAwaitState();
                return false;
            }

            if (!_awaitingFreshCaretForComposition)
            {
                return true;
            }

            if (GetNowMs() >= _awaitingFreshCaretDeadlineTick)
            {
                ClearFreshCaretAwaitState();
                return true;
            }

            return false;
        }

        private void UpdateFreshCaretAwaitState(EngineUiSnapshot beforeState, KeyEngineResult result, bool isKeyDown, bool hasKeyCaret)
        {
            if (!isKeyDown)
            {
                return;
            }

            if (!result.IsComposing)
            {
                ClearFreshCaretAwaitState();
                return;
            }

            if (hasKeyCaret)
            {
                ClearFreshCaretAwaitState();
                return;
            }

            if (!beforeState.IsComposing)
            {
                _awaitingFreshCaretForComposition = true;
                _awaitingFreshCaretDeadlineTick = GetNowMs() + FreshCaretAwaitWindowMs;
            }
        }

        private void ClearFreshCaretAwaitState()
        {
            _awaitingFreshCaretForComposition = false;
            _awaitingFreshCaretDeadlineTick = 0;
        }

        private static long GetNowMs()
        {
            return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        }

        private string MaskInputBufferForDisplay(string inputBuffer)
        {
            if (string.IsNullOrEmpty(inputBuffer))
            {
                return inputBuffer;
            }

            string mask = _state.GetCodeMasking();
            if (string.IsNullOrEmpty(mask))
            {
                return inputBuffer;
            }

            var lut = new StringInfo(mask);
            if (lut.LengthInTextElements <= 0)
            {
                return inputBuffer;
            }

            const string charLut = "abcdefghijklmnopqrstuvwxyz;";
            var sb = new StringBuilder(inputBuffer.Length);

            for (int i = 0; i < inputBuffer.Length; i++)
            {
                if (char.IsWhiteSpace(inputBuffer[i]))
                {
                    sb.Append(inputBuffer[i]);
                    continue;
                }

                int pos = charLut.IndexOf(char.ToLowerInvariant(inputBuffer[i]));
                if (pos < 0)
                {
                    pos = 0;
                }

                pos %= lut.LengthInTextElements;
                sb.Append(lut.SubstringByTextElements(pos, 1));
            }

            return sb.ToString();
        }

        private string BuildDisplayComposition(EngineUiSnapshot engineState)
        {
            if (engineState == null)
            {
                return string.Empty;
            }

            return BuildDisplayComposition(
                engineState.CompositionPrefix,
                engineState.ActiveInputCode ?? engineState.InputCode);
        }

        private string BuildDisplayComposition(string prefix, string activeCode)
        {
            return (prefix ?? string.Empty) + MaskInputBufferForDisplay(activeCode ?? string.Empty);
        }

        private static string Quote(string text)
        {
            return "\"" + SimpleJson.EscapeString(text ?? string.Empty) + "\"";
        }

        private static bool TryOpenTarget(string target)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryOpenAndSelectFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ConvertToString(object value)
        {
            return value == null ? string.Empty : value.ToString();
        }

        private static int ConvertToInt(object value, int fallback)
        {
            if (value == null)
            {
                return fallback;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                return (int)l;
            }

            if (int.TryParse(value.ToString(), out int parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static long ConvertToLong(object value, long fallback)
        {
            if (value == null)
            {
                return fallback;
            }

            if (value is long l)
            {
                return l;
            }

            if (value is int i)
            {
                return i;
            }

            if (long.TryParse(value.ToString(), out long parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static bool ConvertToBool(object value, bool fallback)
        {
            if (value == null)
            {
                return fallback;
            }

            if (value is bool b)
            {
                return b;
            }

            string text = value.ToString();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
            {
                return true;
            }
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
            {
                return false;
            }

            return fallback;
        }
    }
}


