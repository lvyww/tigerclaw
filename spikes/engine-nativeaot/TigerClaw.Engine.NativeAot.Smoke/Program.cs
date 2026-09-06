using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string lexiconPath = Path.Combine(repoRoot, "rime", "tiger_sentence", "tiger_sentence.codes.txt");
string libraryPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        repoRoot,
        "spikes",
        "engine-nativeaot",
        "TigerClaw.Engine.NativeAot",
        "bin",
        "Release",
        "net10.0",
        "osx-arm64",
        "publish",
        "TigerClaw.Engine.NativeAot.dylib");

nint library = NativeLibrary.Load(libraryPath);
Api api = Api.Load(library);
FileInfo libraryInfo = new(libraryPath);

nint runtime = 0;
nint sessionA = 0;
nint sessionB = 0;
nint afterA = 0;
nint afterASecond = 0;
nint afterB = 0;
nint afterBCommit = 0;
nint afterPunctuationStart = 0;
nint afterComma = 0;
nint afterBackspaceStart = 0;
nint afterBackspace = 0;
nint afterIdleComma = 0;
nint afterIdleDigit = 0;
nint afterDecimalPoint = 0;
nint afterFullWidthPeriod = 0;
nint afterShiftDigitStart = 0;
nint afterShiftDigit = 0;
nint afterAe = 0;
nint afterAePage2 = 0;
nint afterAePick = 0;

using SliceBuffer lexiconUtf8 = SliceBuffer.WithIgnoredSuffix(lexiconPath, ".not-part-of-path");
TcEngineConfig config = new()
{
    MaxCandidates = 9,
    PageSize = 2,
    MaxCodeLength = 4,
    AutoCommitUniqueTerminalCode = 1,
    SecondCandidateSemicolon = 1,
    ThirdCandidateQuote = 1,
    UnlimitedMixedInput = 0,
};

try
{
    long workingSetAfterLoad = Environment.WorkingSet;
    Stopwatch runtimeCreateWatch = Stopwatch.StartNew();
    Assert(api.RuntimeCreateWithConfig(lexiconUtf8.Slice, ref config, out runtime) == 0, "runtime create succeeds with ptr+len lexicon path and basic config");
    runtimeCreateWatch.Stop();
    long workingSetAfterRuntime = Environment.WorkingSet;

    Assert(runtime != 0, "runtime handle is opaque and non-null");
    Assert(api.SessionCreate(runtime, out sessionA) == 0, "session A create succeeds");
    Assert(api.SessionCreate(runtime, out sessionB) == 0, "session B create succeeds");
    Assert(sessionA != 0, "session A handle is opaque and non-null");
    Assert(sessionB != 0, "session B handle is opaque and non-null");
    Assert(api.SessionActivate(sessionA) == 0, "session A activate succeeds");
    Assert(api.SessionActivate(sessionB) == 0, "session B activate succeeds");

    ProcessKey(sessionA, 1, "a", "KeyA", 0, out afterA);
    Assert(api.SnapshotGetHandled(afterA) == 1, "a is handled");
    Assert(api.SnapshotGetIsComposing(afterA) == 1, "a starts composition");
    TcUtf8Slice preedit = api.SnapshotGetPreedit(afterA);
    Assert(preedit.Length == 1, "preedit length is exactly one byte");
    Assert(Utf8(preedit) == "a", "preedit is a");
    Assert(api.SnapshotGetCandidateCount(afterA) >= 1, "candidate list is available");
    TcUtf8Slice firstCandidate = api.SnapshotGetCandidate(afterA, 0);
    Assert(firstCandidate.Length == (nuint)Encoding.UTF8.GetByteCount("来"), "candidate length is exact UTF-8 byte count");
    Assert(Utf8(firstCandidate) == "来", "real Rime candidate a -> 来");
    Assert(api.SnapshotGetSelectedIndex(afterA) == 0, "selected candidate starts at zero");
    Assert(api.SnapshotGetCandidateTotal(afterA) == 2, "Rime selection suffixes produce two candidates for a");
    Assert(api.SnapshotGetPageIndex(afterA) == 0 && api.SnapshotGetPageCount(afterA) == 1, "candidate page metadata is available");
    Assert(api.SnapshotGetCaret(afterA) == 1, "preedit caret tracks the code buffer");

    ProcessKey(sessionA, 11, "2", "Digit2", 19, out afterASecond);
    Assert(Utf8(api.SnapshotGetCommit(afterASecond)) == "那个", "digit selects the second real candidate");
    Assert(api.SnapshotGetIsComposing(afterASecond) == 0, "digit selection clears composition");

    ProcessKey(sessionB, 1, "b", "KeyB", 0, out afterB);
    Assert(api.SnapshotGetHandled(afterB) == 1, "b is handled in session B");
    Assert(api.SnapshotGetIsComposing(afterB) == 1, "b starts composition in session B");
    Assert(Utf8(api.SnapshotGetPreedit(afterB)) == "b", "session B preedit is independent");
    Assert(Utf8(api.SnapshotGetCandidate(afterB, 0)) == "如", "real Rime candidate b -> 如");
    Assert(Utf8(api.SnapshotGetPreedit(afterA)) == "a", "session A snapshot remains immutable after B input");

    Assert(api.SessionDeactivate(sessionA) == 0, "session A deactivate succeeds");
    api.SessionRelease(sessionA);
    sessionA = 0;

    ProcessKey(sessionB, 5, " ", "Space", 49, out afterBCommit);
    Assert(api.SnapshotGetHandled(afterBCommit) == 1, "session B Space is handled after session A release");
    TcUtf8Slice commit = api.SnapshotGetCommit(afterBCommit);
    Assert(commit.Length == (nuint)Encoding.UTF8.GetByteCount("如"), "session B commit length is exact UTF-8 byte count");
    Assert(Utf8(commit) == "如", "session B commits 如 after session A release");
    Assert(api.SnapshotGetIsComposing(afterBCommit) == 0, "session B Space clears composition");

    ProcessKey(sessionB, 1, "a", "KeyA", 0, out afterPunctuationStart);
    ProcessKey(sessionB, 1, ",", "Comma", 43, out afterComma);
    Assert(api.SnapshotGetHandled(afterComma) == 1, "punctuation after a candidate is handled by the engine");
    Assert(Utf8(api.SnapshotGetCommit(afterComma)) == "来，", "comma commits the primary candidate followed by a Chinese comma");
    Assert(api.SnapshotGetIsComposing(afterComma) == 0, "punctuation commit clears composition");

    ProcessKey(sessionB, 1, "a", "KeyA", 0, out afterBackspaceStart);
    ProcessKey(sessionB, 2, "", "Backspace", 51, out afterBackspace);
    Assert(api.SnapshotGetHandled(afterBackspace) == 1, "backspace clearing the last code key is handled");
    Assert(Utf8(api.SnapshotGetCommit(afterBackspace)) == "", "backspace clearing the last code key has no commit text");
    Assert(api.SnapshotGetIsComposing(afterBackspace) == 0, "backspace clearing the last code key ends composition");

    ProcessKey(sessionB, 1, ",", "Comma", 43, out afterIdleComma);
    Assert(api.SnapshotGetHandled(afterIdleComma) == 1, "idle punctuation is handled by the engine");
    Assert(Utf8(api.SnapshotGetCommit(afterIdleComma)) == "，", "idle comma becomes a Chinese comma");
    Assert(api.SnapshotGetIsComposing(afterIdleComma) == 0, "idle punctuation does not start a composition");

    ProcessKey(sessionB, 11, "2", "Digit2", 19, out afterIdleDigit);
    Assert(api.SnapshotGetHandled(afterIdleDigit) == 0, "idle digit passes through to the client");
    ProcessKey(sessionB, 1, ".", "Period", 47, out afterDecimalPoint);
    Assert(api.SnapshotGetHandled(afterDecimalPoint) == 1, "period after a pass-through digit is handled");
    Assert(Utf8(api.SnapshotGetCommit(afterDecimalPoint)) == ".", "period after a digit stays half-width for decimal input");
    ProcessKey(sessionB, 1, ".", "Period", 47, out afterFullWidthPeriod);
    Assert(Utf8(api.SnapshotGetCommit(afterFullWidthPeriod)) == "。", "the decimal-period exception is consumed after one period");

    ProcessKey(sessionB, 1, "a", "KeyA", 0, out afterShiftDigitStart);
    ProcessKey(sessionB, 11, "1", "Digit1", 18, out afterShiftDigit, modifiers: 1);
    Assert(api.SnapshotGetHandled(afterShiftDigit) == 1, "shift-digit after a candidate is handled by the engine");
    Assert(Utf8(api.SnapshotGetCommit(afterShiftDigit)) == "来！", "shift-digit commits the primary candidate followed by its Chinese symbol");
    Assert(api.SnapshotGetIsComposing(afterShiftDigit) == 0, "shift-digit punctuation clears composition");

    ProcessKey(sessionB, 1, "a", "KeyA", 0, out nint afterAeStart);
    try
    {
        ProcessKey(sessionB, 1, "e", "KeyE", 14, out afterAe);
    }
    finally
    {
        api.SnapshotRelease(afterAeStart);
    }
    Assert(api.SnapshotGetCandidateTotal(afterAe) == 4, "real code ae retains all four ranked candidates");
    Assert(Utf8(api.SnapshotGetCandidate(afterAe, 0)) == "闲" && Utf8(api.SnapshotGetCandidate(afterAe, 1)) == "乛", "configured first page is visible");
    ProcessKey(sessionB, 15, "", "Equal", 24, out afterAePage2);
    Assert(api.SnapshotGetPageIndex(afterAePage2) == 1 && api.SnapshotGetPageCount(afterAePage2) == 2, "next-page key advances page metadata");
    Assert(Utf8(api.SnapshotGetCandidate(afterAePage2, 0)) == "乚", "next-page key exposes the later candidate");
    ProcessKey(sessionB, 11, "1", "Digit1", 18, out afterAePick);
    Assert(Utf8(api.SnapshotGetCommit(afterAePick)) == "乚", "digit chooses the candidate from the visible page");

    VerifyMixedInput(api, lexiconUtf8.Slice);
    VerifyUserDictionary(api, lexiconUtf8.Slice);
    VerifyBasicSettings(api, lexiconUtf8.Slice);
    VerifyQuickSymbols(api);
    VerifyRimeImportedTables(api);

    double steadyKeyCallMicroseconds = MeasureSteadyKeyCallMicroseconds(sessionB);
    long workingSetAfterSmoke = Environment.WorkingSet;

    Console.WriteLine("ENGINE_NATIVEAOT_PASS");
    Console.WriteLine($"Library={Path.GetRelativePath(repoRoot, libraryPath)}");
    Console.WriteLine($"Dictionary={Path.GetRelativePath(repoRoot, lexiconPath)}");
    Console.WriteLine("ABI=UTF-8 ptr+len slices");
    Console.WriteLine("Scenario=A:a -> 来, B:b -> 如, release A, B:Space -> 如");
    Console.WriteLine($"DylibSizeBytes={libraryInfo.Length}");
    Console.WriteLine($"RuntimeInitMilliseconds={runtimeCreateWatch.Elapsed.TotalMilliseconds:F3}");
    Console.WriteLine($"SteadyKeyCallMicroseconds={steadyKeyCallMicroseconds:F3}");
    Console.WriteLine($"MemoryBaselineBytes=after-load:{workingSetAfterLoad}, after-runtime:{workingSetAfterRuntime}, after-smoke:{workingSetAfterSmoke}");
    Console.WriteLine("MeasurementBoundary=macOS arm64 process working set; dylib loaded in-process; no IMK host, TSF bridge, Overlay, or Sentence sidecar");
}
finally
{
    if (afterA != 0)
    {
        api.SnapshotRelease(afterA);
    }

    if (afterASecond != 0)
    {
        api.SnapshotRelease(afterASecond);
    }

    if (afterB != 0)
    {
        api.SnapshotRelease(afterB);
    }

    if (afterBCommit != 0)
    {
        api.SnapshotRelease(afterBCommit);
    }

    if (afterPunctuationStart != 0)
    {
        api.SnapshotRelease(afterPunctuationStart);
    }

    if (afterComma != 0)
    {
        api.SnapshotRelease(afterComma);
    }

    if (afterBackspaceStart != 0)
    {
        api.SnapshotRelease(afterBackspaceStart);
    }

    if (afterBackspace != 0)
    {
        api.SnapshotRelease(afterBackspace);
    }

    if (afterIdleComma != 0)
    {
        api.SnapshotRelease(afterIdleComma);
    }

    if (afterIdleDigit != 0)
    {
        api.SnapshotRelease(afterIdleDigit);
    }

    if (afterDecimalPoint != 0)
    {
        api.SnapshotRelease(afterDecimalPoint);
    }

    if (afterFullWidthPeriod != 0)
    {
        api.SnapshotRelease(afterFullWidthPeriod);
    }

    if (afterShiftDigitStart != 0)
    {
        api.SnapshotRelease(afterShiftDigitStart);
    }

    if (afterShiftDigit != 0)
    {
        api.SnapshotRelease(afterShiftDigit);
    }

    if (afterAe != 0)
    {
        api.SnapshotRelease(afterAe);
    }

    if (afterAePage2 != 0)
    {
        api.SnapshotRelease(afterAePage2);
    }

    if (afterAePick != 0)
    {
        api.SnapshotRelease(afterAePick);
    }

    if (sessionA != 0)
    {
        api.SessionDeactivate(sessionA);
        api.SessionRelease(sessionA);
    }

    if (sessionB != 0)
    {
        api.SessionDeactivate(sessionB);
        api.SessionRelease(sessionB);
    }

    if (runtime != 0)
    {
        api.RuntimeRelease(runtime);
    }

    NativeLibrary.Free(library);
}

void ProcessKey(nint session, int key, string logicalText, string physicalKey, int physicalScanCode, out nint snapshot, int modifiers = 0)
{
    using SliceBuffer logical = SliceBuffer.WithIgnoredSuffix(logicalText, ".ignored");
    using SliceBuffer physical = SliceBuffer.WithIgnoredSuffix(physicalKey, ".ignored");

    TcInputEvent input = new()
    {
        Key = key,
        LogicalText = logical.Slice,
        Modifiers = modifiers,
        Action = 0,
        PhysicalKey = physical.Slice,
        PhysicalScanCode = physicalScanCode,
        IsExtended = 0,
        IsRepeat = 0,
        RepeatCount = 1,
    };

    Assert(api.SessionProcess(session, ref input, out snapshot) == 0, $"process {logicalText} succeeds with ptr+len input strings");
    Assert(snapshot != 0, $"process {logicalText} returns a snapshot");
}

void VerifyMixedInput(Api api, TcUtf8Slice lexiconPath)
{
    TcEngineConfig mixedConfig = new()
    {
        MaxCandidates = 9,
        PageSize = 5,
        MaxCodeLength = 1,
        AutoCommitUniqueTerminalCode = 0,
        SecondCandidateSemicolon = 1,
        ThirdCandidateQuote = 1,
        UnlimitedMixedInput = 1,
    };
    nint mixedRuntime = 0;
    nint mixedSession = 0;
    nint afterA = 0;
    nint afterUpperA = 0;
    nint afterCommit = 0;
    nint pageHintStart = 0;
    nint pageHintPage2 = 0;
    nint pageHintSealed = 0;
    nint crossedSegment = 0;
    nint rolledBack = 0;
    nint literalEnglish = 0;
    nint rawCommit = 0;
    nint noCandidateSpaceCommit = 0;
    nint afterReactivation = 0;
    try
    {
        Assert(api.RuntimeCreateWithConfig(lexiconPath, ref mixedConfig, out mixedRuntime) == 0, "mixed runtime create succeeds");
        Assert(api.SessionCreate(mixedRuntime, out mixedSession) == 0 && api.SessionActivate(mixedSession) == 0, "mixed session activates");
        ProcessKey(mixedSession, 1, "a", "KeyA", 0, out afterA);
        ProcessKey(mixedSession, 1, "A", "KeyA", 0, out afterUpperA);
        Assert(Utf8(api.SnapshotGetPreedit(afterUpperA)) == "来A", "mixed preedit keeps resolved prefix and raw-case active tail");
        Assert(Utf8(api.SnapshotGetCompositionPrefix(afterUpperA)) == "来", "mixed snapshot exposes the resolved prefix");
        Assert(Utf8(api.SnapshotGetActiveInputCode(afterUpperA)) == "A", "mixed snapshot exposes the raw-case active code");
        ProcessKey(mixedSession, 5, " ", "Space", 49, out afterCommit);
        Assert(Utf8(api.SnapshotGetCommit(afterCommit)) == "来来", "mixed space commits resolved prefix plus active candidate");

        api.SessionDeactivate(mixedSession);
        api.SessionRelease(mixedSession);
        api.RuntimeRelease(mixedRuntime);
        mixedSession = 0;
        mixedRuntime = 0;

        mixedConfig.MaxCodeLength = 2;
        mixedConfig.PageSize = 2;
        Assert(api.RuntimeCreateWithConfig(lexiconPath, ref mixedConfig, out mixedRuntime) == 0, "max-two mixed runtime create succeeds");
        Assert(api.SessionCreate(mixedRuntime, out mixedSession) == 0 && api.SessionActivate(mixedSession) == 0, "max-two mixed session activates");
        ProcessAndRelease(mixedSession, 1, "a", "KeyA", 0);
        ProcessKey(mixedSession, 1, "e", "KeyE", 14, out pageHintStart);
        Assert(api.SnapshotGetCandidateTotal(pageHintStart) == 4, "mixed active segment exposes every candidate before sealing");
        ProcessKey(mixedSession, 15, "", "Equal", 24, out pageHintPage2);
        Assert(Utf8(api.SnapshotGetCandidate(pageHintPage2, 0)) == "乚", "mixed active segment can move to the second candidate page");
        ProcessKey(mixedSession, 1, "b", "KeyB", 11, out pageHintSealed);
        Assert(Utf8(api.SnapshotGetPreedit(pageHintSealed)) == "乚b", "mixed input seals the current page's first candidate as the resolved prefix");
        Assert(Utf8(api.SnapshotGetCompositionPrefix(pageHintSealed)) == "乚", "mixed page preference is exposed through the resolved prefix");

        api.SessionDeactivate(mixedSession);
        api.SessionRelease(mixedSession);
        api.RuntimeRelease(mixedRuntime);
        mixedSession = 0;
        mixedRuntime = 0;

        mixedConfig.MaxCodeLength = 4;
        mixedConfig.PageSize = 5;
        Assert(api.RuntimeCreateWithConfig(lexiconPath, ref mixedConfig, out mixedRuntime) == 0, "max-four mixed runtime create succeeds");
        Assert(api.SessionCreate(mixedRuntime, out mixedSession) == 0 && api.SessionActivate(mixedSession) == 0, "max-four mixed session activates");
        foreach (char value in "aaaa") ProcessAndRelease(mixedSession, 1, value.ToString(), "KeyA", 0);
        ProcessKey(mixedSession, 1, "Q", "KeyQ", 12, out crossedSegment);
        Assert(Utf8(api.SnapshotGetPreedit(crossedSegment)) == "卍Q", "mixed input resolves a completed real-code prefix only after the next key");
        ProcessKey(mixedSession, 2, "", "Backspace", 51, out rolledBack);
        Assert(Utf8(api.SnapshotGetPreedit(rolledBack)) == "aaaa", "mixed backspace restores authoritative raw code across a segment boundary");
        ProcessAndRelease(mixedSession, 4, "", "Escape", 53);

        foreach (char value in "qqqq") ProcessAndRelease(mixedSession, 1, value.ToString(), "KeyQ", 12);
        ProcessKey(mixedSession, 1, "A", "KeyA", 0, out literalEnglish);
        Assert(Utf8(api.SnapshotGetPreedit(literalEnglish)) == "qqqqA", "completed segment without candidates remains literal English with casing preserved");
        ProcessAndRelease(mixedSession, 4, "", "Escape", 53);

        foreach (char value in "aaAaQ") ProcessAndRelease(mixedSession, 1, value.ToString(), $"Key{char.ToUpperInvariant(value)}", 0);
        ProcessKey(mixedSession, 3, "\r", "Enter", 36, out rawCommit);
        Assert(api.SnapshotGetHandled(rawCommit) == 1, "mixed Enter is handled");
        Assert(Utf8(api.SnapshotGetCommit(rawCommit)) == "aaAaQ", "mixed Enter commits the authoritative raw input with casing preserved");
        Assert(api.SnapshotGetIsComposing(rawCommit) == 0, "mixed Enter clears composition");

        foreach (char value in "qqqqqzz") ProcessAndRelease(mixedSession, 1, value.ToString(), $"Key{char.ToUpperInvariant(value)}", 0);
        ProcessKey(mixedSession, 5, " ", "Space", 49, out noCandidateSpaceCommit);
        Assert(api.SnapshotGetHandled(noCandidateSpaceCommit) == 1, "mixed Space is handled when the active tail has no candidate");
        Assert(Utf8(api.SnapshotGetCommit(noCandidateSpaceCommit)) == "qqqq", "mixed Space commits only the literal resolved prefix when the active tail has no candidate");
        Assert(api.SnapshotGetIsComposing(noCandidateSpaceCommit) == 0, "mixed Space clears a candidate-less active tail");

        foreach (char value in "aaaa") ProcessAndRelease(mixedSession, 1, value.ToString(), "KeyA", 0);
        ProcessAndRelease(mixedSession, 1, "B", "KeyB", 11);
        Assert(api.SessionDeactivate(mixedSession) == 0 && api.SessionActivate(mixedSession) == 0, "mixed session reactivates after clearing composition");
        ProcessKey(mixedSession, 1, "b", "KeyB", 11, out afterReactivation);
        Assert(Utf8(api.SnapshotGetPreedit(afterReactivation)) == "b", "deactivation does not leak a resolved mixed prefix into the next activation");
    }
    finally
    {
        if (afterA != 0) api.SnapshotRelease(afterA);
        if (afterUpperA != 0) api.SnapshotRelease(afterUpperA);
        if (afterCommit != 0) api.SnapshotRelease(afterCommit);
        if (pageHintStart != 0) api.SnapshotRelease(pageHintStart);
        if (pageHintPage2 != 0) api.SnapshotRelease(pageHintPage2);
        if (pageHintSealed != 0) api.SnapshotRelease(pageHintSealed);
        if (crossedSegment != 0) api.SnapshotRelease(crossedSegment);
        if (rolledBack != 0) api.SnapshotRelease(rolledBack);
        if (literalEnglish != 0) api.SnapshotRelease(literalEnglish);
        if (rawCommit != 0) api.SnapshotRelease(rawCommit);
        if (noCandidateSpaceCommit != 0) api.SnapshotRelease(noCandidateSpaceCommit);
        if (afterReactivation != 0) api.SnapshotRelease(afterReactivation);
        if (mixedSession != 0) { api.SessionDeactivate(mixedSession); api.SessionRelease(mixedSession); }
        if (mixedRuntime != 0) api.RuntimeRelease(mixedRuntime);
    }
}

void VerifyUserDictionary(Api api, TcUtf8Slice lexiconPath)
{
    string userDictionaryPath = Path.Combine(Path.GetTempPath(), $"tigerclaw-nativeaot-user-{Guid.NewGuid():N}.tsv");
    nint userRuntime = 0;
    nint userSession = 0;
    nint afterA = 0;
    nint afterCommit = 0;
    try
    {
        File.WriteAllText(userDictionaryPath, "自定义\ta\nignored\t12\n", Encoding.UTF8);
        using SliceBuffer userDictionaryUtf8 = SliceBuffer.WithIgnoredSuffix(userDictionaryPath, ".ignored");
        TcEngineConfig userConfig = new()
        {
            MaxCandidates = 9,
            PageSize = 5,
            MaxCodeLength = 4,
            AutoCommitUniqueTerminalCode = 1,
            SecondCandidateSemicolon = 1,
            ThirdCandidateQuote = 1,
            UnlimitedMixedInput = 0,
            UserDictionaryPath = userDictionaryUtf8.Slice,
        };

        Assert(api.RuntimeCreateWithConfig(lexiconPath, ref userConfig, out userRuntime) == 0, "user-dictionary runtime create succeeds");
        Assert(api.SessionCreate(userRuntime, out userSession) == 0 && api.SessionActivate(userSession) == 0, "user-dictionary session activates");
        ProcessKey(userSession, 1, "a", "KeyA", 0, out afterA);
        Assert(Utf8(api.SnapshotGetCandidate(afterA, 0)) == "自定义", "user dictionary candidate is promoted ahead of the bundled lexicon");
        Assert(api.SnapshotGetCandidateTotal(afterA) == 3, "user dictionary preserves bundled alternatives after the promoted candidate");
        ProcessKey(userSession, 5, " ", "Space", 49, out afterCommit);
        Assert(Utf8(api.SnapshotGetCommit(afterCommit)) == "自定义", "user dictionary candidate commits through the normal space path");
    }
    finally
    {
        if (afterA != 0) api.SnapshotRelease(afterA);
        if (afterCommit != 0) api.SnapshotRelease(afterCommit);
        if (userSession != 0) { api.SessionDeactivate(userSession); api.SessionRelease(userSession); }
        if (userRuntime != 0) api.RuntimeRelease(userRuntime);
        if (File.Exists(userDictionaryPath)) File.Delete(userDictionaryPath);
    }
}

void VerifyRimeImportedTables(Api api)
{
    string directory = Path.Combine(Path.GetTempPath(), $"tigerclaw-nativeaot-import-{Guid.NewGuid():N}");
    string mainPath = Path.Combine(directory, "main.dict.yaml");
    string importedPath = Path.Combine(directory, "words.dict.yaml");
    nint importedRuntime = 0;
    nint importedSession = 0;
    nint afterMain = 0;
    nint afterImported = 0;
    nint afterQuickSymbol = 0;
    nint afterQuickCandidates = 0;
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(mainPath, """
name: main
columns:
  - text
  - weight
  - code
import_tables:
  - words
...
低权	1	aa
高权	10	aa
""", Encoding.UTF8);
        File.WriteAllText(importedPath, """
name: words
columns:
  - text
  - weight
  - code
...
导入词	100	bb
""", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(directory, "快符.txt"),
            "！\t;a\n甲\t[b\n乙\t[b;\n",
            Encoding.UTF8);

        using SliceBuffer importedLexiconUtf8 = SliceBuffer.WithIgnoredSuffix(mainPath, ".ignored");
        TcEngineConfig config = new()
        {
            MaxCandidates = 9,
            PageSize = 5,
            MaxCodeLength = 4,
            AutoCommitUniqueTerminalCode = 0,
            SecondCandidateSemicolon = 1,
            ThirdCandidateQuote = 1,
            UnlimitedMixedInput = 1,
        };
        Assert(api.RuntimeCreateWithConfig(importedLexiconUtf8.Slice, ref config, out importedRuntime) == 0, "Rime dictionary with import_tables creates a runtime");
        Assert(api.SessionCreate(importedRuntime, out importedSession) == 0 && api.SessionActivate(importedSession) == 0, "Rime imported-table session activates");
        ProcessAndRelease(importedSession, 1, "a", "KeyA", 0);
        ProcessKey(importedSession, 1, "a", "KeyA", 0, out afterMain);
        Assert(Utf8(api.SnapshotGetCandidate(afterMain, 0)) == "高权", "Rime columns place the code column correctly and rank candidates by weight");
        ProcessAndRelease(importedSession, 4, "", "Escape", 53);
        ProcessAndRelease(importedSession, 1, "b", "KeyB", 11);
        ProcessKey(importedSession, 1, "b", "KeyB", 11, out afterImported);
        Assert(Utf8(api.SnapshotGetCandidate(afterImported, 0)) == "导入词", "Rime import_tables contributes word candidates");
        ProcessAndRelease(importedSession, 4, "", "Escape", 53);
        ProcessAndRelease(importedSession, 1, ";", "Semicolon", 41);
        ProcessKey(importedSession, 1, "a", "KeyA", 0, out afterQuickSymbol);
        Assert(Utf8(api.SnapshotGetCommit(afterQuickSymbol)) == "！", "Rime companion 快符.txt contributes quick symbols");
        ProcessAndRelease(importedSession, 4, "", "Escape", 53);
        ProcessAndRelease(importedSession, 1, "[", "BracketLeft", 33);
        ProcessKey(importedSession, 1, "b", "KeyB", 11, out afterQuickCandidates);
        Assert(api.SnapshotGetCandidateTotal(afterQuickCandidates) == 2 &&
               Utf8(api.SnapshotGetCandidate(afterQuickCandidates, 0)) == "甲" &&
               Utf8(api.SnapshotGetCandidate(afterQuickCandidates, 1)) == "乙",
            "Rime companion 快符.txt retains rank-suffixed multi-candidate quick codes");
    }
    finally
    {
        if (afterMain != 0) api.SnapshotRelease(afterMain);
        if (afterImported != 0) api.SnapshotRelease(afterImported);
        if (afterQuickSymbol != 0) api.SnapshotRelease(afterQuickSymbol);
        if (afterQuickCandidates != 0) api.SnapshotRelease(afterQuickCandidates);
        if (importedSession != 0) { api.SessionDeactivate(importedSession); api.SessionRelease(importedSession); }
        if (importedRuntime != 0) api.RuntimeRelease(importedRuntime);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

void VerifyBasicSettings(Api api, TcUtf8Slice lexiconPath)
{
    using SliceBuffer selectionKeys = SliceBuffer.WithIgnoredSuffix("as", ".ignored");
    using SliceBuffer previousPageKeys = SliceBuffer.WithIgnoredSuffix("[", ".ignored");
    using SliceBuffer nextPageKeys = SliceBuffer.WithIgnoredSuffix("]", ".ignored");
    TcEngineConfig config = new()
    {
        MaxCandidates = 9,
        PageSize = 5,
        MaxCodeLength = 4,
        AutoCommitUniqueTerminalCode = 1,
        SecondCandidateSemicolon = 1,
        ThirdCandidateQuote = 1,
        UnlimitedMixedInput = 0,
        UseEnglishPunctuationInChinese = 1,
        SlashOutputsDunhao = 0,
        TabClearsComposition = 1,
        EnterClearsComposition = 1,
        SelectionKeys = selectionKeys.Slice,
        PreviousPageKeys = previousPageKeys.Slice,
        NextPageKeys = nextPageKeys.Slice,
    };
    nint runtime = 0;
    nint session = 0;
    nint punctuation = 0;
    nint afterA = 0;
    nint tab = 0;
    nint enter = 0;
    nint customSelection = 0;
    try
    {
        Assert(api.RuntimeCreateWithConfig(lexiconPath, ref config, out runtime) == 0, "basic settings runtime creates");
        Assert(api.SessionCreate(runtime, out session) == 0 && api.SessionActivate(session) == 0, "basic settings session activates");
        ProcessKey(session, 1, ",", "Comma", 43, out punctuation);
        Assert(Utf8(api.SnapshotGetCommit(punctuation)) == ",", "English-punctuation setting changes idle comma output");
        ProcessKey(session, 1, "a", "KeyA", 0, out afterA);
        ProcessKey(session, 1, "s", "KeyS", 1, out customSelection);
        Assert(Utf8(api.SnapshotGetCommit(customSelection)) == "那个", "custom candidate key selects the configured second candidate");
        api.SnapshotRelease(afterA); afterA = 0;
        api.SnapshotRelease(customSelection); customSelection = 0;
        ProcessKey(session, 1, "a", "KeyA", 0, out afterA);
        ProcessKey(session, 6, "", "Tab", 48, out tab);
        Assert(api.SnapshotGetHandled(tab) == 1 && api.SnapshotGetIsComposing(tab) == 0, "Tab-clear setting clears composition");
        api.SnapshotRelease(afterA); afterA = 0;
        ProcessKey(session, 1, "a", "KeyA", 0, out afterA);
        ProcessKey(session, 3, "", "Enter", 36, out enter);
        Assert(api.SnapshotGetHandled(enter) == 1 && Utf8(api.SnapshotGetCommit(enter)) == "", "Enter-clear setting clears without committing raw code");
    }
    finally
    {
        if (punctuation != 0) api.SnapshotRelease(punctuation);
        if (afterA != 0) api.SnapshotRelease(afterA);
        if (tab != 0) api.SnapshotRelease(tab);
        if (enter != 0) api.SnapshotRelease(enter);
        if (customSelection != 0) api.SnapshotRelease(customSelection);
        if (session != 0) { api.SessionDeactivate(session); api.SessionRelease(session); }
        if (runtime != 0) api.RuntimeRelease(runtime);
    }
}

void VerifyQuickSymbols(Api api)
{
    string path = Path.Combine(Path.GetTempPath(), $"tigerclaw-nativeaot-quick-{Guid.NewGuid():N}.dict.yaml");
    nint runtime = 0;
    nint session = 0;
    nint start = 0;
    nint commit = 0;
    nint addWordAction = 0;
    nint hideCandidateAction = 0;
    try
    {
        File.WriteAllText(path, "---\nname: quick\n...\n★\t;a\n{添加}\t;b\n{隐藏候选}\t;c\n", Encoding.UTF8);
        using SliceBuffer lexicon = SliceBuffer.WithIgnoredSuffix(path, ".ignored");
        TcEngineConfig config = new()
        {
            MaxCandidates = 9,
            PageSize = 5,
            MaxCodeLength = 4,
            AutoCommitUniqueTerminalCode = 1,
            SecondCandidateSemicolon = 1,
            ThirdCandidateQuote = 1,
            SlashOutputsDunhao = 1,
            TabClearsComposition = 1,
        };
        Assert(api.RuntimeCreateWithConfig(lexicon.Slice, ref config, out runtime) == 0, "quick-symbol runtime creates");
        Assert(api.SessionCreate(runtime, out session) == 0 && api.SessionActivate(session) == 0, "quick-symbol session activates");
        ProcessKey(session, 12, ";", "Semicolon", 41, out start);
        Assert(Utf8(api.SnapshotGetPreedit(start)) == ";", "quick-symbol prefix starts a composition");
        ProcessKey(session, 1, "a", "KeyA", 0, out commit);
        Assert(Utf8(api.SnapshotGetCommit(commit)) == "★" && api.SnapshotGetIsComposing(commit) == 0, "unique quick-symbol code commits immediately");
        ProcessAndRelease(session, 12, ";", "Semicolon", 41);
        ProcessKey(session, 1, "b", "KeyB", 11, out addWordAction);
        Assert(api.SnapshotGetAction(addWordAction) == 1 && Utf8(api.SnapshotGetCommit(addWordAction)) == "", "{添加} emits the add-word host action without literal text");
        ProcessAndRelease(session, 12, ";", "Semicolon", 41);
        ProcessKey(session, 1, "c", "KeyC", 8, out hideCandidateAction);
        Assert(api.SnapshotGetAction(hideCandidateAction) == 2 && Utf8(api.SnapshotGetCommit(hideCandidateAction)) == "", "{隐藏候选} emits the visibility host action without literal text");
    }
    finally
    {
        if (start != 0) api.SnapshotRelease(start);
        if (commit != 0) api.SnapshotRelease(commit);
        if (addWordAction != 0) api.SnapshotRelease(addWordAction);
        if (hideCandidateAction != 0) api.SnapshotRelease(hideCandidateAction);
        if (session != 0) { api.SessionDeactivate(session); api.SessionRelease(session); }
        if (runtime != 0) api.RuntimeRelease(runtime);
        if (File.Exists(path)) File.Delete(path);
    }
}

double MeasureSteadyKeyCallMicroseconds(nint session)
{
    const int warmupIterations = 128;
    const int measuredIterations = 2048;

    for (int i = 0; i < warmupIterations; i++)
    {
        ProcessAndRelease(session, 1, "a", "KeyA", 0);
        ProcessAndRelease(session, 2, "", "Backspace", 51);
    }

    Stopwatch steadyWatch = Stopwatch.StartNew();
    for (int i = 0; i < measuredIterations; i++)
    {
        ProcessAndRelease(session, 1, "a", "KeyA", 0);
        ProcessAndRelease(session, 2, "", "Backspace", 51);
    }

    steadyWatch.Stop();
    return steadyWatch.Elapsed.TotalMilliseconds * 1000.0 / (measuredIterations * 2);
}

void ProcessAndRelease(nint session, int key, string logicalText, string physicalKey, int physicalScanCode)
{
    nint snapshot = 0;
    try
    {
        ProcessKey(session, key, logicalText, physicalKey, physicalScanCode, out snapshot);
    }
    finally
    {
        if (snapshot != 0)
        {
            api.SnapshotRelease(snapshot);
        }
    }
}

static string FindRepoRoot(string start)
{
    DirectoryInfo? dir = new(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "rime", "tiger_sentence", "tiger_sentence.codes.txt")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("Could not locate TigerClaw repository root.");
}

static unsafe string Utf8(TcUtf8Slice value)
{
    if (value.Data == 0 || value.Length == 0)
    {
        return string.Empty;
    }

    if (value.Length > int.MaxValue)
    {
        throw new InvalidOperationException("Slice is too large for the smoke test.");
    }

    return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)value.Data, checked((int)value.Length)));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed unsafe class SliceBuffer : IDisposable
{
    private SliceBuffer(nint buffer, nuint length)
    {
        Buffer = buffer;
        Slice = new TcUtf8Slice
        {
            Data = buffer,
            Length = length,
        };
    }

    public nint Buffer { get; private set; }
    public TcUtf8Slice Slice { get; }

    public static SliceBuffer WithIgnoredSuffix(string value, string ignoredSuffix)
    {
        byte[] visible = Encoding.UTF8.GetBytes(value);
        byte[] ignored = Encoding.UTF8.GetBytes(ignoredSuffix);
        nint buffer = (nint)NativeMemory.Alloc((nuint)(visible.Length + ignored.Length));
        visible.CopyTo(new Span<byte>((void*)buffer, visible.Length));
        ignored.CopyTo(new Span<byte>((byte*)buffer + visible.Length, ignored.Length));
        return new SliceBuffer(buffer, (nuint)visible.Length);
    }

    public void Dispose()
    {
        if (Buffer != 0)
        {
            NativeMemory.Free((void*)Buffer);
            Buffer = 0;
        }
    }
}

internal sealed class Api
{
    public required RuntimeCreateDelegate RuntimeCreate { get; init; }
    public required RuntimeCreateWithConfigDelegate RuntimeCreateWithConfig { get; init; }
    public required RuntimeReleaseDelegate RuntimeRelease { get; init; }
    public required SessionCreateDelegate SessionCreate { get; init; }
    public required SessionActivateDelegate SessionActivate { get; init; }
    public required SessionProcessDelegate SessionProcess { get; init; }
    public required SessionDeactivateDelegate SessionDeactivate { get; init; }
    public required SessionReleaseDelegate SessionRelease { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetHandled { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetIsComposing { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetSelectedIndex { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetCaret { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetCandidateTotal { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetPageIndex { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetPageCount { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetAction { get; init; }
    public required SnapshotGetStringDelegate SnapshotGetPreedit { get; init; }
    public required SnapshotGetStringDelegate SnapshotGetCompositionPrefix { get; init; }
    public required SnapshotGetStringDelegate SnapshotGetActiveInputCode { get; init; }
    public required SnapshotGetStringDelegate SnapshotGetCommit { get; init; }
    public required SnapshotGetIntDelegate SnapshotGetCandidateCount { get; init; }
    public required SnapshotGetCandidateDelegate SnapshotGetCandidate { get; init; }
    public required SnapshotReleaseDelegate SnapshotRelease { get; init; }

    public static Api Load(nint library)
    {
        return new Api
        {
            RuntimeCreate = Load<RuntimeCreateDelegate>(library, "tc_runtime_create"),
            RuntimeCreateWithConfig = Load<RuntimeCreateWithConfigDelegate>(library, "tc_runtime_create_with_config"),
            RuntimeRelease = Load<RuntimeReleaseDelegate>(library, "tc_runtime_release"),
            SessionCreate = Load<SessionCreateDelegate>(library, "tc_session_create"),
            SessionActivate = Load<SessionActivateDelegate>(library, "tc_session_activate"),
            SessionProcess = Load<SessionProcessDelegate>(library, "tc_session_process"),
            SessionDeactivate = Load<SessionDeactivateDelegate>(library, "tc_session_deactivate"),
            SessionRelease = Load<SessionReleaseDelegate>(library, "tc_session_release"),
            SnapshotGetHandled = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_handled"),
            SnapshotGetIsComposing = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_is_composing"),
            SnapshotGetSelectedIndex = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_selected_index"),
            SnapshotGetCaret = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_caret"),
            SnapshotGetCandidateTotal = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_candidate_total"),
            SnapshotGetPageIndex = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_page_index"),
            SnapshotGetPageCount = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_page_count"),
            SnapshotGetAction = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_action"),
            SnapshotGetPreedit = Load<SnapshotGetStringDelegate>(library, "tc_snapshot_get_preedit"),
            SnapshotGetCompositionPrefix = Load<SnapshotGetStringDelegate>(library, "tc_snapshot_get_composition_prefix"),
            SnapshotGetActiveInputCode = Load<SnapshotGetStringDelegate>(library, "tc_snapshot_get_active_input_code"),
            SnapshotGetCommit = Load<SnapshotGetStringDelegate>(library, "tc_snapshot_get_commit"),
            SnapshotGetCandidateCount = Load<SnapshotGetIntDelegate>(library, "tc_snapshot_get_candidate_count"),
            SnapshotGetCandidate = Load<SnapshotGetCandidateDelegate>(library, "tc_snapshot_get_candidate"),
            SnapshotRelease = Load<SnapshotReleaseDelegate>(library, "tc_snapshot_release"),
        };
    }

    private static T Load<T>(nint library, string name)
        where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcUtf8Slice
{
    public nint Data;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TcInputEvent
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
internal struct TcEngineConfig
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
    public TcUtf8Slice UserDictionaryPath;
    public TcUtf8Slice SelectionKeys;
    public TcUtf8Slice PreviousPageKeys;
    public TcUtf8Slice NextPageKeys;
    public TcUtf8Slice PinyinLexiconPath;
    public TcUtf8Slice SentenceModelPath;
    public TcUtf8Slice SentenceQwenNativeLibraryPath;
    public TcUtf8Slice SentenceQwenModelPath;
    public TcUtf8Slice SentenceLexiconPath;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int RuntimeCreateDelegate(TcUtf8Slice lexiconPath, out nint runtime);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int RuntimeCreateWithConfigDelegate(TcUtf8Slice lexiconPath, ref TcEngineConfig config, out nint runtime);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void RuntimeReleaseDelegate(nint runtime);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SessionCreateDelegate(nint runtime, out nint session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SessionActivateDelegate(nint session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SessionProcessDelegate(nint session, ref TcInputEvent input, out nint snapshot);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SessionDeactivateDelegate(nint session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SessionReleaseDelegate(nint session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SnapshotGetIntDelegate(nint snapshot);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate TcUtf8Slice SnapshotGetStringDelegate(nint snapshot);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate TcUtf8Slice SnapshotGetCandidateDelegate(nint snapshot, int index);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SnapshotReleaseDelegate(nint snapshot);
