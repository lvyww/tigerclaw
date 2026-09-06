using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;

namespace TigerClaw.Engine.NativeAot;

public static unsafe class NativeExports
{
    private const int StatusOk = 0;
    private const int StatusInvalidArgument = 1;
    private const int StatusRuntimeError = 2;

    [UnmanagedCallersOnly(EntryPoint = "tc_runtime_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int RuntimeCreate(TcUtf8Slice lexiconPath, nint* outRuntime)
    {
        return RuntimeCreateCore(lexiconPath, BasicEngineConfig.Default, null, null, null, null, null, null, outRuntime);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_runtime_create_with_config", CallConvs = [typeof(CallConvCdecl)])]
    public static int RuntimeCreateWithConfig(TcUtf8Slice lexiconPath, TcEngineConfig* config, nint* outRuntime)
    {
        if (config is null)
        {
            return StatusInvalidArgument;
        }

        if (!config->UserDictionaryPath.IsValid || !config->SelectionKeys.IsValid || !config->PreviousPageKeys.IsValid || !config->NextPageKeys.IsValid || !config->PinyinLexiconPath.IsValid || !config->SentenceModelPath.IsValid || !config->SentenceQwenNativeLibraryPath.IsValid || !config->SentenceQwenModelPath.IsValid || !config->SentenceLexiconPath.IsValid || !config->SentenceFullCodeWhitelist.IsValid)
        {
            return StatusInvalidArgument;
        }

        string? userDictionaryPath = config->UserDictionaryPath.Data is null
            ? null
            : Utf8ToString(config->UserDictionaryPath);
        string? pinyinLexiconPath = config->PinyinLexiconPath.Data is null
            ? null
            : Utf8ToString(config->PinyinLexiconPath);
        string? sentenceModelPath = config->SentenceModelPath.Data is null
            ? null
            : Utf8ToString(config->SentenceModelPath);
        string? sentenceQwenNativeLibraryPath = config->SentenceQwenNativeLibraryPath.Data is null
            ? null
            : Utf8ToString(config->SentenceQwenNativeLibraryPath);
        string? sentenceQwenModelPath = config->SentenceQwenModelPath.Data is null
            ? null
            : Utf8ToString(config->SentenceQwenModelPath);
        string? sentenceLexiconPath = config->SentenceLexiconPath.Data is null
            ? null
            : Utf8ToString(config->SentenceLexiconPath);
        string? sentenceFullCodeWhitelist = config->SentenceFullCodeWhitelist.Data is null
            ? null
            : Utf8ToString(config->SentenceFullCodeWhitelist);
        BasicEngineConfig engineConfig = BasicEngineConfig.FromAbi(*config, sentenceFullCodeWhitelist).WithConfiguredKeys(
            Utf8ToNullableString(config->SelectionKeys),
            Utf8ToNullableString(config->PreviousPageKeys),
            Utf8ToNullableString(config->NextPageKeys));
        return RuntimeCreateCore(lexiconPath, engineConfig, userDictionaryPath, pinyinLexiconPath, sentenceModelPath, sentenceQwenNativeLibraryPath, sentenceQwenModelPath, sentenceLexiconPath, outRuntime);
    }

    private static int RuntimeCreateCore(
        TcUtf8Slice lexiconPath,
        BasicEngineConfig config,
        string? userDictionaryPath,
        string? pinyinLexiconPath,
        string? sentenceModelPath,
        string? sentenceQwenNativeLibraryPath,
        string? sentenceQwenModelPath,
        string? sentenceLexiconPath,
        nint* outRuntime)
    {
        if (!lexiconPath.IsValid || outRuntime is null)
        {
            return StatusInvalidArgument;
        }

        *outRuntime = 0;
        return Try(() =>
        {
            string path = Utf8ToString(lexiconPath);
            RimeLexicon lexicon = RimeLexicon.Load(path, userDictionaryPath);
            string resolvedSentenceLexiconPath = !string.IsNullOrWhiteSpace(sentenceLexiconPath) && File.Exists(sentenceLexiconPath)
                ? sentenceLexiconPath
                : path;
            RimeLexicon sentenceLexicon = !string.Equals(resolvedSentenceLexiconPath, path, StringComparison.Ordinal)
                ? RimeLexicon.Load(resolvedSentenceLexiconPath, userDictionaryPath)
                : lexicon;
            RuntimeHandle runtime = new(path, resolvedSentenceLexiconPath, lexicon, sentenceLexicon, PinyinLexicon.Load(pinyinLexiconPath), config, sentenceModelPath, sentenceQwenNativeLibraryPath, sentenceQwenModelPath);
            *outRuntime = GCHandle.ToIntPtr(GCHandle.Alloc(runtime));
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_runtime_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void RuntimeRelease(nint runtime)
    {
        FreeHandle(runtime);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionCreate(nint runtime, nint* outSession)
    {
        if (runtime == 0 || outSession is null)
        {
            return StatusInvalidArgument;
        }

        *outSession = 0;
        return Try(() =>
        {
            RuntimeHandle runtimeHandle = Get<RuntimeHandle>(runtime);
            SessionHandle session = new(runtimeHandle.CreateEngine());
            *outSession = GCHandle.ToIntPtr(GCHandle.Alloc(session));
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionActivate(nint session)
    {
        if (session == 0)
        {
            return StatusInvalidArgument;
        }

        return Try(() => Get<SessionHandle>(session).Active = true);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_process", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionProcess(nint session, TcInputEvent* input, nint* outSnapshot)
    {
        if (session == 0 || input is null || outSnapshot is null)
        {
            return StatusInvalidArgument;
        }

        *outSnapshot = 0;
        return Try(() =>
        {
            SessionHandle sessionHandle = Get<SessionHandle>(session);
            EngineSnapshot snapshot = sessionHandle.Active
                ? sessionHandle.Engine.Process(ReadInputEvent(input))
                : EngineSnapshot.Empty(handled: false);
            *outSnapshot = GCHandle.ToIntPtr(GCHandle.Alloc(new SnapshotHandle(snapshot)));
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_select_candidate", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionSelectCandidate(nint session, int pageIndex, nint* outSnapshot)
    {
        if (session == 0 || pageIndex < 0 || outSnapshot is null)
        {
            return StatusInvalidArgument;
        }

        *outSnapshot = 0;
        return Try(() =>
        {
            SessionHandle sessionHandle = Get<SessionHandle>(session);
            EngineSnapshot snapshot = sessionHandle.Active
                ? sessionHandle.Engine.SelectCandidate(pageIndex)
                : EngineSnapshot.Empty(handled: false);
            *outSnapshot = GCHandle.ToIntPtr(GCHandle.Alloc(new SnapshotHandle(snapshot)));
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_query_snapshot", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionQuerySnapshot(nint session, nint* outSnapshot)
    {
        if (session == 0 || outSnapshot is null)
        {
            return StatusInvalidArgument;
        }

        *outSnapshot = 0;
        return Try(() =>
        {
            SessionHandle sessionHandle = Get<SessionHandle>(session);
            EngineSnapshot snapshot = sessionHandle.Active
                ? sessionHandle.Engine.CurrentSnapshot()
                : EngineSnapshot.Empty(handled: false);
            *outSnapshot = GCHandle.ToIntPtr(GCHandle.Alloc(new SnapshotHandle(snapshot)));
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_deactivate", CallConvs = [typeof(CallConvCdecl)])]
    public static int SessionDeactivate(nint session)
    {
        if (session == 0)
        {
            return StatusInvalidArgument;
        }

        return Try(() =>
        {
            SessionHandle sessionHandle = Get<SessionHandle>(session);
            sessionHandle.Active = false;
            sessionHandle.Engine.Clear();
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_session_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void SessionRelease(nint session)
    {
        FreeHandle(session);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_handled", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetHandled(nint snapshot)
    {
        return snapshot == 0 ? 0 : Bool(Get<SnapshotHandle>(snapshot).Snapshot.Handled);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_is_composing", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetIsComposing(nint snapshot)
    {
        return snapshot == 0 ? 0 : Bool(Get<SnapshotHandle>(snapshot).Snapshot.IsComposing);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_selected_index", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetSelectedIndex(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Snapshot.SelectedIndex;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_caret", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetCaret(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Snapshot.Caret;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_candidate_total", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetCandidateTotal(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Snapshot.CandidateTotal;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_page_index", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetPageIndex(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Snapshot.PageIndex;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_page_count", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetPageCount(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Snapshot.PageCount;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_sentence_rerank_pending", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetSentenceRerankPending(nint snapshot)
    {
        return snapshot == 0 ? 0 : Bool(Get<SnapshotHandle>(snapshot).Snapshot.SentenceRerankPending);
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_action", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetAction(nint snapshot)
    {
        return snapshot == 0 ? 0 : (int)Get<SnapshotHandle>(snapshot).Snapshot.Action;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_preedit", CallConvs = [typeof(CallConvCdecl)])]
    public static TcUtf8Slice SnapshotGetPreedit(nint snapshot)
    {
        return snapshot == 0 ? default : Get<SnapshotHandle>(snapshot).Preedit;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_composition_prefix", CallConvs = [typeof(CallConvCdecl)])]
    public static TcUtf8Slice SnapshotGetCompositionPrefix(nint snapshot)
    {
        return snapshot == 0 ? default : Get<SnapshotHandle>(snapshot).CompositionPrefix;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_active_input_code", CallConvs = [typeof(CallConvCdecl)])]
    public static TcUtf8Slice SnapshotGetActiveInputCode(nint snapshot)
    {
        return snapshot == 0 ? default : Get<SnapshotHandle>(snapshot).ActiveInputCode;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_commit", CallConvs = [typeof(CallConvCdecl)])]
    public static TcUtf8Slice SnapshotGetCommit(nint snapshot)
    {
        return snapshot == 0 ? default : Get<SnapshotHandle>(snapshot).Commit;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_candidate_count", CallConvs = [typeof(CallConvCdecl)])]
    public static int SnapshotGetCandidateCount(nint snapshot)
    {
        return snapshot == 0 ? 0 : Get<SnapshotHandle>(snapshot).Candidates.Length;
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_get_candidate", CallConvs = [typeof(CallConvCdecl)])]
    public static TcUtf8Slice SnapshotGetCandidate(nint snapshot, int index)
    {
        if (snapshot == 0)
        {
            return default;
        }

        TcUtf8Slice[] candidates = Get<SnapshotHandle>(snapshot).Candidates;
        return index < 0 || index >= candidates.Length ? default : candidates[index];
    }

    [UnmanagedCallersOnly(EntryPoint = "tc_snapshot_release", CallConvs = [typeof(CallConvCdecl)])]
    public static void SnapshotRelease(nint snapshot)
    {
        if (snapshot == 0)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(snapshot);
        if (handle.Target is SnapshotHandle snapshotHandle)
        {
            snapshotHandle.Dispose();
        }

        handle.Free();
    }

    private static InputEvent ReadInputEvent(TcInputEvent* input)
    {
        return new InputEvent(
            (InputKey)input->Key,
            Utf8ToNullableString(input->LogicalText),
            input->Modifiers,
            (KeyAction)input->Action,
            Utf8ToNullableString(input->PhysicalKey),
            input->PhysicalScanCode,
            input->IsExtended != 0,
            input->IsRepeat != 0,
            input->RepeatCount);
    }

    private static int Try(Action action)
    {
        try
        {
            action();
            return StatusOk;
        }
        catch
        {
            return StatusRuntimeError;
        }
    }

    private static T Get<T>(nint handle)
        where T : class
    {
        return (T)(GCHandle.FromIntPtr(handle).Target ?? throw new InvalidOperationException("Handle target was released."));
    }

    private static void FreeHandle(nint handle)
    {
        if (handle != 0)
        {
            GCHandle managedHandle = GCHandle.FromIntPtr(handle);
            if (managedHandle.Target is IDisposable disposable)
            {
                disposable.Dispose();
            }
            managedHandle.Free();
        }
    }

    private static string Utf8ToString(TcUtf8Slice value)
    {
        if (!value.IsValid)
        {
            throw new ArgumentException("UTF-8 slice has a null pointer and non-zero length.");
        }

        if (value.Length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "UTF-8 slice is too large.");
        }

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value.Data, checked((int)value.Length)));
    }

    private static string? Utf8ToNullableString(TcUtf8Slice value)
    {
        return value.Data is null && value.Length == 0 ? null : Utf8ToString(value);
    }

    private static int Bool(bool value)
    {
        return value ? 1 : 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TcInputEvent
{
    public int Key;
    public TcUtf8Slice LogicalText;
    public int Modifiers;
    public int Action;
    public TcUtf8Slice PhysicalKey;
    public int PhysicalScanCode;
    public int IsExtended;
    public int IsRepeat;
    public int RepeatCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TcEngineConfig
{
    public int MaxCandidates;
    public int PageSize;
    public int MaxCodeLength;
    public int AutoCommitUniqueTerminalCode;
    public int SecondCandidateSemicolon;
    public int ThirdCandidateQuote;
    public int UnlimitedMixedInput;
    public int UseEnglishPunctuationInChinese;
    public int SlashOutputsDunhao;
    public int TabClearsComposition;
    public int EnterClearsComposition;
    public int PinyinReverseEnabled;
    public int ClearOnNoCode;
    public int SentenceInputEnabled;
    public int SentenceNeuralRerankEnabled;
    public int SentenceAutoCommitEnabled;
    public int SentenceOptimalCodeHighFreqLimit;
    public int SentenceAllowDuplicateSingleCharacters;
    public TcUtf8Slice UserDictionaryPath;
    public TcUtf8Slice SelectionKeys;
    public TcUtf8Slice PreviousPageKeys;
    public TcUtf8Slice NextPageKeys;
    public TcUtf8Slice PinyinLexiconPath;
    public TcUtf8Slice SentenceModelPath;
    public TcUtf8Slice SentenceQwenNativeLibraryPath;
    public TcUtf8Slice SentenceQwenModelPath;
    public TcUtf8Slice SentenceLexiconPath;
    public TcUtf8Slice SentenceFullCodeWhitelist;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TcUtf8Slice
{
    public byte* Data;
    public nuint Length;

    public readonly bool IsValid => Data is not null || Length == 0;
}

internal sealed class RuntimeHandle : IDisposable
{
    private readonly RimeLexicon _lexicon;
    private readonly PinyinLexicon _pinyinLexicon;
    private readonly BasicEngineConfig _config;
    private readonly TigerClaw.Core.SentenceLexiconIndex? _sentenceLexicon;
    private readonly TigerClaw.Core.SentenceSupplementMatcher _sentenceSupplements = TigerClaw.Core.SentenceSupplementMatcher.Empty;
    private readonly string? _sentenceModelPath;
    private readonly SentenceNeuralReranker? _sentenceReranker;

    public RuntimeHandle(string lexiconPath, string sentenceLexiconPath, RimeLexicon lexicon, RimeLexicon sentenceLexicon, PinyinLexicon pinyinLexicon, BasicEngineConfig config, string? sentenceModelPath, string? sentenceQwenNativeLibraryPath, string? sentenceQwenModelPath)
    {
        _lexicon = lexicon;
        _pinyinLexicon = pinyinLexicon;
        _config = config;
        if (config.SentenceInputEnabled && !string.IsNullOrWhiteSpace(sentenceModelPath) && File.Exists(sentenceModelPath))
        {
            _sentenceLexicon = TigerClaw.Core.SentenceLexiconIndex.Build(
                sentenceLexicon.SentenceSource,
                TigerClaw.Core.SentenceCharacterRanks.TakeTop(config.SentenceOptimalCodeHighFreqLimit),
                ParseTextElements(config.SentenceFullCodeWhitelist));
            _sentenceSupplements = SentenceSupplementLoader.LoadForLexicon(sentenceLexiconPath);
            _sentenceModelPath = sentenceModelPath;
            if (config.SentenceNeuralRerankEnabled &&
                !string.IsNullOrWhiteSpace(sentenceQwenNativeLibraryPath) &&
                !string.IsNullOrWhiteSpace(sentenceQwenModelPath))
            {
                _sentenceReranker = new SentenceNeuralReranker(sentenceQwenNativeLibraryPath, sentenceQwenModelPath);
            }
        }
    }

    public BasicEngine CreateEngine() => new(_lexicon, _pinyinLexicon, _config, _sentenceLexicon, _sentenceSupplements, _sentenceModelPath, _sentenceReranker);

    public void Dispose() => _sentenceReranker?.Dispose();

    private static ISet<string> ParseTextElements(string raw)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return values;
        }

        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(raw.Trim());
        while (enumerator.MoveNext())
        {
            string text = enumerator.GetTextElement();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }
}

internal sealed class SessionHandle(BasicEngine engine) : IDisposable
{
    public BasicEngine Engine { get; } = engine;
    public bool Active { get; set; }

    public void Dispose() => Engine.Dispose();
}

internal sealed unsafe class SnapshotHandle : IDisposable
{
    public SnapshotHandle(EngineSnapshot snapshot)
    {
        Snapshot = snapshot;
        Preedit = StringToUtf8(snapshot.Preedit);
        CompositionPrefix = StringToUtf8(snapshot.CompositionPrefix);
        ActiveInputCode = StringToUtf8(snapshot.ActiveInputCode);
        Commit = snapshot.Commit is null ? default : StringToUtf8(snapshot.Commit);
        Candidates = new TcUtf8Slice[snapshot.Candidates.Count];
        for (int i = 0; i < snapshot.Candidates.Count; i++)
        {
            Candidates[i] = StringToUtf8(snapshot.Candidates[i]);
        }
    }

    public EngineSnapshot Snapshot { get; }
    public TcUtf8Slice Preedit { get; private set; }
    public TcUtf8Slice CompositionPrefix { get; private set; }
    public TcUtf8Slice ActiveInputCode { get; private set; }
    public TcUtf8Slice Commit { get; private set; }
    public TcUtf8Slice[] Candidates { get; }

    public void Dispose()
    {
        Free(Preedit);
        Preedit = default;
        Free(CompositionPrefix);
        CompositionPrefix = default;
        Free(ActiveInputCode);
        ActiveInputCode = default;
        Free(Commit);
        Commit = default;

        for (int i = 0; i < Candidates.Length; i++)
        {
            Free(Candidates[i]);
            Candidates[i] = default;
        }
    }

    private static TcUtf8Slice StringToUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length == 0)
        {
            return default;
        }

        byte* buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        return new TcUtf8Slice
        {
            Data = buffer,
            Length = (nuint)bytes.Length,
        };
    }

    private static void Free(TcUtf8Slice value)
    {
        if (value.Data is not null)
        {
            NativeMemory.Free(value.Data);
        }
    }
}
