using System.Security.Cryptography;
using System.Text.Json;
using TigerClaw.Engine.Experimental;
using TigerClaw.Engine.Experimental.Input;
using TigerClaw.Engine.Experimental.Lexicon;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string rimeDictPath = Path.Combine(repoRoot, "rime", "tiger_sentence", "tiger_sentence.dict.yaml");
RimeLexiconProvider lexicon = RimeLexiconProvider.Load(rimeDictPath);

Assert(lexicon.EntryCount > 100, "real Rime lexicon loads more than the header seed rows");
Assert(lexicon.LookupExact("a").First().Text == "来", "code a maps to the first real candidate");
Assert(lexicon.LookupExact("a").Select(entry => entry.Text).SequenceEqual(["来", "那个"]),
    "Rime selection suffixes become one ordered candidate sequence");

InputEvent key = InputEvent.Character('a', physicalKey: "KeyA", physicalScanCode: 0, isExtended: false);
Assert(key.Key == InputKey.Character, "character key uses platform-neutral semantic key");
Assert(key.Text == "a", "character key keeps logical text");
Assert(key.PhysicalKey == "KeyA", "character key can carry a host physical key id");
Assert(key.PhysicalScanCode == 0, "character key can carry an optional host scan code");
Assert(!key.IsExtended, "character key can carry an optional extended-key marker");

InputEvent windowsKey = WindowsInputEventMapper.Map(
    WindowsVirtualKey.A,
    scanCode: 0x1E,
    action: "down",
    shift: true,
    ctrl: false,
    alt: false,
    win: false,
    capsLock: false,
    numLock: false,
    repeat: 2,
    extended: false);
Assert(windowsKey.Key == InputKey.Character, "Windows letter maps to semantic character");
Assert(windowsKey.Text == "A", "Windows mapper keeps logical shifted text");
Assert(windowsKey.PhysicalKey == "VK_41", "Windows mapper keeps physical key identity");
Assert(windowsKey.PhysicalScanCode == 0x1E, "Windows mapper keeps scan code");
Assert(windowsKey.IsRepeat, "Windows mapper keeps repeat state");
Assert(windowsKey.NormalizedRepeatCount == 2, "Windows mapper keeps repeat count");

InputEvent rightCtrlUp = WindowsInputEventMapper.Map(
    WindowsVirtualKey.Control,
    scanCode: 0x1D,
    action: "key_up",
    shift: false,
    ctrl: true,
    alt: false,
    win: false,
    capsLock: false,
    numLock: false,
    repeat: 0,
    extended: true);
Assert(rightCtrlUp.Action == KeyAction.KeyUp, "Windows mapper keeps key-up action");
Assert(rightCtrlUp.PhysicalKey == "VK_A3", "Windows mapper resolves right-control identity");
Assert(rightCtrlUp.IsExtended, "Windows mapper keeps extended marker");
Assert(rightCtrlUp.NormalizedRepeatCount == 1, "Windows mapper normalizes repeat count");

BasicTigerClawEngine engine = new(lexicon);
EngineSnapshot afterA = engine.Process(key);
Assert(afterA.Handled, "letter key is handled while composing");
Assert(afterA.Preedit == "a", "preedit tracks the neutral code buffer");
Assert(afterA.Candidates.SequenceEqual(["来", "那个"]), "real candidate list is ordered from the lexicon");
Assert(afterA.SelectedIndex == 0, "selection starts at the first candidate");
Assert(afterA.Commit is null, "letter key does not commit text");

EngineSnapshot afterSpace = engine.Process(new InputEvent(InputKey.Space, null, InputModifiers.None, KeyAction.KeyDown, "Space"));
Assert(afterSpace.Handled, "space commits an active candidate");
Assert(afterSpace.Commit == "来", "space commits the selected real candidate");
Assert(!afterSpace.IsComposing, "space commit clears composition");
Assert(afterSpace.Preedit.Length == 0, "space commit clears preedit");

BasicTigerClawEngine selectionEngine = new(lexicon);
selectionEngine.Process(InputEvent.Character('a', physicalKey: "KeyA"));
EngineSnapshot selectedByDigit = selectionEngine.Process(new InputEvent(InputKey.Digit, "2", InputModifiers.None, KeyAction.KeyDown, "Digit2"));
Assert(selectedByDigit.Handled && selectedByDigit.Commit == "那个", "digit selects the second real candidate");

BasicTigerClawEngine semicolonEngine = new(lexicon);
semicolonEngine.Process(InputEvent.Character('a', physicalKey: "KeyA"));
Assert(semicolonEngine.Process(new InputEvent(InputKey.Semicolon, ";", InputModifiers.None, KeyAction.KeyDown, "Semicolon")).Commit == "那个",
    "semicolon selects the second real candidate when enabled");

BasicTigerClawEngine quoteEngine = new(
    new InMemoryLexiconProvider([
        new LexiconEntry("q", "甲", 0),
        new LexiconEntry("q", "乙", 1),
        new LexiconEntry("q", "丙", 2),
    ]));
quoteEngine.Process(InputEvent.Character('q', physicalKey: "KeyQ"));
Assert(quoteEngine.Process(new InputEvent(InputKey.Quote, "'", InputModifiers.None, KeyAction.KeyDown, "Quote")).Commit == "丙",
    "quote selects the third candidate when enabled");

InMemoryLexiconProvider pagingLexicon = new([
    new LexiconEntry("q", "甲", 0),
    new LexiconEntry("q", "乙", 1),
    new LexiconEntry("q", "丙", 2),
    new LexiconEntry("q", "丁", 3),
    new LexiconEntry("q", "戊", 4),
    new LexiconEntry("q", "己", 5),
]);
BasicTigerClawEngine pagingEngine = new(pagingLexicon, new EngineConfig(PageSize: 2));
EngineSnapshot firstPage = pagingEngine.Process(InputEvent.Character('q', physicalKey: "KeyQ"));
Assert(firstPage.Candidates.SequenceEqual(["甲", "乙"]), "first candidate page contains the configured window");
Assert(firstPage.CandidateTotal == 6 && firstPage.PageIndex == 0 && firstPage.PageCount == 3, "first candidate page metadata is exact");
EngineSnapshot secondPage = pagingEngine.Process(new InputEvent(InputKey.PageNext, null, InputModifiers.None, KeyAction.KeyDown, "Equal"));
Assert(secondPage.Candidates.SequenceEqual(["丙", "丁"]), "next-page action advances the visible candidates");
Assert(secondPage.PageIndex == 1 && secondPage.PageCount == 3, "next-page metadata is exact");
Assert(pagingEngine.Process(new InputEvent(InputKey.Digit, "1", InputModifiers.None, KeyAction.KeyDown, "Digit1")).Commit == "丙",
    "digit selection is relative to the visible page");

BasicTigerClawEngine autoCommitEngine = new(
    new InMemoryLexiconProvider([new LexiconEntry("x", "行", 0)]),
    new EngineConfig(MaxCodeLength: 1));
EngineSnapshot autoCommit = autoCommitEngine.Process(InputEvent.Character('x', physicalKey: "KeyX"));
Assert(autoCommit.Commit == "行" && !autoCommit.IsComposing, "unique terminal code auto-commits at configured maximum length");

BasicTigerClawEngine sessionA = new(lexicon);
BasicTigerClawEngine sessionB = new(lexicon);
sessionA.Process(InputEvent.Character('a', physicalKey: "KeyA"));
sessionB.Process(InputEvent.Character('t', physicalKey: "KeyT"));
Assert(sessionA.Query().Preedit == "a", "session A keeps its own preedit");
Assert(sessionB.Query().Preedit == "t", "session B keeps its own preedit");
Assert(sessionB.Process(new InputEvent(InputKey.Space, null, InputModifiers.None, KeyAction.KeyDown, "Space")).Commit == "我", "session B commits independently");
Assert(sessionA.Query().Preedit == "a", "session B commit does not clear session A");

string fixturePath = Path.Combine(repoRoot, "tests", "engine_cases", "basic_real_code_commit.json");
RunBasicFixture(fixturePath, repoRoot, lexicon);

Console.WriteLine("REAL_ENGINE_SLICE_PASS");
Console.WriteLine($"LexiconEntries={lexicon.EntryCount}");
Console.WriteLine($"Dictionary={Path.GetRelativePath(repoRoot, rimeDictPath)}");
Console.WriteLine($"Fixture={Path.GetRelativePath(repoRoot, fixturePath)}");

static string FindRepoRoot(string start)
{
    DirectoryInfo? dir = new(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "rime", "tiger_sentence", "tiger_sentence.dict.yaml")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("Could not locate TigerClaw repository root.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RunBasicFixture(string fixturePath, string repoRoot, RimeLexiconProvider lexicon)
{
    using FileStream stream = File.OpenRead(fixturePath);
    using JsonDocument document = JsonDocument.Parse(stream);
    JsonElement root = document.RootElement;

    string lexiconRelativePath = root
        .GetProperty("resources")
        .GetProperty("lexicon_file")
        .GetProperty("path")
        .GetString() ?? throw new InvalidOperationException("Fixture is missing resources.lexicon_file.path.");
    string expectedSha256 = root
        .GetProperty("resources")
        .GetProperty("lexicon_file")
        .GetProperty("sha256")
        .GetString() ?? throw new InvalidOperationException("Fixture is missing resources.lexicon_file.sha256.");

    string actualSha256 = Sha256Hex(Path.Combine(repoRoot, lexiconRelativePath));
    Assert(actualSha256 == expectedSha256, "fixture lexicon hash matches the loaded real lexicon");

    BasicTigerClawEngine engine = new(lexicon);
    List<EngineSnapshot> snapshots = [];
    foreach (JsonElement eventElement in root.GetProperty("events").EnumerateArray())
    {
        snapshots.Add(engine.Process(ReadInputEvent(eventElement)));
    }

    foreach (JsonElement update in root.GetProperty("expected").GetProperty("updates").EnumerateArray())
    {
        int afterEvent = update.GetProperty("after_event").GetInt32();
        EngineSnapshot snapshot = snapshots[afterEvent];

        if (update.TryGetProperty("handled", out JsonElement handled))
        {
            Assert(snapshot.Handled == handled.GetBoolean(), $"fixture update {afterEvent} handled matches");
        }

        if (update.TryGetProperty("preedit", out JsonElement preedit))
        {
            Assert(snapshot.Preedit == preedit.GetString(), $"fixture update {afterEvent} preedit matches");
        }

        if (update.TryGetProperty("candidates", out JsonElement candidates))
        {
            string[] expectedCandidates = candidates.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            Assert(snapshot.Candidates.SequenceEqual(expectedCandidates), $"fixture update {afterEvent} candidates match");
        }

        if (update.TryGetProperty("selected_index", out JsonElement selectedIndex))
        {
            Assert(snapshot.SelectedIndex == selectedIndex.GetInt32(), $"fixture update {afterEvent} selected index matches");
        }

        if (update.TryGetProperty("commit", out JsonElement commit))
        {
            string? expectedCommit = commit.ValueKind == JsonValueKind.Null ? null : commit.GetString();
            Assert(snapshot.Commit == expectedCommit, $"fixture update {afterEvent} commit matches");
        }

        if (update.TryGetProperty("is_composing", out JsonElement isComposing))
        {
            Assert(snapshot.IsComposing == isComposing.GetBoolean(), $"fixture update {afterEvent} composition state matches");
        }
    }
}

static InputEvent ReadInputEvent(JsonElement eventElement)
{
    string keyName = eventElement.GetProperty("key").GetString() ?? "Unknown";
    InputKey key = Enum.Parse<InputKey>(keyName, ignoreCase: true);
    string? value = eventElement.TryGetProperty("value", out JsonElement valueElement)
        ? valueElement.GetString()
        : null;
    KeyAction action = eventElement.TryGetProperty("action", out JsonElement actionElement)
        ? Enum.Parse<KeyAction>(actionElement.GetString() ?? nameof(KeyAction.KeyDown), ignoreCase: true)
        : KeyAction.KeyDown;
    string? physicalKey = eventElement.TryGetProperty("physical_key", out JsonElement physicalKeyElement)
        ? physicalKeyElement.GetString()
        : null;
    int? physicalScanCode = eventElement.TryGetProperty("physical_scan_code", out JsonElement physicalScanCodeElement)
        ? physicalScanCodeElement.GetInt32()
        : null;
    bool isExtended = eventElement.TryGetProperty("is_extended", out JsonElement isExtendedElement)
        && isExtendedElement.GetBoolean();
    bool isRepeat = eventElement.TryGetProperty("is_repeat", out JsonElement isRepeatElement)
        && isRepeatElement.GetBoolean();

    InputModifiers modifiers = InputModifiers.None;
    if (eventElement.TryGetProperty("modifiers", out JsonElement modifierElements))
    {
        foreach (JsonElement modifierElement in modifierElements.EnumerateArray())
        {
            if (Enum.TryParse(modifierElement.GetString(), ignoreCase: true, out InputModifiers parsed))
            {
                modifiers |= parsed;
            }
        }
    }

    return new InputEvent(key, value, modifiers, action, physicalKey, physicalScanCode, isExtended, isRepeat);
}

static string Sha256Hex(string path)
{
    using FileStream stream = File.OpenRead(path);
    byte[] hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}
