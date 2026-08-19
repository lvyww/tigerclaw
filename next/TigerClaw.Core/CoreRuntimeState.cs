using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Globalization;

using System.IO;

using System.Linq;

using System.Text;

using Microsoft.Win32;



namespace TigerClaw.Core

{

    internal sealed class CoreRuntimeState

    {

        private const string DefaultCodeRoot = "\u7801\u8868"; // unicode: 鐮佽〃

        private const string KeyCodeRoot = "\u7801\u8868\u5b58\u50a8\u4f4d\u7f6e"; // unicode: 鐮佽〃瀛樺偍浣嶇疆

        private const string KeyCurrentMb = "\u5f53\u524d\u7801\u8868"; // unicode: 褰撳墠鐮佽〃

        private const string KeyAutoStart = "\u5f00\u673a\u81ea\u52a8\u542f\u52a8"; // unicode: 寮€鏈鸿嚜鍔ㄥ惎鍔?
        private const string KeyHideStatusBar = "\u9690\u85cf\u72b6\u6001\u680f"; // unicode: 闅愯棌鐘舵€佹爮

        private const string KeyTheme = "\u4e3b\u9898"; // unicode: 涓婚

        private const string KeyDefaultChinese = "\u9ed8\u8ba4\u4e2d\u6587"; // unicode: 榛樿涓枃

        private const string KeyCtrlEqualAddCi = "Ctrl+\u7b49\u53f7\u624b\u52a8\u52a0\u8bcd";
        private const string KeyCtrlMSwitchSchema = "Ctrl+m\u5207\u6362\u6700\u8fd1\u7801\u8868"; // Ctrl+m switch recent code table
        // Internal/persisted record of the two most-recently-used code tables (for Ctrl+m), stored
        // as "name|name". Persisted so the pair survives a restart; hidden from the settings dialog.
        private const string KeyRecentSchemas = "\u6700\u8fd1\u7801\u8868\u5bf9"; // unicode: \u6700\u8fd1\u7801\u8868\u5bf9

        private const string KeyMaxCodeLen = "\u6700\u5927\u7801\u957f"; // unicode: 鏈€澶х爜闀?
        private const string KeyUnlimitedMixedChineseEnglishInput = "\u4e2d\u82f1\u6587\u4e0d\u9650\u957f\u6df7\u5408\u8f93\u5165"; // unicode: 中英文不限长混合输入
        private const string KeySentenceInput = "\u6574\u53e5\u8f93\u5165"; // 整句输入
        private const string KeyAutoEnableSentenceBySchema = "\u81ea\u52a8\u542f\u7528\u6574\u53e5\u6a21\u5f0f"; // 自动启用整句模式
        private const string KeySentenceNeuralRerank = "\u6574\u53e5\u795e\u7ecf\u91cd\u6392"; // 整句神经重排
        private const string KeySentenceAutoCommit = "\u6574\u53e5\u81ea\u52a8\u63d0\u524d\u4e0a\u5c4f"; // 整句自动提前上屏
        private const string KeyCnUseEnPunc = "\u4e2d\u6587\u72b6\u6001\u4e0b\u4f7f\u7528\u82f1\u6587\u6807\u70b9"; // unicode: 涓枃鐘舵€佷笅浣跨敤鑻辨枃鏍囩偣

        private const string KeyVerticalCandidates = "\u7ad6\u6392\u5019\u9009"; // unicode: 绔栨帓鍊欓€?
        private const string KeyShowCandidateIndex = "\u663e\u793a\u5019\u9009\u5e8f\u53f7"; // unicode: 鏄剧ず鍊欓€夊簭鍙?
        private const string KeyBackQuery = "`\u952e\u62fc\u97f3\u53cd\u67e5"; // unicode: `閿嫾闊冲弽鏌?
        private const string KeyShowComment = "\u663e\u793a\u6ce8\u91ca"; // unicode: 鏄剧ず娉ㄩ噴

        private const string KeyShowSplit = "\u663e\u793a\u62c6\u5206"; // unicode: 鏄剧ず鎷嗗垎
        private const string KeyCandidateExpandDelayMs = "\u5ef6\u65f6\u663e\u793a\u5019\u9009(\u6beb\u79d2)"; // 延时显示候选(毫秒)
        private const string KeyAnnotationExpandDelayMs = "\u5ef6\u65f6\u5c55\u5f00\u6ce8\u91ca\u548c\u62c6\u5206(\u6beb\u79d2)"; // 延时展开注释和拆分(毫秒)

        private const string KeyPageSize = "\u6bcf\u9875\u5019\u9009\u4e2a\u6570"; // unicode: 姣忛〉鍊欓€変釜鏁?
        private const string KeyPageKeys = "\u7ffb\u9875\u952e"; // unicode: 缈婚〉閿?
        private const string KeySemicolon2 = "\u5206\u53f7\u6b21\u9009"; // unicode: 鍒嗗彿娆￠€?
        private const string KeyQuote3 = "\u5f15\u53f7\u4e09\u9009"; // unicode: 寮曞彿涓夐€?
        private const string KeySlashDunhao = "/\u8f93\u51fa\u987f\u53f7"; // unicode: /杈撳嚭椤垮彿

        private const string KeyHideCandidate = "\u9690\u85cf\u5019\u9009"; // unicode: 闅愯棌鍊欓€?
        private const string KeyShowInputCodeInCandidateWindow = "\u5019\u9009\u7a97\u663e\u793a\u7f16\u7801"; // unicode: ???????
        private const string KeyCodeMasking = "\u7f16\u7801\u4f2a\u88c5"; // unicode: 缂栫爜浼

        private const string KeyClearOnNoCode = "\u7a7a\u7801\u81ea\u52a8\u6e05\u5c4f"; // unicode: 绌虹爜鑷姩娓呭睆

        private const string KeyTabClear = "TAB\u6e05\u5c4f"; // unicode: TAB娓呭睆

        private const string KeyEnterClear = "\u56de\u8f66\u6e05\u5c4f"; // unicode: 鍥炶溅娓呭睆

        private const string KeyMaxAuto = "\u6700\u5927\u7801\u957f\u65e0\u91cd\u81ea\u52a8\u4e0a\u5c4f"; // unicode: 鏈€澶х爜闀挎棤閲嶈嚜鍔ㄤ笂灞?
        private const string KeyFont = "\u5b57\u4f53"; // unicode: 瀛椾綋

        private const string KeyFontSize = "\u5b57\u4f53\u5927\u5c0f"; // unicode: 瀛椾綋澶у皬
        private const string KeyUseClipboardCommit = "\u4f7f\u7528\u526a\u8d34\u677f\u4e0a\u5c4f"; // unicode: 使用剪贴板上屏
        private const string KeyClipboardCommitWhitelist = "\u4f7f\u7528\u526a\u8d34\u677f\u4e0a\u5c4f\u767d\u540d\u5355"; // unicode: 使用剪贴板上屏白名单

        private const string KeyKeySound = "\u5f00\u542f\u6253\u5b57\u97f3\u6548(\u5a31\u4e50)"; // unicode: 寮€鍚墦瀛楅煶鏁?濞变箰)

        private const string KeyKeySoundVolume = "\u6309\u952e\u97f3\u91cf0~100"; // unicode: 鎸夐敭闊抽噺0~100

        private const string KeyShiftToggle = "shift\u5207\u6362\u4e2d\u82f1\u6587"; // unicode: shift鍒囨崲涓嫳鏂?
        private const string KeyCtrlSpaceToggle = "Ctrl+\u7a7a\u683c\u5207\u6362\u4e2d\u82f1\u6587"; // unicode: Ctrl+绌烘牸鍒囨崲涓嫳鏂?
        private const string KeyNativeHookAltBackslashToggle = "Alt+\\\u542f\u7528\u6216\u7981\u7528\u5916\u6302\u7248"; // unicode: Alt+\启用或禁用外挂版
        private const string KeyAutoSwitchSystemLanguage = "\u81ea\u52a8\u5207\u6362\u7cfb\u7edf\u8bed\u8a00"; // unicode: 自动切换系统语言
        private const string DefaultThemeName = "\u9ed8\u8ba4"; // unicode: 榛樿

        private const string DefaultPageKeys = "- =";

        private const string DefaultFontName = "#\u971e\u9e5c\u6587\u6977 GB \u5c4f\u5e55\u9605\u8bfb\u7248"; // unicode: #闇為箿鏂囨シ GB 灞忓箷闃呰鐗?
        private const string ConstructCodeFileName = "\u6784\u8bcd.txt"; // unicode: 鏋勮瘝.txt
        private const string DisplayCommitSeparator = "\u001E";
        private const string DisplayCommitMarker = "=>";

        private static readonly string[] ConstructCiRemoveTokens = new[]

        {

            "\n", "\r", "\t", " ",

            "=", "\uFF0C", "-", "\u3002", "\u00B7", "\u3010", "\u3001", "\u3011", "\uFF1B", // unicode: 锛?; 銆?; 路 ; 銆?; 銆?; 銆?; 锛?
            "\uFF09", "\uFF01", "@", "#", "\uFFE5", "%", "\u2026\u2026", "&", "*", "\uFF08", "+", "\u300A", "\u2014\u2014", "\u300B", "~", "{", "|", "}", "\uFF1F", "\uFF1A", // unicode: 锛?; 锛?; 锟?; 鈥︹€?; 锛?; 銆?; 鈥斺€?; 銆?; 锛?; 锛?
            ",", ".", "`", "[", "\\", "]", "/", ";", "'",

            ")", "!", "$", "^", "<", "_", ">", "?", "\""

        };

        private static readonly string Yes = "\u662f"; // unicode: 鏄?
        private static readonly string No = "\u5426"; // unicode: 鍚?
        private static readonly KeyValuePair<string, string>[] DefaultConfigPairs = new[]

        {

            new KeyValuePair<string, string>(KeyAutoStart, Yes),

            new KeyValuePair<string, string>(KeyHideStatusBar, No),

            new KeyValuePair<string, string>(KeyCodeRoot, DefaultCodeRoot),

            new KeyValuePair<string, string>(KeyCurrentMb, string.Empty),

            new KeyValuePair<string, string>(KeyTheme, DefaultThemeName),

            new KeyValuePair<string, string>(KeyDefaultChinese, Yes),

            new KeyValuePair<string, string>(KeyCnUseEnPunc, No),

            new KeyValuePair<string, string>(KeyShiftToggle, Yes),

            new KeyValuePair<string, string>(KeyCtrlSpaceToggle, Yes),

            new KeyValuePair<string, string>(KeyNativeHookAltBackslashToggle, Yes),

            new KeyValuePair<string, string>(KeyAutoSwitchSystemLanguage, Yes),

            new KeyValuePair<string, string>(KeyCtrlEqualAddCi, Yes),

            new KeyValuePair<string, string>(KeyCtrlMSwitchSchema, No),

            new KeyValuePair<string, string>(KeyRecentSchemas, string.Empty),

            new KeyValuePair<string, string>(KeyEnterClear, No),

            new KeyValuePair<string, string>(KeyTabClear, Yes),

            new KeyValuePair<string, string>(KeyVerticalCandidates, Yes),

            new KeyValuePair<string, string>(KeyShowCandidateIndex, Yes),

            new KeyValuePair<string, string>(KeyBackQuery, Yes),

            new KeyValuePair<string, string>(KeyShowComment, Yes),

            new KeyValuePair<string, string>(KeyShowSplit, No),

            new KeyValuePair<string, string>(KeyCandidateExpandDelayMs, string.Empty),

            new KeyValuePair<string, string>(KeyAnnotationExpandDelayMs, string.Empty),

            new KeyValuePair<string, string>(KeyPageSize, "5"),

            new KeyValuePair<string, string>(KeyPageKeys, DefaultPageKeys),

            new KeyValuePair<string, string>(KeySemicolon2, Yes),

            new KeyValuePair<string, string>(KeyQuote3, Yes),

            new KeyValuePair<string, string>(KeySlashDunhao, Yes),

            new KeyValuePair<string, string>(KeyHideCandidate, No),

            new KeyValuePair<string, string>(KeyShowInputCodeInCandidateWindow, No),

            new KeyValuePair<string, string>(KeyCodeMasking, string.Empty),

            new KeyValuePair<string, string>(KeyClearOnNoCode, Yes),

            new KeyValuePair<string, string>(KeyMaxCodeLen, "4"),

            new KeyValuePair<string, string>(KeyUnlimitedMixedChineseEnglishInput, No),

            new KeyValuePair<string, string>(KeySentenceInput, No),

            new KeyValuePair<string, string>(KeyAutoEnableSentenceBySchema, Yes),

            new KeyValuePair<string, string>(KeySentenceNeuralRerank, Yes),

            new KeyValuePair<string, string>(KeySentenceAutoCommit, No),

            new KeyValuePair<string, string>(KeyMaxAuto, Yes),

            new KeyValuePair<string, string>(KeyFont, DefaultFontName),

            new KeyValuePair<string, string>(KeyFontSize, "17"),

            new KeyValuePair<string, string>(KeyUseClipboardCommit, No),

            new KeyValuePair<string, string>(KeyClipboardCommitWhitelist, "Pure Writer.exe,Notepad.exe"),

            new KeyValuePair<string, string>(KeyKeySound, No),

            new KeyValuePair<string, string>(KeyKeySoundVolume, "30")

        };

        private static readonly HashSet<string> KnownConfigKeys = new HashSet<string>(
            DefaultConfigPairs.Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);



        private readonly object _lock = new object();

        // Recent code tables (MRU, most-recent first, max 2): drives Ctrl+m switch-recent-schema.
        private readonly List<string> _recentSchemas = new List<string>();

        private readonly Dictionary<string, string> _config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, List<string>> _lexicon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, List<string>> _pinyinLexicon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _commentMap = new Dictionary<string, string>(StringComparer.Ordinal);

        private Dictionary<string, string> _splitMap = new Dictionary<string, string>(StringComparer.Ordinal);

        private Dictionary<string, string> _fullCodeMap = new Dictionary<string, string>(StringComparer.Ordinal);

        private Dictionary<string, string> _constructCodeMap = new Dictionary<string, string>(StringComparer.Ordinal);

        private HashSet<string> _unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private HashSet<string> _nonTerminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _shortSymbolSemicolon;

        private bool _shortSymbolSlash;

        private bool _shortSymbolLBracket;

        private bool _shortSymbolZ;

        private HashSet<string> _autoShortSymbol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ctrl+m snapshot cache: lazily holds the most-recently-left code table's loaded lexicon so
        // switching back is instant (no disk reload). Capacity 1; only the Ctrl+m path fills/uses it,
        // and any plain ReloadLexicon clears it. Key = code-table directory name.
        private readonly Dictionary<string, LexiconSnapshot> _lexiconCache =
            new Dictionary<string, LexiconSnapshot>(StringComparer.OrdinalIgnoreCase);

        // A fully-loaded code table: the bundle ReloadLexicon assigns to the live fields all at once.
        private sealed class LexiconSnapshot
        {
            public Dictionary<string, List<string>> Lexicon;
            public Dictionary<string, List<string>> PinyinLexicon;
            public Dictionary<string, string> CommentMap;
            public Dictionary<string, string> SplitMap;
            public Dictionary<string, string> FullCodeMap;
            public Dictionary<string, string> ConstructCodeMap;
            public HashSet<string> Unique;
            public HashSet<string> NonTerminal;
            public bool ShortSymbolSemicolon;
            public bool ShortSymbolSlash;
            public bool ShortSymbolLBracket;
            public bool ShortSymbolZ;
            public HashSet<string> AutoShortSymbol;
        }



        private readonly string _exeDir;

        private readonly string _baseDir;

        private readonly string _configPath;

        private readonly string _customPath;

        private readonly bool _coreSendHistoryLogEnabled;



        private int _caretX;

        private int _caretY;

        private int _caretWidth = 2;

        private int _caretHeight = 20;

        private long _focusHwnd;

        private int _focusProcessId;

        private string _focusProcessName = string.Empty;

        private string _focusClassName = string.Empty;

        private string _focusWindowTitle = string.Empty;



        public int ConfigVersion { get; private set; }

        public int LexiconVersion { get; private set; }



        public CoreRuntimeState()

        {

            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

            _exeDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

            _baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);

            _configPath = Path.Combine(_exeDir, "config.txt");

            _customPath = Path.Combine(_exeDir, "custom_words.txt");

            _coreSendHistoryLogEnabled = ReadEnvFlag("BIME_CORE_SENDHISTORY_LOG");

        }



        public void Initialize()

        {

            EnsureConfigFile();

            ReloadConfig();

            ReloadLexicon();

        }



        public bool ReloadConfig()

        {

            try

            {

                EnsureConfigFile();

                Dictionary<string, string> merged = CreateDefaultConfig();

                Encoding enc = DetectTextEncoding(_configPath);

                foreach (string raw in File.ReadAllLines(_configPath, enc))

                {

                    if (string.IsNullOrWhiteSpace(raw)) { continue; }

                    string line = raw.Trim();

                    if (line.StartsWith("#", StringComparison.Ordinal)) { continue; }

                    int pos = FindSep(line);

                    if (pos <= 0 || pos >= line.Length - 1) { continue; }

                    string key = line.Substring(0, pos).Trim();

                    string value = line.Substring(pos + 1).Trim();

                    if (key.Length == 0) { continue; }
                    if (!KnownConfigKeys.Contains(key)) { continue; }

                    merged[key] = value;

                }

                merged[KeyCodeRoot] = NormalizePathSetting(merged[KeyCodeRoot]);

                lock (_lock)

                {

                    _config.Clear();

                    foreach (var kv in merged) { _config[kv.Key] = kv.Value; }

                    SeedRecentSchemasFromConfigNoLock();

                    ConfigVersion++;

                }

                WriteConfigNoThrow();
                SyncAutoStartNoThrow();

                return true;

            }

            catch { return false; }

        }



        public bool ReloadLexicon()

        {

            try

            {

                string root = ResolveCodeRoot();

                string mbDir = ResolveCurrentMbDir(root);

                if (!string.IsNullOrEmpty(mbDir))
                {
                    lock (_lock) { RecordRecentSchemaNoLock(Path.GetFileName(mbDir)); }
                    // Persist the updated recent-table pair so Ctrl+m remembers it across restarts.
                    WriteConfigNoThrow();
                }

                // A full disk reload of the current table invalidates the Ctrl+m snapshot cache.
                lock (_lock) { _lexiconCache.Clear(); }

                ApplyLexiconSnapshot(BuildLexiconSnapshot(mbDir));

                return true;

            }

            catch { return false; }

        }

        // Build a fully-loaded lexicon snapshot from disk for the given code-table directory.
        // Pure heavy work: reads only `mbDir`; touches no live fields and takes no lock.
        private LexiconSnapshot BuildLexiconSnapshot(string mbDir)
        {

                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                var codedRows = new List<(string code, string text, int freq)>(4096);

                var noCodeRows = new List<(string text, int freq)>(1024);

                string constructPath = string.IsNullOrEmpty(mbDir) ? null : Path.Combine(mbDir, ConstructCodeFileName);

                bool hasConstructFile = !string.IsNullOrEmpty(constructPath) && File.Exists(constructPath);

                if (!string.IsNullOrEmpty(mbDir) && Directory.Exists(mbDir))

                {

                    LoadMbDir(mbDir, codedRows, noCodeRows, hasConstructFile);

                }



                foreach ((string code, string text, int freq) row in codedRows.OrderByDescending(x => x.freq))

                {

                    string c = NormalizeCode(row.code);

                    if (c.Length == 0)

                    {

                        continue;

                    }



                    if (!map.TryGetValue(c, out List<string> list))

                    {

                        list = new List<string>();

                        map[c] = list;

                    }



                    if (!list.Contains(row.text))

                    {

                        list.Add(row.text);

                    }

                }



                Dictionary<string, string> constructCodeMap = BuildConstructCodeMap(constructPath, hasConstructFile, map);



                if (hasConstructFile)

                {

                    ParseMbFile(constructPath, new List<(string code, string text, int freq)>(), noCodeRows);

                }



                AppendInferredNoCodeRows(noCodeRows, constructCodeMap, codedRows);



                map.Clear();

                foreach ((string code, string text, int freq) row in codedRows.OrderByDescending(x => x.freq))

                {

                    string c = NormalizeCode(row.code);

                    if (c.Length == 0)

                    {

                        continue;

                    }



                    if (!map.TryGetValue(c, out List<string> list))

                    {

                        list = new List<string>();

                        map[c] = list;

                    }



                    if (!list.Contains(row.text))

                    {

                        list.Add(row.text);

                    }

                }



                Dictionary<string, string> constructCodeMapFinal = BuildConstructCodeMap(constructPath, hasConstructFile, map);

                if (!string.IsNullOrEmpty(mbDir) && Directory.Exists(mbDir))

                {

                    LoadAdjust(Path.Combine(mbDir, "\u7528\u6237\u8c03\u6574.txt"), map); // unicode: 鐢ㄦ埛璋冩暣.txt

                }

                Dictionary<string, List<string>> pyMap = LoadPinyinLexicon();

                Dictionary<string, string> commentMap = LoadCommentMap(mbDir);

                Dictionary<string, string> splitMap = LoadSplitMap(mbDir);

                Dictionary<string, string> fullCodeMap = BuildFullCodeMap(map);

                RebuildMeta(map, out HashSet<string> unique, out HashSet<string> nonTerm);

                RebuildShortSymbolMeta(map,

                                       out bool shortSemi,

                                       out bool shortSlash,

                                       out bool shortLBracket,

                                       out bool shortZ,

                                       out HashSet<string> autoShort);

                return new LexiconSnapshot
                {
                    Lexicon = map,
                    PinyinLexicon = pyMap,
                    CommentMap = commentMap,
                    SplitMap = splitMap,
                    FullCodeMap = fullCodeMap,
                    ConstructCodeMap = constructCodeMapFinal,
                    Unique = unique,
                    NonTerminal = nonTerm,
                    ShortSymbolSemicolon = shortSemi,
                    ShortSymbolSlash = shortSlash,
                    ShortSymbolLBracket = shortLBracket,
                    ShortSymbolZ = shortZ,
                    AutoShortSymbol = autoShort,
                };

        }

        // Swap a loaded snapshot into the live fields under lock and bump LexiconVersion so the
        // existing UI pipeline (PublishUiState -> GetUiSnapshot) re-resolves candidates.
        private void ApplyLexiconSnapshot(LexiconSnapshot s)
        {
            lock (_lock)
            {
                _lexicon = s.Lexicon;
                _pinyinLexicon = s.PinyinLexicon;
                _commentMap = s.CommentMap;
                _splitMap = s.SplitMap;
                _fullCodeMap = s.FullCodeMap;
                _constructCodeMap = s.ConstructCodeMap;
                _unique = s.Unique;
                _nonTerminal = s.NonTerminal;
                _shortSymbolSemicolon = s.ShortSymbolSemicolon;
                _shortSymbolSlash = s.ShortSymbolSlash;
                _shortSymbolLBracket = s.ShortSymbolLBracket;
                _shortSymbolZ = s.ShortSymbolZ;
                _autoShortSymbol = s.AutoShortSymbol;
                LexiconVersion++;
            }
        }

        // Reference-copy the current live fields into a snapshot so the table being switched away
        // from can be cached for an instant Ctrl+m switch back.
        private LexiconSnapshot CaptureLiveSnapshot()
        {
            lock (_lock)
            {
                return new LexiconSnapshot
                {
                    Lexicon = _lexicon,
                    PinyinLexicon = _pinyinLexicon,
                    CommentMap = _commentMap,
                    SplitMap = _splitMap,
                    FullCodeMap = _fullCodeMap,
                    ConstructCodeMap = _constructCodeMap,
                    Unique = _unique,
                    NonTerminal = _nonTerminal,
                    ShortSymbolSemicolon = _shortSymbolSemicolon,
                    ShortSymbolSlash = _shortSymbolSlash,
                    ShortSymbolLBracket = _shortSymbolLBracket,
                    ShortSymbolZ = _shortSymbolZ,
                    AutoShortSymbol = _autoShortSymbol,
                };
            }
        }



        public string GetCustomSelectionKeyConfigPath()

        {

            return Path.Combine(_exeDir, "\u81ea\u5b9a\u4e49\u9009\u91cd\u952e.txt"); // unicode: 鑷畾涔夐€夐噸閿?txt

        }



        public List<string> GetCandidates(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return null; }

            lock (_lock) { return _lexicon.TryGetValue(c, out List<string> v) ? new List<string>(v) : null; }

        }

        public Dictionary<string, List<string>> GetSentenceLexiconSnapshot()
        {
            lock (_lock)
            {
                var snapshot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<string>> pair in _lexicon)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.Count == 0)
                    {
                        continue;
                    }

                    var values = new List<string>(pair.Value.Count);
                    foreach (string entry in pair.Value)
                    {
                        string text = GetCandidateCommitText(entry);
                        if (!string.IsNullOrEmpty(text) && !values.Contains(text))
                        {
                            values.Add(text);
                        }
                    }

                    if (values.Count > 0)
                    {
                        snapshot[pair.Key] = values;
                    }
                }

                return snapshot;
            }
        }

        public string GetCandidateDisplayText(string entry)

        {

            UnpackDisplayCommitEntry(entry, out string displayText, out _);

            return displayText;

        }

        public string GetCandidateCommitText(string entry)

        {

            UnpackDisplayCommitEntry(entry, out _, out string commitText);

            return commitText;

        }

        public bool IsDisplayCommitSeparatedCandidate(string entry)

        {

            return IsPackedDisplayCommitEntry(entry);

        }



        public List<string> GetPinyinCandidates(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return null; }

            lock (_lock) { return _pinyinLexicon.TryGetValue(c, out List<string> v) ? new List<string>(v) : null; }

        }



        public bool HasPinyinLexicon()

        {

            lock (_lock) { return _pinyinLexicon.Count > 0; }

        }



        public bool HasCode(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return false; }

            lock (_lock) { return _lexicon.TryGetValue(c, out List<string> v) && v != null && v.Count > 0; }

        }



        public bool IsUniqueTerminalCode(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return false; }

            lock (_lock) { return _unique.Contains(c); }

        }



        public bool IsNonTerminalCode(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return false; }

            lock (_lock) { return _nonTerminal.Contains(c); }

        }



        public bool GetBackQueryEnabled() => GetBool(KeyBackQuery, true);



        public bool GetCtrlEqualAddCiEnabled() => GetBool(KeyCtrlEqualAddCi, true);

        public bool GetCtrlMSwitchSchemaEnabled() => GetBool(KeyCtrlMSwitchSchema, false);



        public string GetPageKeys()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyPageKeys, out string value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : DefaultPageKeys;

            }

        }



        public bool IsShortSymbolSemicolonEnabled()

        {

            lock (_lock) { return _shortSymbolSemicolon; }

        }



        public bool IsShortSymbolSlashEnabled()

        {

            lock (_lock) { return _shortSymbolSlash; }

        }



        public bool IsShortSymbolLBracketEnabled()

        {

            lock (_lock) { return _shortSymbolLBracket; }

        }



        public bool IsShortSymbolZEnabled()

        {

            lock (_lock) { return _shortSymbolZ; }

        }



        public bool IsAutoShortSymbol(string code)

        {

            string c = NormalizeCode(code);

            if (c.Length == 0) { return false; }

            lock (_lock) { return _autoShortSymbol.Contains(c); }

        }



        public bool TryAddCi(string code, string text, out string reason)

        {

            reason = string.Empty;

            string c = NormalizeCode(code);

            string t = ParseLexiconEntryToken(text);

            if (c.Length == 0) { reason = "code is empty"; return false; }

            if (t.Length == 0) { reason = "text is empty"; return false; }

            try

            {

                lock (_lock)

                {

                    if (!_lexicon.TryGetValue(c, out List<string> list))

                    {

                        list = new List<string>();

                        _lexicon[c] = list;

                    }

                    list.RemoveAll(x => CandidateIdentityEquals(x, t));

                    list.Add(t);

                    RebuildMeta(_lexicon, out _unique, out _nonTerminal);

                    RebuildShortSymbolMeta(_lexicon,

                                           out _shortSymbolSemicolon,

                                           out _shortSymbolSlash,

                                           out _shortSymbolLBracket,

                                           out _shortSymbolZ,

                                           out _autoShortSymbol);

                    LexiconVersion++;

                }

                AppendAdjustOperationNoThrow("{\u6dfb\u52a0}", c, t); // unicode: {娣诲姞}

                return true;

            }

            catch (Exception ex) { reason = ex.Message; return false; }

        }



        public bool TryUserDelete(string code, string text, out string reason)

        {

            reason = string.Empty;

            string c = NormalizeCode(code);

            string t = GetCommitText(text);

            if (c.Length == 0) { reason = "code is empty"; return false; }

            if (t.Length == 0) { reason = "text is empty"; return false; }

            try

            {

                bool changed;

                lock (_lock)

                {

                    if (!_lexicon.TryGetValue(c, out List<string> list) || list == null || list.Count == 0)

                    {

                        return false;

                    }



                    int before = list.Count;

                    list.RemoveAll(x => CandidateIdentityEquals(x, t));

                    changed = list.Count != before;

                    if (changed)

                    {

                        RebuildMeta(_lexicon, out _unique, out _nonTerminal);

                        RebuildShortSymbolMeta(_lexicon,

                                               out _shortSymbolSemicolon,

                                               out _shortSymbolSlash,

                                               out _shortSymbolLBracket,

                                               out _shortSymbolZ,

                                               out _autoShortSymbol);

                        LexiconVersion++;

                    }

                }



                if (changed)

                {

                    AppendAdjustOperationNoThrow("{\u5220\u9664}", c, t); // unicode: {鍒犻櫎}

                }

                return changed;

            }

            catch (Exception ex) { reason = ex.Message; return false; }

        }



        public bool TryUserTop(string code, string text, out string reason)

        {

            reason = string.Empty;

            string c = NormalizeCode(code);

            string t = GetCommitText(text);

            if (c.Length == 0) { reason = "code is empty"; return false; }

            if (t.Length == 0) { reason = "text is empty"; return false; }

            try

            {

                bool changed = false;

                lock (_lock)

                {

                    if (!_lexicon.TryGetValue(c, out List<string> list))

                    {

                        list = new List<string>();

                        _lexicon[c] = list;

                    }



                    int idx = list.FindIndex(x => CandidateIdentityEquals(x, t));

                    if (idx != 0)

                    {

                        string stored = idx >= 0 ? list[idx] : t;

                        if (idx > 0)

                        {

                            list.RemoveAt(idx);

                        }



                        list.Insert(0, stored);

                        changed = true;

                        RebuildMeta(_lexicon, out _unique, out _nonTerminal);

                        RebuildShortSymbolMeta(_lexicon,

                                               out _shortSymbolSemicolon,

                                               out _shortSymbolSlash,

                                               out _shortSymbolLBracket,

                                               out _shortSymbolZ,

                                               out _autoShortSymbol);

                        LexiconVersion++;

                    }

                }



                if (changed)

                {

                    AppendAdjustOperationNoThrow("{\u7f6e\u9876}", c, t); // unicode: {缃《}

                }

                return changed;

            }

            catch (Exception ex) { reason = ex.Message; return false; }

        }



        public bool TryUserAdvance(string code, string text, out string reason)

        {

            reason = string.Empty;

            string c = NormalizeCode(code);

            string t = GetCommitText(text);

            if (c.Length == 0) { reason = "code is empty"; return false; }

            if (t.Length == 0) { reason = "text is empty"; return false; }

            try

            {

                bool changed = false;

                lock (_lock)

                {

                    if (_lexicon.TryGetValue(c, out List<string> list) && list != null)

                    {

                        int idx = list.FindIndex(x => CandidateIdentityEquals(x, t));

                        if (idx > 0)

                        {

                            string stored = list[idx];

                            list.RemoveAt(idx);

                            list.Insert(idx - 1, stored);

                            changed = true;

                            RebuildMeta(_lexicon, out _unique, out _nonTerminal);

                            RebuildShortSymbolMeta(_lexicon,

                                                   out _shortSymbolSemicolon,

                                                   out _shortSymbolSlash,

                                                   out _shortSymbolLBracket,

                                                   out _shortSymbolZ,

                                                   out _autoShortSymbol);

                            LexiconVersion++;

                        }

                    }

                }



                if (changed)

                {

                    AppendAdjustOperationNoThrow("{\u524d\u79fb}", c, t); // unicode: {鍓嶇Щ}

                }

                return changed;

            }

            catch (Exception ex) { reason = ex.Message; return false; }

        }



        public int GetMaxCodeLength()

        {

            lock (_lock)

            {

                if (_config.TryGetValue(KeyMaxCodeLen, out string raw) && int.TryParse(raw, out int n))

                {

                    if (n < 1) { return 1; }

                    if (n > 16) { return 16; }

                    return n;

                }

            }

            return 4;

        }



        public bool GetDefaultChinese() => GetBool(KeyDefaultChinese, true);

        public bool GetUseEnPuncInCn() => GetBool(KeyCnUseEnPunc, false);

        public bool GetSecondCandidateSemicolon() => GetBool(KeySemicolon2, true);

        public bool GetThirdCandidateQuote() => GetBool(KeyQuote3, true);

        public bool GetSlashOutputsDunhao() => GetBool(KeySlashDunhao, true);

        public bool GetClearOnNoCode() => GetBool(KeyClearOnNoCode, true);

        public bool GetTabClear() => GetBool(KeyTabClear, true);

        public bool GetEnterClear() => GetBool(KeyEnterClear, false);

        public bool GetMaxCodeAutoCommit() => GetBool(KeyMaxAuto, true);

        public bool GetUnlimitedMixedChineseEnglishInput() => GetBool(KeyUnlimitedMixedChineseEnglishInput, false);

        public bool GetSentenceInputEnabled() => GetBool(KeySentenceInput, false);

        public bool GetAutoEnableSentenceBySchema() => GetBool(KeyAutoEnableSentenceBySchema, true);

        public bool IsSentenceInputActive()
        {
            if (GetSentenceInputEnabled())
            {
                return true;
            }

            if (!GetAutoEnableSentenceBySchema())
            {
                return false;
            }

            string schema = GetCurrentSchema();
            return !string.IsNullOrEmpty(schema) &&
                   schema.IndexOf("\u6574\u53e5", StringComparison.Ordinal) >= 0; // 整句
        }

        public bool GetSentenceNeuralRerankEnabled() => GetBool(KeySentenceNeuralRerank, true);

        public bool GetSentenceAutoCommitEnabled() => GetBool(KeySentenceAutoCommit, false);

        public bool GetShiftToggleEnabled() => GetBool(KeyShiftToggle, true);

        public bool GetCtrlSpaceToggleEnabled() => GetBool(KeyCtrlSpaceToggle, true);

        public bool GetNativeHookAltBackslashToggleEnabled() => GetBool(KeyNativeHookAltBackslashToggle, true);

        public bool GetAutoSwitchSystemLanguageEnabled() => GetBool(KeyAutoSwitchSystemLanguage, true);

        public bool GetVerticalCandidates() => GetBool(KeyVerticalCandidates, true);

        public bool GetShowCandidateIndex() => GetBool(KeyShowCandidateIndex, true);

        public bool GetHideCandidateItems() => GetBool(KeyHideCandidate, false);

        public bool GetShowInputCodeInCandidateWindow() => GetBool(KeyShowInputCodeInCandidateWindow, false);
        public bool GetHideStatusBar() => GetBool(KeyHideStatusBar, false);

        public bool GetShowComment() => GetBool(KeyShowComment, true);

        public bool GetShowSplit() => GetBool(KeyShowSplit, false);

        public int GetCandidateExpandDelayMs()
        {
            lock (_lock)
            {
                if (_config.TryGetValue(KeyCandidateExpandDelayMs, out string raw) &&
                    int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    if (value < 0) { return 0; }
                    if (value > 60000) { return 60000; }
                    return value;
                }
            }

            return 0;
        }

        public int GetAnnotationExpandDelayMs()
        {
            lock (_lock)
            {
                if (_config.TryGetValue(KeyAnnotationExpandDelayMs, out string raw) &&
                    int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    if (value < 0) { return 0; }
                    if (value > 60000) { return 60000; }
                    return value;
                }
            }

            return 0;
        }

        public bool GetKeySoundEnabled() => GetBool(KeyKeySound, false);

        public bool GetUseClipboardCommit() => GetBool(KeyUseClipboardCommit, false);

        public string GetClipboardCommitWhitelist()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyClipboardCommitWhitelist, out string raw)

                    ? (raw ?? string.Empty).Trim()

                    : "Pure Writer.exe,Notepad.exe";

            }

        }

        public bool GetCoreSendHistoryLogEnabled() => _coreSendHistoryLogEnabled;



        public int GetKeySoundVolumePercent()

        {

            lock (_lock)

            {

                if (_config.TryGetValue(KeyKeySoundVolume, out string raw) && int.TryParse(raw, out int value))

                {

                    if (value < 0) { return 0; }

                    if (value > 100) { return 100; }

                    return value;

                }

            }



            return 30;

        }



        public bool TryToggleHideCandidateItems(out bool hidden)

        {

            lock (_lock)

            {

                bool current = _config.TryGetValue(KeyHideCandidate, out string raw) ? ParseBool(raw, false) : false;

                hidden = !current;

                _config[KeyHideCandidate] = hidden ? Yes : No;

                ConfigVersion++;

            }



            WriteConfigNoThrow();
            SyncAutoStartNoThrow();

            return true;

        }



        public string GetCandidateAnnotation(string word, bool pinyinMode)

        {

            if (string.IsNullOrEmpty(word))

            {

                return string.Empty;

            }



            lock (_lock)

            {

                if (IsPackedDisplayCommitEntry(word))

                {

                    return string.Empty;

                }

                var splitList = new List<string>();

                var codeList = new List<string>();

                string comment = string.Empty;



                if (pinyinMode)

                {

                    if (TryCollectByTextElements(word, _splitMap, splitList) &&

                        TryCollectByTextElements(word, _fullCodeMap, codeList))

                    {

                        return string.Join("\u00B7", splitList) + " | " + string.Join("\u00B7", codeList); // unicode: 路

                    }



                    if (splitList.Count > 0)

                    {

                        return string.Join("\u00B7", splitList); // unicode: 路

                    }



                    if (codeList.Count > 0)

                    {

                        return string.Join("\u00B7", codeList); // unicode: 路

                    }



                    return string.Empty;

                }



                if (GetShowSplit() && _splitMap.Count > 0)

                {

                    _ = TryCollectByTextElements(word, _splitMap, splitList);

                }



                if (GetShowComment() && _commentMap.TryGetValue(word, out string c))

                {

                    comment = c ?? string.Empty;

                }



                if (splitList.Count > 0)

                {

                    string text = string.Join("\u00B7", splitList); // unicode: 路

                    if (!string.IsNullOrEmpty(comment))

                    {

                        text += " " + comment;

                    }



                    return text;

                }



                if (!string.IsNullOrEmpty(comment))

                {

                    return comment;

                }



                return string.Empty;

            }

        }



        public int GetPageSize()

        {

            lock (_lock)

            {

                if (_config.TryGetValue(KeyPageSize, out string raw) && int.TryParse(raw, out int n))

                {

                    if (n < 1) { return 1; }

                    if (n > 10) { return 10; }

                    return n;

                }

            }



            return 5;

        }



        public string GetCodeMasking()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyCodeMasking, out string raw) ? raw ?? string.Empty : string.Empty;

            }

        }



        public string GetThemeName()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyTheme, out string raw) && !string.IsNullOrWhiteSpace(raw)

                    ? raw.Trim()

                    : DefaultThemeName;

            }

        }



        public string GetFontName()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyFont, out string raw) && !string.IsNullOrWhiteSpace(raw)

                    ? raw.Trim()

                    : DefaultFontName;

            }

        }



        public double GetFontSize()

        {

            lock (_lock)

            {

                if (_config.TryGetValue(KeyFontSize, out string raw) &&

                    double.TryParse(raw, out double value))

                {

                    if (value < 3) { return 3; }

                    if (value > 200) { return 200; }

                    return value;

                }

            }



            return 17;

        }



        public void UpdateCaret(int x, int y, int width, int height)

        {

            lock (_lock)

            {

                _caretX = x;

                _caretY = y;

                _caretWidth = width;

                _caretHeight = height;

            }

        }



        public void UpdateFocus(long hwnd, int processId, string processName, string className, string windowTitle)

        {

            lock (_lock)

            {

                _focusHwnd = hwnd;

                _focusProcessId = processId;

                _focusProcessName = processName ?? string.Empty;

                _focusClassName = className ?? string.Empty;

                _focusWindowTitle = windowTitle ?? string.Empty;

            }

        }



        public void GetCaret(out int x, out int y, out int width, out int height)

        {

            lock (_lock)

            {

                x = _caretX;

                y = _caretY;

                width = _caretWidth;

                height = _caretHeight;

            }

        }



        public void GetFocus(out long hwnd, out int processId, out string processName, out string className, out string windowTitle)

        {

            lock (_lock)

            {

                hwnd = _focusHwnd;

                processId = _focusProcessId;

                processName = _focusProcessName;

                className = _focusClassName;

                windowTitle = _focusWindowTitle;

            }

        }



        public bool GetBool(string key, bool def)

        {

            lock (_lock) { return _config.TryGetValue(key, out string raw) ? ParseBool(raw, def) : def; }

        }



        public string[] GetSchemaList()

        {

            try

            {

                string root = ResolveCodeRoot();

                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))

                {

                    return Array.Empty<string>();

                }



                return Directory.GetDirectories(root)

                    .Select(Path.GetFileName)

                    .Where(name => !string.IsNullOrWhiteSpace(name))

                    .Distinct(StringComparer.OrdinalIgnoreCase)

                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)

                    .ToArray();

            }

            catch

            {

                return Array.Empty<string>();

            }

        }



        public string GetCurrentSchema()

        {

            lock (_lock)

            {

                return _config.TryGetValue(KeyCurrentMb, out string raw) ? (raw ?? string.Empty).Trim() : string.Empty;

            }

        }

        // Move a code-table name to the front of the recent-MRU list (case-insensitive), capped at 2.
        // Caller must hold _lock.
        private void RecordRecentSchemaNoLock(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            name = name.Trim();
            _recentSchemas.RemoveAll(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            _recentSchemas.Insert(0, name);
            while (_recentSchemas.Count > 2)
            {
                _recentSchemas.RemoveAt(_recentSchemas.Count - 1);
            }

            // Mirror the pair into the config dict so it can be persisted (ReloadLexicon writes the
            // file). Stored as "name|name"; survives a restart and is seeded back on config load.
            _config[KeyRecentSchemas] = string.Join("|", _recentSchemas);
        }

        // Restore the recent-MRU list from the persisted "name|name" config value. Caller must hold
        // _lock. Runs on config load, before the first ReloadLexicon, so a restart remembers the pair.
        private void SeedRecentSchemasFromConfigNoLock()
        {
            _recentSchemas.Clear();
            if (!_config.TryGetValue(KeyRecentSchemas, out string raw) || string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            foreach (string part in raw.Split('|'))
            {
                string name = part.Trim();
                if (name.Length == 0 ||
                    _recentSchemas.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _recentSchemas.Add(name);
                if (_recentSchemas.Count >= 2)
                {
                    break;
                }
            }
        }

        // Ctrl+m: switch to the most recently used "other" code table and reload the lexicon.
        // Returns false (no-op) when fewer than two code tables exist.
        public bool TrySwitchRecentSchema(out string newSchema)
        {
            newSchema = null;

            string[] schemas = GetSchemaList();
            if (schemas == null || schemas.Length < 2)
            {
                return false;
            }

            string current = GetCurrentSchema();

            string target = null;
            lock (_lock)
            {
                foreach (string s in _recentSchemas)
                {
                    if (!string.Equals(s, current, StringComparison.OrdinalIgnoreCase) &&
                        schemas.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
                    {
                        target = s;
                        break;
                    }
                }
            }

            // No recorded second table yet (e.g. just started): seed the pair with the next table in the list.
            if (string.IsNullOrEmpty(target))
            {
                int idx = Array.FindIndex(schemas, x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                int nextIdx = (idx < 0) ? 0 : (idx + 1) % schemas.Length;
                target = schemas[nextIdx];
            }

            // Normalize to the canonical directory casing.
            string canonical = schemas.FirstOrDefault(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(canonical) || string.Equals(canonical, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Cache-aware Ctrl+m switch (faster than the menu/set_config path's disk ReloadLexicon):
            // snapshot the table we are leaving, then restore the target from cache if present.
            string leaving = current;
            LexiconSnapshot leavingSnapshot = CaptureLiveSnapshot();

            // Persist the new current table + recent pair (same as the menu/set_config path, but we
            // replace the disk ReloadLexicon with the snapshot swap below).
            TrySetConfigValue(KeyCurrentMb, canonical, out _, out _);
            lock (_lock) { RecordRecentSchemaNoLock(canonical); }
            WriteConfigNoThrow();

            string targetMbDir = ResolveCurrentMbDir(ResolveCodeRoot());

            LexiconSnapshot hit;
            lock (_lock)
            {
                _lexiconCache.TryGetValue(canonical, out hit); // read before clearing
                _lexiconCache.Clear();                          // enforce capacity 1
                if (!string.IsNullOrEmpty(leaving))
                {
                    _lexiconCache[leaving] = leavingSnapshot;   // keep only the table we just left
                }
            }

            // Cache hit -> instant swap; miss -> load the target from disk (outside the lock).
            ApplyLexiconSnapshot(hit ?? BuildLexiconSnapshot(targetMbDir));

            newSchema = canonical;
            return true;
        }



        public string GetCurrentCodeTablePath()

        {

            try

            {

                string root = ResolveCodeRoot();

                if (string.IsNullOrEmpty(root))

                {

                    return string.Empty;

                }



                string current = ResolveCurrentMbDir(root);

                if (!string.IsNullOrEmpty(current))

                {

                    return current;

                }



                return root;

            }

            catch

            {

                return string.Empty;

            }

        }



        public bool TryExportCurrentLexicon(out string exportPath, out string reason)

        {

            exportPath = string.Empty;

            reason = string.Empty;

            try

            {

                string schemaName = GetCurrentSchema();

                if (string.IsNullOrWhiteSpace(schemaName))

                {

                    reason = "current schema is empty";

                    return false;

                }



                string exportDir = Path.Combine(_exeDir, "\u7801\u8868\u5bfc\u51fa"); // 码表导出

                Directory.CreateDirectory(exportDir);



                string safeSchemaName = string.Concat(schemaName.Split(Path.GetInvalidFileNameChars()));

                if (string.IsNullOrWhiteSpace(safeSchemaName))

                {

                    safeSchemaName = "export";

                }



                string fileName = safeSchemaName + " " + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt";

                string fullPath = Path.Combine(exportDir, fileName);



                List<KeyValuePair<string, List<string>>> snapshot;

                lock (_lock)

                {

                    snapshot = _lexicon

                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)

                        .Select(pair => new KeyValuePair<string, List<string>>(

                            pair.Key,

                            pair.Value == null ? new List<string>() : new List<string>(pair.Value)))

                        .ToList();

                }



                using (var writer = new StreamWriter(fullPath, false, new UTF8Encoding(true)))

                {

                    foreach (var item in snapshot)

                    {

                        if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null || item.Value.Count == 0)

                        {

                            continue;

                        }



                        writer.Write(item.Key);

                        writer.Write(' ');

                        writer.WriteLine(string.Join(" ", item.Value.Select(FormatLexiconEntryForExport)));

                    }

                }



                exportPath = fullPath;

                return true;

            }

            catch (Exception ex)

            {

                reason = ex.Message;

                exportPath = string.Empty;

                return false;

            }

        }



        public string ConstructCi(string name)

        {

            lock (_lock)

            {

                return ConstructCiFromLookup(name, _constructCodeMap);

            }

        }



        private static string ConstructCiFromLookup(string name, Dictionary<string, string> codeLookup)

        {

            string source = name ?? string.Empty;

            foreach (string token in ConstructCiRemoveTokens)

            {

                if (!string.IsNullOrEmpty(token))

                {

                    source = source.Replace(token, string.Empty);

                }

            }



            if (source.Length == 0)

            {

                return string.Empty;

            }



            StringInfo info = new StringInfo(source);

            int sourceLen = info.LengthInTextElements;

            if (sourceLen <= 0)

            {

                return string.Empty;

            }



            var fullCodes = new List<string>(sourceLen);

            for (int i = 0; i < sourceLen; i++)

            {

                string one = info.SubstringByTextElements(i, 1);

                string full = GetFullCodeForSingleChar(one, codeLookup);

                if (string.IsNullOrEmpty(full))

                {

                    // Unknown characters are ignored when constructing code.

                    continue;

                }



                fullCodes.Add(full);

            }



            int len = fullCodes.Count;

            if (len <= 0)

            {

                return string.Empty;

            }



            if (len == 1)

            {

                return fullCodes[0];

            }



            if (len == 2)

            {

                string a = fullCodes[0];

                string b = fullCodes[1];

                if (a.Length >= 2 && b.Length >= 2)

                {

                    return a.Substring(0, 2) + b.Substring(0, 2);

                }



                return string.Empty;

            }



            if (len == 3)

            {

                string a = fullCodes[0];

                string b = fullCodes[1];

                string c = fullCodes[2];

                if (a.Length >= 1 && b.Length >= 1 && c.Length >= 2)

                {

                    return a.Substring(0, 1) + b.Substring(0, 1) + c.Substring(0, 2);

                }



                return string.Empty;

            }



            string first = fullCodes[0];

            string second = fullCodes[1];

            string third = fullCodes[2];

            string last = fullCodes[len - 1];

            if (first.Length >= 1 && second.Length >= 1 && third.Length >= 1 && last.Length >= 1)

            {

                return first.Substring(0, 1) + second.Substring(0, 1) + third.Substring(0, 1) + last.Substring(0, 1);

            }



            return string.Empty;

        }



        private static string GetFullCodeForSingleChar(string oneCharText, Dictionary<string, string> codeLookup)

        {

            if (string.IsNullOrEmpty(oneCharText))

            {

                return string.Empty;

            }



            if (codeLookup != null &&

                codeLookup.TryGetValue(oneCharText, out string bestCode) &&

                !string.IsNullOrEmpty(bestCode))

            {

                return bestCode;

            }



            // For Latin letters in word-construction, treat as pseudo Hanzi:

            // full code = lowercase letter repeated twice (A->aa, k->kk).

            if (oneCharText.Length == 1)

            {

                char ch = oneCharText[0];

                if (IsAsciiLetter(ch))

                {

                    char lower = char.ToLowerInvariant(ch);

                    return new string(lower, 2);

                }

            }



            return string.Empty;

        }



        private static bool IsAsciiLetter(char ch)

        {

            return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

        }



        public string GetConfigText()

        {

            Dictionary<string, string> snapshot;

            lock (_lock)

            {

                snapshot = new Dictionary<string, string>(_config, StringComparer.OrdinalIgnoreCase);

            }



            List<string> lines = BuildConfigLines(snapshot);

            return string.Join("\n", lines);

        }



        public bool TrySetConfigValue(string key, string value, out bool changed, out string reason)

        {

            changed = false;

            reason = string.Empty;



            string k = (key ?? string.Empty).Trim();

            if (k.Length == 0)

            {

                reason = "key is empty";

                return false;

            }

            if (!KnownConfigKeys.Contains(k))

            {

                reason = "unknown key";

                return false;

            }



            string v = (value ?? string.Empty).Trim();

            if (string.Equals(k, KeyCodeRoot, StringComparison.OrdinalIgnoreCase))

            {

                v = NormalizePathSetting(v);

            }



            lock (_lock)

            {

                string oldValue = _config.TryGetValue(k, out string oldRaw) ? oldRaw ?? string.Empty : string.Empty;

                if (string.Equals(oldValue, v, StringComparison.Ordinal))

                {

                    return true;

                }



                _config[k] = v;

                ConfigVersion++;

                changed = true;

            }



            WriteConfigNoThrow();

            return true;

        }



        private void LoadMbDir(string dir,

                               List<(string code, string text, int freq)> codedRows,

                               List<(string text, int freq)> noCodeRows,

                               bool skipConstructCodeFile)

        {

            foreach (string file in Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))

            {

                if (skipConstructCodeFile &&

                    string.Equals(Path.GetFileName(file), ConstructCodeFileName, StringComparison.OrdinalIgnoreCase))

                {

                    continue;

                }



                ParseMbFile(file, codedRows, noCodeRows);

            }

            foreach (string file in Directory.GetFiles(dir, "*.dict.yaml", SearchOption.TopDirectoryOnly))

            {

                ParseMbFile(file, codedRows, noCodeRows);

            }

        }



        private static void ParseMbFile(string path,

                                        List<(string code, string text, int freq)> outCodedRows,

                                        List<(string text, int freq)> outNoCodeRows)

        {

            Encoding enc = DetectTextEncoding(path);

            bool body = !path.EndsWith(".dict.yaml", StringComparison.OrdinalIgnoreCase);

            foreach (string raw in File.ReadLines(path, enc))

            {

                if (string.IsNullOrWhiteSpace(raw)) { continue; }

                string line = raw.Trim();

                if (line.StartsWith("#", StringComparison.Ordinal)) { continue; }

                if (!body) { if (line == "...") { body = true; } continue; }

                line = StripInlineComment(line);

                if (line.Length == 0) { continue; }

                if (line.StartsWith("{\u6dfb\u52a0}") || line.StartsWith("{\u5220\u9664}") || line.StartsWith("{\u7f6e\u9876}") || line.StartsWith("{\u524d\u79fb}")) { continue; } // unicode: {娣诲姞} ; {鍒犻櫎} ; {缃《} ; {鍓嶇Щ}

                if (TryParseCodeFirstLine(line, out string codeFirstCode, out List<string> codeFirstTexts, out int codeFirstFreq))

                {

                    foreach (string item in codeFirstTexts)

                    {

                        outCodedRows.Add((codeFirstCode, item, codeFirstFreq));

                    }

                    continue;

                }

                string[] p = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (p.Length < 1) { continue; }

                string text = ParseLexiconEntryToken(p[0]);

                if (text.Length == 0) { continue; }

                string code = string.Empty;

                int freq = 0;

                if (p.Length >= 3 && int.TryParse(p[2], out int f2)) { code = p[1]; freq = f2; }

                else if (p.Length >= 3 && int.TryParse(p[1], out int f1)) { code = p[2]; freq = f1; }

                else if (p.Length >= 2 && !int.TryParse(p[1], out _)) { code = p[1]; }

                else if (p.Length >= 2 && int.TryParse(p[1], out int f4)) { freq = f4; }



                if (!string.IsNullOrWhiteSpace(code))

                {

                    outCodedRows.Add((code.Trim(), text, freq));

                }

                else

                {

                    outNoCodeRows.Add((text, freq));

                }

            }

        }



        private static void ParseMbFile(string path, List<(string code, string text, int freq)> outRows)

        {

            ParseMbFile(path, outRows, new List<(string text, int freq)>());

        }



        private static void AppendInferredNoCodeRows(List<(string text, int freq)> noCodeRows,

                                                     Dictionary<string, string> constructCodeMap,

                                                     List<(string code, string text, int freq)> codedRows)

        {

            if (noCodeRows == null || codedRows == null || noCodeRows.Count == 0)

            {

                return;

            }



            foreach ((string text, int freq) row in noCodeRows)

            {

                if (string.IsNullOrEmpty(row.text))

                {

                    continue;

                }



                string inferredCode = ConstructCiFromLookup(GetCommitText(row.text), constructCodeMap);

                string code = NormalizeCode(inferredCode);

                if (code.Length == 0)

                {

                    continue;

                }



                codedRows.Add((code, row.text, row.freq));

            }

        }



        private static void LoadAdjust(string file, Dictionary<string, List<string>> target)

        {

            if (!File.Exists(file)) { return; }

            Encoding enc = DetectTextEncoding(file);

            foreach (string raw in File.ReadLines(file, enc))

            {

                if (string.IsNullOrWhiteSpace(raw)) { continue; }

                string line = raw.Trim();

                if (!line.StartsWith("{", StringComparison.Ordinal)) { continue; }

                if (line.StartsWith("{\u6dfb\u52a0}")) { ApplyAddLike(line.Substring("{\u6dfb\u52a0}".Length), target, false); } // unicode: {娣诲姞}

                else if (line.StartsWith("{\u7f6e\u9876}")) { ApplyAddLike(line.Substring("{\u7f6e\u9876}".Length), target, true); } // unicode: {缃《}

                else if (line.StartsWith("{\u5220\u9664}")) { ApplyDelete(line.Substring("{\u5220\u9664}".Length), target); } // unicode: {鍒犻櫎}

                else if (line.StartsWith("{\u524d\u79fb}")) { ApplyAdvance(line.Substring("{\u524d\u79fb}".Length), target); } // unicode: {鍓嶇Щ}

            }

        }



        private static void LoadCustom(string file, Dictionary<string, List<string>> target)

        {

            if (!File.Exists(file)) { return; }

            Encoding enc = DetectTextEncoding(file);

            foreach (string raw in File.ReadLines(file, enc))

            {

                if (string.IsNullOrWhiteSpace(raw)) { continue; }

                string line = raw.Trim(); if (line.StartsWith("#", StringComparison.Ordinal)) { continue; }

                string[] p = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (p.Length < 2) { continue; }

                string code = NormalizeCode(p[0]);

                string text = ParseLexiconEntryToken(p[1].Trim());

                if (code.Length == 0 || text.Length == 0) { continue; }

                if (!target.TryGetValue(code, out List<string> list)) { list = new List<string>(); target[code] = list; }

                list.RemoveAll(x => CandidateIdentityEquals(x, text));

                list.Insert(0, text);

            }

        }



        private static void RebuildMeta(Dictionary<string, List<string>> map, out HashSet<string> unique, out HashSet<string> nonTerminal)

        {

            unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            nonTerminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var hasLonger = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in map)

            {

                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || kv.Value.Count == 0) { continue; }

                for (int i = 1; i < kv.Key.Length; i++) { hasLonger.Add(kv.Key.Substring(0, i)); }

            }

            foreach (var kv in map)

            {

                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || kv.Value.Count != 1) { continue; }

                if (!hasLonger.Contains(kv.Key)) { unique.Add(kv.Key); }

            }

            nonTerminal.UnionWith(hasLonger);

        }



        private static void RebuildShortSymbolMeta(Dictionary<string, List<string>> map,

                                                   out bool shortSemicolon,

                                                   out bool shortSlash,

                                                   out bool shortLBracket,

                                                   out bool shortZ,

                                                   out HashSet<string> autoShortSymbol)

        {

            shortSemicolon = false;

            shortSlash = false;

            shortLBracket = false;

            shortZ = false;

            autoShortSymbol = new HashSet<string>(StringComparer.OrdinalIgnoreCase);



            var headSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool zIsCode = false;

            foreach (KeyValuePair<string, List<string>> kv in map)

            {

                string key = kv.Key;

                if (string.IsNullOrEmpty(key))

                {

                    continue;

                }



                string head = key.Substring(0, 1);

                headSet.Add(head);

                if (!zIsCode && !string.Equals(head, "z", StringComparison.OrdinalIgnoreCase) && key.IndexOf('z') >= 0)

                {

                    zIsCode = true;

                }

            }



            shortSemicolon = headSet.Contains(";");

            shortSlash = headSet.Contains("/");

            shortLBracket = headSet.Contains("[");

            shortZ = !zIsCode && headSet.Contains("a");



            if (!(shortSemicolon || shortSlash || shortLBracket || shortZ))

            {

                return;

            }



            var symbolMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var countMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, List<string>> kv in map)

            {

                string key = kv.Key;

                if (string.IsNullOrEmpty(key))

                {

                    continue;

                }



                bool include = (shortSemicolon && key.StartsWith(";", StringComparison.Ordinal)) ||

                               (shortSlash && key.StartsWith("/", StringComparison.Ordinal)) ||

                               (shortLBracket && key.StartsWith("[", StringComparison.Ordinal)) ||

                               (shortZ && key.StartsWith("z", StringComparison.OrdinalIgnoreCase));

                if (!include)

                {

                    continue;

                }



                symbolMap[key] = kv.Value;

                countMap[key] = 0;

            }



            foreach (KeyValuePair<string, List<string>> kv in symbolMap)

            {

                for (int i = 0; i < kv.Key.Length; i++)

                {

                    string prefix = kv.Key.Substring(0, i + 1);

                    if (symbolMap.TryGetValue(prefix, out List<string> prefixCandidates))

                    {

                        countMap[prefix] += prefixCandidates?.Count ?? 0;

                    }

                }

            }



            foreach (KeyValuePair<string, int> kv in countMap)

            {

                if (kv.Value == 1)

                {

                    autoShortSymbol.Add(kv.Key);

                }

            }

        }



        private static void ApplyAddLike(string payload, Dictionary<string, List<string>> target, bool top)

        {

            if (!TryParsePair(payload, out string code, out string text)) { return; }

            if (!target.TryGetValue(code, out List<string> list)) { list = new List<string>(); target[code] = list; }

            int existingIndex = list.FindIndex(x => CandidateIdentityEquals(x, text));

            string stored = existingIndex >= 0 ? list[existingIndex] : text;

            if (existingIndex >= 0) { list.RemoveAt(existingIndex); }

            if (top) { list.Insert(0, stored); } else { list.Add(stored); }

        }



        private static void ApplyDelete(string payload, Dictionary<string, List<string>> target)

        {

            if (!TryParsePair(payload, out string code, out string text)) { return; }

            if (target.TryGetValue(code, out List<string> list)) { list.RemoveAll(x => CandidateIdentityEquals(x, text)); }

        }



        private static void ApplyAdvance(string payload, Dictionary<string, List<string>> target)

        {

            if (!TryParsePair(payload, out string code, out string text)) { return; }

            if (!target.TryGetValue(code, out List<string> list)) { return; }

            int pos = list.FindIndex(x => CandidateIdentityEquals(x, text));

            if (pos > 0)

            {

                string stored = list[pos];

                list.RemoveAt(pos);

                list.Insert(pos - 1, stored);

            }

        }



        private static bool TryParsePair(string payload, out string code, out string text)

        {

            code = string.Empty; text = string.Empty;

            if (string.IsNullOrWhiteSpace(payload)) { return false; }

            string[] p = payload.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (p.Length < 2) { return false; }

            code = NormalizeCode(p[0]);

            text = ParseLexiconEntryToken(p[1].Trim());

            return code.Length > 0 && text.Length > 0;

        }



        private static Dictionary<string, string> LoadCommentMap(string mbDir)

        {

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(mbDir) || !Directory.Exists(mbDir))

            {

                return map;

            }



            string commentGlob = "*." + "\u6CE8\u91CA"; // 注释
            foreach (string file in Directory.GetFiles(mbDir, commentGlob, SearchOption.TopDirectoryOnly))

            {

                Encoding enc = DetectTextEncoding(file);

                foreach (string raw in File.ReadLines(file, enc))

                {

                    string line = (raw ?? string.Empty).Trim();

                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))

                    {

                        continue;

                    }



                    string[] parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 2)

                    {

                        continue;

                    }



                    string key = WrapDec(parts[0]);

                    string value = WrapDec(parts[1]);

                    if (key.Length == 0 || value.Length == 0)

                    {

                        continue;

                    }



                    if (!map.ContainsKey(key))

                    {

                        map[key] = value;

                    }

                    else

                    {

                        map[key] = map[key] + " " + value;

                    }

                }

            }



            return map;

        }



        private static Dictionary<string, string> LoadSplitMap(string mbDir)

        {

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(mbDir) || !Directory.Exists(mbDir))

            {

                return map;

            }



            string splitGlob = "*." + "\u62C6\u5206"; // 拆分
            foreach (string file in Directory.GetFiles(mbDir, splitGlob, SearchOption.TopDirectoryOnly))

            {

                Encoding enc = DetectTextEncoding(file);

                foreach (string raw in File.ReadLines(file, enc))

                {

                    string line = (raw ?? string.Empty).Trim();

                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))

                    {

                        continue;

                    }



                    string[] parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 2)

                    {

                        continue;

                    }



                    string key = WrapDec(parts[0]);

                    string value = WrapDec(parts[1]);

                    if (key.Length == 0 || value.Length == 0)

                    {

                        continue;

                    }



                    map[key] = value;

                }

            }



            return map;

        }



        private static Dictionary<string, string> BuildFullCodeMap(Dictionary<string, List<string>> lexicon)

        {

            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (lexicon == null)

            {

                return map;

            }



            foreach (KeyValuePair<string, List<string>> kv in lexicon)

            {

                string code = kv.Key ?? string.Empty;

                if (code.Length == 0 || kv.Value == null || kv.Value.Count == 0)

                {

                    continue;

                }



                foreach (string text in kv.Value)

                {
                    string commitText = GetCommitText(text);

                    if (string.IsNullOrEmpty(commitText))

                    {

                        continue;

                    }



                    if (!map.TryGetValue(commitText, out string oldCode) || oldCode.Length < code.Length)

                    {

                        map[commitText] = code;

                    }

                }

            }



            return map;

        }



        private static Dictionary<string, string> BuildConstructCodeMap(string constructPath, bool hasConstructFile, Dictionary<string, List<string>> lexicon)

        {

            var map = new Dictionary<string, string>(StringComparer.Ordinal);



            // Step 1: read 鏋勮瘝.txt first; values here have highest priority.

            if (hasConstructFile && !string.IsNullOrEmpty(constructPath) && File.Exists(constructPath))

            {

                var rows = new List<(string code, string text, int freq)>(1024);

                ParseMbFile(constructPath, rows);

                foreach ((string codeRaw, string textRaw, int _) in rows)

                {

                    string code = NormalizeCode(codeRaw);

                    if (code.Length == 0)

                    {

                        continue;

                    }



                    string text = GetCommitText(textRaw);

                    if (!IsSingleTextElement(text))

                    {

                        continue;

                    }



                    if (!map.ContainsKey(text))

                    {

                        map[text] = code;

                    }

                }

            }



            if (lexicon == null || lexicon.Count == 0)

            {

                return map;

            }



            var bestFromLexicon = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, List<string>> kv in lexicon)

            {

                string code = NormalizeCode(kv.Key);

                if (code.Length < 2 || kv.Value == null || kv.Value.Count == 0)

                {

                    continue;

                }



                foreach (string text in kv.Value)

                {
                    string commitText = GetCommitText(text);

                    if (string.IsNullOrEmpty(commitText) || !IsSingleTextElement(commitText) || map.ContainsKey(commitText))

                    {

                        continue;

                    }



                    if (!bestFromLexicon.TryGetValue(commitText, out string oldCode))

                    {

                        bestFromLexicon[commitText] = code;

                    }

                    else

                    {

                        int oldRank = GetConstructCodePriority(oldCode);

                        int newRank = GetConstructCodePriority(code);

                        if (newRank > oldRank ||

                            (newRank == oldRank && string.CompareOrdinal(code, oldCode) < 0))

                        {

                            bestFromLexicon[commitText] = code;

                        }

                    }

                }

            }



            foreach (KeyValuePair<string, string> kv in bestFromLexicon)

            {

                if (!map.ContainsKey(kv.Key))

                {

                    map[kv.Key] = kv.Value;

                }

            }



            return map;

        }



        private static bool IsSingleTextElement(string text)

        {

            if (string.IsNullOrEmpty(text))

            {

                return false;

            }



            return new StringInfo(text).LengthInTextElements == 1;

        }



        private static int GetConstructCodePriority(string code)

        {

            if (string.IsNullOrEmpty(code))

            {

                return int.MinValue;

            }



            char first = char.ToLowerInvariant(code[0]);

            if (!char.IsLetter(first))

            {

                return 0;

            }



            if (first == 'o')

            {

                return 1;

            }



            if (first == 'z')

            {

                return 2;

            }



            return 3;

        }



        private static bool TryCollectByTextElements(string text, Dictionary<string, string> lookup, List<string> output)

        {

            output.Clear();

            if (string.IsNullOrEmpty(text) || lookup == null || lookup.Count == 0)

            {

                return false;

            }



            StringInfo info = new StringInfo(text);

            for (int i = 0; i < info.LengthInTextElements; i++)

            {

                string one = info.SubstringByTextElements(i, 1);

                if (!lookup.TryGetValue(one, out string value) || string.IsNullOrEmpty(value))

                {

                    output.Clear();

                    return false;

                }



                output.Add(value);

            }



            return output.Count > 0;

        }



        private Dictionary<string, List<string>> LoadPinyinLexicon()

        {

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            string root = ResolvePinyinRoot();

            if (!Directory.Exists(root))

            {

                return map;

            }



            var rows = new List<(string code, string text, int freq)>(2048);

            foreach (string file in Directory.GetFiles(root, "*.txt", SearchOption.TopDirectoryOnly))

            {

                ParseMbFile(file, rows);

            }



            foreach ((string code, string text, int freq) row in rows.OrderByDescending(x => x.freq))

            {

                string c = NormalizeCode(row.code);

                if (c.Length == 0)

                {

                    continue;

                }



                if (!map.TryGetValue(c, out List<string> list))

                {

                    list = new List<string>();

                    map[c] = list;

                }



                if (!list.Contains(row.text))

                {

                    list.Add(row.text);

                }

            }



            return map;

        }



        private string ResolvePinyinRoot()

        {

            return Path.GetFullPath(Path.Combine(_exeDir, "\u62fc\u97f3\u53cd\u67e5\u7801\u8868")); // unicode: 鎷奸煶鍙嶆煡鐮佽〃

        }



        private void EnsureConfigFile()

        {
            string directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_configPath)) { return; }

            Dictionary<string, string> defaults = CreateDefaultConfig();

            File.WriteAllLines(_configPath, BuildConfigLines(defaults), new UTF8Encoding(false));

        }



        private void EnsureCustomFile()

        {

            if (!File.Exists(_customPath))

            {

                File.WriteAllText(_customPath, "# code<TAB>text" + Environment.NewLine, new UTF8Encoding(false));

            }

        }



        private Dictionary<string, string> CreateDefaultConfig()

        {

            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> kv in DefaultConfigPairs)

            {

                cfg[kv.Key] = kv.Value;

            }



            return cfg;

        }



        private static List<string> BuildConfigLines(Dictionary<string, string> cfg)

        {

            var lines = new List<string>(cfg.Count + 1)

            {

                "# TigerClaw.Core config"

            };

            foreach (KeyValuePair<string, string> kv in DefaultConfigPairs)

            {

                if (cfg.TryGetValue(kv.Key, out string value))

                {

                    lines.Add(kv.Key + "\t" + value);

                }

            }

            return lines;

        }

        private static string GetAppDataDirectory()

        {

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TigerClaw");

        }



        private void WriteConfigNoThrow()

        {

            try

            {

                Dictionary<string, string> snapshot;

                lock (_lock) { snapshot = new Dictionary<string, string>(_config, StringComparer.OrdinalIgnoreCase); }

                File.WriteAllLines(_configPath, BuildConfigLines(snapshot), new UTF8Encoding(false));

            }

            catch { }

        }



        private void SyncAutoStartNoThrow()

        {

            try

            {

                bool enabled;

                lock (_lock)

                {

                    enabled = _config.TryGetValue(KeyAutoStart, out string raw) ? ParseBool(raw, false) : false;

                }



                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true) ??

                                             Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))

                {

                    if (runKey == null)

                    {

                        return;

                    }



                    const string valueName = "TigerClawCore";
                    const string legacyValueName = "TigerClaw";

                    if (!enabled)

                    {

                        if (runKey.GetValue(valueName) != null)

                        {

                            runKey.DeleteValue(valueName, false);

                        }

                        if (runKey.GetValue(legacyValueName) != null)

                        {

                            runKey.DeleteValue(legacyValueName, false);

                        }

                        return;

                    }



                    string exePath = Process.GetCurrentProcess().MainModule?.FileName;

                    if (string.IsNullOrWhiteSpace(exePath))

                    {

                        exePath = Path.Combine(_exeDir, "TigerClaw.Core.exe");

                    }



                    string command = "\"" + exePath + "\" --with-overlay";

                    string current = runKey.GetValue(valueName) as string;

                    if (!string.Equals(current ?? string.Empty, command, StringComparison.Ordinal))

                    {

                        runKey.SetValue(valueName, command, RegistryValueKind.String);

                    }

                    if (runKey.GetValue(legacyValueName) != null)

                    {

                        runKey.DeleteValue(legacyValueName, false);

                    }

                }

            }

            catch { }

        }



        private string ResolveCodeRoot()

        {

            string configured;

            lock (_lock) { configured = _config.TryGetValue(KeyCodeRoot, out string raw) ? NormalizePathSetting(raw) : DefaultCodeRoot; }

            if (Path.IsPathRooted(configured))

            {

                return Path.GetFullPath(configured);

            }



            return Path.GetFullPath(Path.Combine(_exeDir, configured));

        }



        private string ResolveCurrentMbDir(string root)

        {

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return null; }

            string[] dirs = Directory.GetDirectories(root);

            if (dirs.Length == 0) { return null; }

            string current;

            lock (_lock) { _config.TryGetValue(KeyCurrentMb, out current); }

            if (!string.IsNullOrEmpty(current))

            {

                foreach (string d in dirs) { if (string.Equals(Path.GetFileName(d), current, StringComparison.OrdinalIgnoreCase)) { return d; } }

            }

            string fallback = dirs.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            if (string.IsNullOrEmpty(fallback)) { return null; }

            lock (_lock) { _config[KeyCurrentMb] = fallback; ConfigVersion++; }

            WriteConfigNoThrow();

            return Path.Combine(root, fallback);

        }



        private void AppendAdjustOperationNoThrow(string opPrefix, string code, string text)

        {

            try

            {

                string adjustPath = ResolveAdjustFilePathNoThrow();

                if (string.IsNullOrEmpty(adjustPath))

                {

                    return;

                }



                string dir = Path.GetDirectoryName(adjustPath);

                if (!string.IsNullOrEmpty(dir))

                {

                    Directory.CreateDirectory(dir);

                }



                File.AppendAllText(adjustPath, opPrefix + code + "\t" + FormatLexiconEntryForExport(text) + Environment.NewLine, new UTF8Encoding(false));

            }

            catch

            {

            }

        }



        private string ResolveAdjustFilePathNoThrow()

        {

            try

            {

                string root = ResolveCodeRoot();

                string mbDir = ResolveCurrentMbDir(root);

                if (string.IsNullOrEmpty(mbDir))

                {

                    return null;

                }



                return Path.Combine(mbDir, "\u7528\u6237\u8c03\u6574.txt"); // unicode: 鐢ㄦ埛璋冩暣.txt

            }

            catch

            {

                return null;

            }

        }



        private static int FindSep(string line)

        {

            for (int i = 0; i < line.Length; i++) { if (line[i] == '\t' || line[i] == ' ' || line[i] == ',') { return i; } }

            return -1;

        }



        private static string StripInlineComment(string line)

        {

            if (string.IsNullOrEmpty(line))

            {

                return string.Empty;

            }



            var sb = new StringBuilder(line.Length);

            int slashRun = 0;

            for (int i = 0; i < line.Length; i++)

            {

                char ch = line[i];

                if (ch == '#')

                {

                    if ((slashRun & 1) == 1)

                    {

                        // "\#" means literal '#': drop the escaping slash and keep '#'.

                        if (sb.Length > 0) { sb.Length -= 1; }

                        sb.Append('#');

                        slashRun = 0;

                        continue;

                    }



                    break;

                }



                sb.Append(ch);

                slashRun = (ch == '\\') ? (slashRun + 1) : 0;

            }



            return sb.ToString().Trim();

        }



        private static string NormalizeCode(string code) => (code ?? string.Empty).Trim().ToLowerInvariant();



        private static bool TryParseCodeFirstLine(string line, out string code, out List<string> texts, out int freq)

        {

            code = string.Empty;

            texts = null;

            freq = 0;

            if (string.IsNullOrWhiteSpace(line) || line.IndexOf('\t') >= 0)

            {

                return false;

            }



            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)

            {

                return false;

            }



            string first = NormalizeCode(parts[0]);

            if (first.Length == 0 || !IsLikelyCodeToken(parts[0]))

            {

                return false;

            }



            int endExclusive = parts.Length;

            if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1], out int parsedFreq))

            {

                freq = parsedFreq;

                endExclusive--;

            }



            var parsedTexts = new List<string>();

            for (int i = 1; i < endExclusive; i++)

            {

                string token = ParseLexiconEntryToken(parts[i]);

                if (token.Length > 0)

                {

                    parsedTexts.Add(token);

                }

            }



            if (parsedTexts.Count == 0)

            {

                return false;

            }



            code = first;

            texts = parsedTexts;

            return true;

        }



        private static bool IsLikelyCodeToken(string token)

        {

            if (string.IsNullOrWhiteSpace(token))

            {

                return false;

            }



            string text = token.Trim();

            for (int i = 0; i < text.Length; i++)

            {

                char ch = text[i];

                bool ok =

                    (ch >= 'a' && ch <= 'z') ||

                    (ch >= 'A' && ch <= 'Z') ||

                    (ch >= '0' && ch <= '9') ||

                    ch == ';' ||

                    ch == '/' ||

                    ch == '[' ||

                    ch == ']' ||

                    ch == '\'' ||

                    ch == '-' ||

                    ch == '=';

                if (!ok)

                {

                    return false;

                }

            }



            return true;

        }



        private static string ParseLexiconEntryToken(string token)

        {

            string raw = WrapDec(token?.Trim());

            if (raw.Length == 0)

            {

                return string.Empty;

            }



            int markerIndex = raw.IndexOf(DisplayCommitMarker, StringComparison.Ordinal);

            if (markerIndex <= 0 || markerIndex >= raw.Length - DisplayCommitMarker.Length)

            {

                return raw;

            }



            string displayText = raw.Substring(0, markerIndex);

            string commitText = raw.Substring(markerIndex + DisplayCommitMarker.Length);

            if (displayText.Length == 0 || commitText.Length == 0)

            {

                return raw;

            }



            return PackDisplayCommitEntry(displayText, commitText);

        }



        private static string PackDisplayCommitEntry(string displayText, string commitText)

        {

            string display = displayText ?? string.Empty;

            string commit = commitText ?? string.Empty;

            if (display.Length == 0 || commit.Length == 0 || string.Equals(display, commit, StringComparison.Ordinal))

            {

                return commit;

            }



            return display + DisplayCommitSeparator + commit;

        }



        private static bool IsPackedDisplayCommitEntry(string entry)

        {

            return !string.IsNullOrEmpty(entry) && entry.IndexOf(DisplayCommitSeparator, StringComparison.Ordinal) >= 0;

        }



        private static void UnpackDisplayCommitEntry(string entry, out string displayText, out string commitText)

        {

            string value = entry ?? string.Empty;

            int separatorIndex = value.IndexOf(DisplayCommitSeparator, StringComparison.Ordinal);

            if (separatorIndex < 0)

            {

                displayText = value;

                commitText = value;

                return;

            }



            displayText = value.Substring(0, separatorIndex);

            commitText = value.Substring(separatorIndex + DisplayCommitSeparator.Length);

            if (displayText.Length == 0)

            {

                displayText = commitText;

            }

            if (commitText.Length == 0)

            {

                commitText = displayText;

            }

        }



        private static string GetCommitText(string entry)

        {

            UnpackDisplayCommitEntry(entry, out _, out string commitText);

            return commitText;

        }



        private static bool CandidateIdentityEquals(string left, string right)

        {

            return string.Equals(GetCommitText(left), GetCommitText(right), StringComparison.Ordinal);

        }



        private static string FormatLexiconEntryForExport(string entry)

        {

            UnpackDisplayCommitEntry(entry, out string displayText, out string commitText);

            if (string.Equals(displayText, commitText, StringComparison.Ordinal))

            {

                return WrapEnc(commitText);

            }



            return WrapEnc(displayText + DisplayCommitMarker + commitText);

        }



        private static string NormalizePathSetting(string value)

        {

            string p = (value ?? string.Empty).Trim();

            if (p.Length == 0) { return DefaultCodeRoot; }

            while (p.Length > 1 && (p.EndsWith("\\", StringComparison.Ordinal) || p.EndsWith("/", StringComparison.Ordinal)))

            {

                if (p.Length == 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) { break; }

                p = p.Substring(0, p.Length - 1);

            }

            return p.Length == 0 ? DefaultCodeRoot : p;

        }



        private static bool ParseBool(string text, bool def)

        {

            if (string.IsNullOrWhiteSpace(text)) { return def; }

            string v = text.Trim();

            if (string.Equals(v, Yes, StringComparison.OrdinalIgnoreCase) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase) || v == "1") { return true; }

            if (string.Equals(v, No, StringComparison.OrdinalIgnoreCase) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase) || v == "0") { return false; }

            return def;

        }



        private static bool ReadEnvFlag(string name)

        {

            string value = Environment.GetEnvironmentVariable(name) ?? string.Empty;

            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||

                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||

                   string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||

                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

        }



        private static string WrapDec(string s)

        {

            const string marker = "Bime20231222BIME";

            if (s == null) { return string.Empty; }

            return s.Replace("\\\\", marker).Replace("\\t", "\t").Replace("\\n", "\r\n").Replace("\\s", " ").Replace(marker, "\\");

        }



        private static string WrapEnc(string s)

        {

            if (s == null) { return string.Empty; }

            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace(" ", "\\s");

        }



        private static Encoding DetectTextEncoding(string filePath)

        {

            try

            {

                using (var stream = File.OpenRead(filePath))

                {

                    byte[] head = new byte[4];

                    int read = stream.Read(head, 0, head.Length);

                    if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { return Encoding.UTF8; }

                    if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE) { return Encoding.Unicode; }

                    if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF) { return Encoding.BigEndianUnicode; }

                    stream.Position = 0;

                    byte[] all = new byte[stream.Length];

                    stream.Read(all, 0, all.Length);

                    if (LooksUtf8(all)) { return Encoding.UTF8; }

                }

            }

            catch { }

            return Encoding.Default;

        }



        private static bool LooksUtf8(byte[] data)

        {

            int remain = 0;

            for (int i = 0; i < data.Length; i++)

            {

                byte b = data[i];

                if (remain == 0)

                {

                    if ((b & 0x80) == 0) { continue; }

                    while ((b & 0x80) != 0) { remain++; b <<= 1; }

                    if (remain <= 1 || remain > 6) { return false; }

                    remain--;

                }

                else

                {

                    if ((b & 0xC0) != 0x80) { return false; }

                    remain--;

                }

            }

            return remain == 0;

        }

    }

}


