# TigerClaw Agent Handoff

This file is the single project handoff entry for future agents. Treat it as the current source of truth before reading older documents. External documents are referenced only where they are still useful.

Last reorganized: 2026-08-26.

## Current Architecture

TigerClaw is a Windows input method built around a split-process runtime.

The parallel Rust Core replacement candidate has its own handoff document at
[`rust/TigerClaw.Core.Rust/HANDOFF.md`](rust/TigerClaw.Core.Rust/HANDOFF.md).
Read it when working on Rust parity, Rust publishing, or controlled Core
replacement tests. The C# Core remains the default active implementation.

```text
Windows TSF
  -> BimeTSF2/SampleIME/TigerClaw.dll
  -> named pipe \\.\pipe\BimeIPC
  -> next/TigerClaw.Core.exe
  -> optional named pipe \\.\pipe\TigerClaw.Sentence.v1
  -> next/TigerClaw.Sentence.exe
  -> memory mapped UI state + heartbeat/events
  -> next/TigerClaw.Overlay.exe
  -> next/TigerClaw.Dialog.exe
```

Active components:

- `BimeTSF2/SampleIME/`: active TSF DLL build/package source. It is bridge-only: capture TSF key/focus/caret/IME activation events, send them to Core, and apply Core responses back to TSF.
- `next/TigerClaw.Core/`: input method state center. Owns config, lexicon loading, candidate logic, key handling, code masking, caret/focus state, IPC responses, and process launch commands.
- `next/TigerClaw.Overlay/`: WPF status/candidate UI. Reads `OverlayUiState` from shared memory, renders candidate/status windows, and plays typing sounds.
- `next/TigerClaw.Dialog/`: WPF settings/add-word/custom-selection-key UI. Talks to Core through the same named pipe.
- `next/TigerClaw.Shared/`: shared runtime constants, build info, process guards, heartbeat/MMF helpers, and `OverlayUiState`.
- `next/TigerClaw.Sentence/`: optional .NET Qwen reranking sidecar backed by the native llama.cpp wrapper in `next/TigerClaw.Sentence.Native/`. Core starts it lazily; failures never replace the native n-gram candidate order.
- `next/TigerClaw.Hook.Native/`: experimental native hook frontend. It is built and published, but it must not replace the default TSF workflow unless explicitly launched.

Deprecated root implementations `bime/` and `BimeTSF/` have been removed. Upstream/reference-only trees live under `reference/` and must not be wired into active build or publish scripts.

## Runtime Contracts

Main IPC:

- Pipe: `\\.\pipe\BimeIPC`
- Encoding: UTF-8 JSON, one message per line
- Contract reference: `Protocol/messages.md`
- Main handler: `next/TigerClaw.Core/ProtocolHandler.cs`

Sentence reranking IPC:

- Pipe: `\\.\pipe\TigerClaw.Sentence.v1`
- Contract reference: `Protocol/sentence_messages.md`
- Core client: `next/TigerClaw.Core/SentenceRerankClient.cs`
- Sidecar server: `next/TigerClaw.Sentence/Program.cs`

Common pipe message types in active code:

- TSF/Native/Dialog -> Core requests: `hello`, `query_state`, `key`, `ctrl_space`, `show_menu`, `show_config`, `show_addci`, `reload_config`, `reload_mb`, `get_config`, `set_config`, `add_ci`, `construct_ci`
- TSF/Native -> Core notifications: `focus`, `caret`, `ime_active`, `composition_canceled`, `hook_native_disabled`
- Core -> caller response: `response` with fields such as `success`, `handled`, `commit_text`, `input_buffer`, `keyboard_open`, `cancel_composition`

UI state:

- Core publishes `OverlayUiState` to `Local\TigerClaw.UiState.v1`.
- Overlay polls that memory map via `UiStateReader`.
- Core heartbeat is `Local\TigerClaw.Heartbeat.v1`; Overlay heartbeat is `Local\TigerClaw.OverlayHeartbeat.v1`.
- Status-window menu trigger uses `Local\TigerClaw.ShowMenu.v1`.

Important behavior:

- Core composition authority remains raw. Unlimited mixed Chinese/English input keeps the complete per-composition raw code, including letter casing, separately and full-decodes it on every edit; lexicon lookup is case-insensitive, while display and literal/raw commits preserve casing. The outward composition is derived as a resolved prefix plus an active code tail. Completed segments without candidates remain literal English in the resolved prefix.
- Sentence input likewise keeps raw code authoritative. Its outward composition inserts spaces according to the currently selected candidate's segmentation; those spaces are display-only and never enter raw-code commits. Up/Down and Tab/Shift+Tab traverse the visible sentence candidates without reordering them (Tab selection overrides Tab-clear while any sentence candidate is visible); Overlay highlights the selected index except when the first candidate is current. Sentence mode leaves Ctrl+number shortcuts to the target application.
- Pure n-gram sentence decoding adds a `2.0` score reward per emitted Unicode character during Beam expansion. This code-conditioned output-length prior counteracts the raw language model's preference for shorter texts sharing the same Tiger code; Core and the standalone Rime Lua decoder must keep the value and expansion-time behavior aligned.
- Windows Core loads an optional per-schema `补充语料.txt` from the active code-table directory. Each non-comment line is `text [weight]`, separated by spaces or tabs; weight defaults to 1000. The file uses the normal automatic text-encoding detector and must be excluded from ordinary `*.txt` lexicon scanning. Exact Unicode-text-element matches add `clamp(9.0 + 2.0 * ln(weight / 1000), 0, 16)` during Beam expansion, including every occurrence of a one-character entry; overlapping entries ending at the same position contribute only the largest reward. User-specified entries are treated as a strong preference. The reward affects Beam pruning, n-gram order, and the base score retained by Qwen reranking, but not confidence mass. Supplemental data is part of the schema snapshot and changes only after lexicon reload/schema switching. It never creates an otherwise illegal code path or affects non-sentence input. The Rime pack mirrors the same scoring through user-data-root `tiger_sentence.supplement.txt`; it requires UTF-8 and reloads when the Lua module is redeployed.
- Sentence input is disabled by default. `自动启用整句模式` (default on) does not change the `整句输入` switch; when that switch is off and the current schema name contains `整句`, sentence mode still runs. When enabled, Core decodes on a latest-generation-only background worker so TSF key responses never wait for Beam Search; deterministic commit/selection actions synchronously ensure the latest result when necessary. The decoder reuses prefix lattice states across append/backspace (full rebuild when the one-key whole-input rule changes); an append expands only edges that cross the previous raw end, so retained paths are not rescored or counted twice. Beam destinations adaptively aggregate duplicate output text during high-ambiguity expansion, use deterministic exact Top-K selection instead of sorting discarded states, and freeze processed positions back to compact lists; an unchanged frozen source bucket is returned directly on later generations instead of rebuilding its dictionary. Lexicon entries cache their `StringInfo` text elements and log-rank penalty. Beam states retain raw/text boundaries but not repeatedly concatenated segmented-code strings; segmentation is reconstructed only for the final visible candidates. These optimizations preserve candidate scores/order and confidence mass while avoiding long-composition dictionary retention. Exact KN transition scores and observed-bigram probes use fixed-size inline-entry caches, so cache replacement allocates no per-miss objects, and EOS is not written back into cached beams. Worker and sync complement share one locked decoder so incremental state and the allocation-free model caches stay consistent. While decoding is pending, Overlay keeps the previous candidate list so the window does not collapse, and the displayed code keeps the last segmentation spaces and only appends or trims the live raw suffix (those spaces stay display-only). TSF polls lightweight `query_state` so composition segmentation follows the same current first candidate as Overlay, including later neural reranking. First-key pending with nothing to hold keeps the Overlay candidate window hidden until the first result. A one-key segment is legal only when the whole input is one key. Other segments consume at least two keys including an optional selector (`;` selects rank 2, `'` selects rank 3, digits select an explicit rank). When the whole input has at most four encoding keys, a bare segment may use every lexicon entry on that code, but first-choice paths stay ahead of later ranks and those later ranks keep lexicon order; longer inputs still require an explicit selector for anything after rank 1. Semicolon and quote selectors enter the code only when their corresponding selection settings are enabled. Multi-character lexicon entries are legal edges. The top 1500 characters from the embedded frequency list keep the shortest first-choice code only; rarer single characters may also be reached by their non-primary codes. Core exclusively uses the full-corpus pruned Modified Kneser-Ney V2 model (`sentence-ngram-v2.bin`); the old compact format is not loaded. Debug and release builds both map the raw n-gram file read-only so its pages remain file-backed. Qwen3 0.6B Q8 reranks exactly the first five n-gram candidates with weight 0.84 on every accepted generation; its GGUF is likewise file-mapped and unencrypted. Reranking remains asynchronous and generation-checked.
- Core sentence early commit is experimental and controlled by `整句自动提前上屏` (default off). The runtime key path never synchronously complements Beam Search: it evaluates only a completed current/previous generation, then starts the current key's worker, so confirmation can be delayed by one key when decoding is asynchronous. The authoritative decoded raw-code length gate is strict: length `<= 4` never triggers or accumulates stability; only after the fifth decoded key do all candidates (including one-/two-key first segments) participate normally. Stability uses the longest common text-element prefix of the confidence proposals from three exact consecutive one-key generations, each calculated at confidence mass `>= 0.995`; the chosen prefix must resolve to the same raw-code boundary in all three generations. Backspace, a missing generation, an empty proposal, or manual candidate traversal invalidates/suspends the evidence. Confidence uses every retained final beam candidate, log-sums alternate paths that produce the same text, and also includes completed lattice states followed by a legal-but-incomplete trailing code prefix. These shadow hypotheses affect only early-commit confidence, never visible candidates or normal ranking; if any contributing beam was truncated, that generation is ineligible. The decoder returns only an `EarlyCommitEvidence` summary (proposal, raw boundaries, truncation, and neural-constraint flag) rather than full confidence candidate arrays; complete paths reuse their already computed EOS/isolation adjustment, and visible candidates use deterministic exact Top-K without sorting the discarded beam. Each partial commit may emit one or more new characters, waits at least three new decoded raw keys after the prior commit, and retains the entire unstable suffix including at least the final candidate character. Decoder candidates carry text-edge/raw-edge metadata, so `_sentenceCommittedRawLength` is resolved without recursive prefix decodes. A Qwen result constrains the matching completed generation when it arrives in time, except when the confidence winner is an incomplete-tail hypothesis that Qwen was never given; commits advance the generation, so a late conflicting result cannot reorder the composition. Decoder results, candidate display, completion, and suffix commits all enforce that candidates retain `_sentenceCommittedText`. The 128-key safety cap applies to the live tail rather than already committed context. Keep these invariants when changing early-commit logic; do not permanently filter short first segments after the raw-length gate.
- TSF key requests carry a stable `client_session` + `event_id`. Test callbacks defer ambiguous failures to the matching real callback, and failed-key replay reuses the same identity; Core caches the first response so a timeout retry cannot execute a physical key twice.
- Code masking (`编码伪装`) is display-only and is applied in `ProtocolHandler` before TSF/Overlay see outward display code.
- Candidate display is owned by Overlay. TSF legacy candidate UI is not the active path. If the candidate window does not fit below the caret, Overlay flips it above and keeps that side for the rest of the composition. Within one composition Overlay also pins the caret anchor to the first resolved point, and only shifts left or flips up when the window would leave the work area.
- Candidate window does not normally show input code unless the relevant config says so.
- Overlay typing sound is implemented in `next/TigerClaw.Overlay/TypingSoundPlayer.cs`.

## Key Code Paths

Startup:

- Core entry: `next/TigerClaw.Core/Program.cs`
- Core process launching: `next/TigerClaw.Core/ProcessLauncher.cs`
- TSF registration guard/shared constants: `next/TigerClaw.Shared/`

Key handling:

- TSF side: `BimeTSF2/SampleIME/KeyEventSink.cpp`
- TSF pipe client: `BimeTSF2/SampleIME/PipeClient.cpp`
- Core protocol dispatch: `next/TigerClaw.Core/ProtocolHandler.cs`
- Core engine: `next/TigerClaw.Core/InputMethodEngine.cs`
- Runtime config/lexicon/candidates: `next/TigerClaw.Core/CoreRuntimeState.cs`

UI:

- Overlay main window: `next/TigerClaw.Overlay/MainWindow.xaml.cs`
- Overlay candidate rendering: `next/TigerClaw.Overlay/MainWindow.Candidate.cs`
- Overlay state reader: `next/TigerClaw.Overlay/OverlayStateSource.cs`
- Settings window: `next/TigerClaw.Dialog/ConfigWindow.xaml` and `ConfigWindow.xaml.cs`
- Add-word window: `next/TigerClaw.Dialog/AddCiWindow.xaml` and `AddCiWindow.xaml.cs`
- Dialog pipe client: `next/TigerClaw.Dialog/CorePipeClient.cs`

Native hook:

- Entry: `next/TigerClaw.Hook.Native/App/main.cpp`
- Runtime: `next/TigerClaw.Hook.Native/App/HookRuntime.cpp`
- Low-level keyboard hook: `next/TigerClaw.Hook.Native/Hook/KeyboardHook.cpp`
- Native pipe client: `next/TigerClaw.Hook.Native/IPC/PipeClient.cpp`

## Build And Smoke Test

Mainline debug build:

```batch
next\build_next.bat
```

Initialize the pinned llama.cpp dependency once after cloning:

```batch
git submodule update --init --recursive
```

The x64 native scorer builds with MSVC. The ARM64 scorer uses Visual Studio's
`C++ Clang tools for Windows` component because llama.cpp does not support its
ARM CPU backend with MSVC.

Debug output:

```text
next\_run\Debug\net48\
next\_run\Debug\native\
next\_run\Debug\sentence\
```

Release publish:

```batch
publish.bat
```

Release output:

```text
release\
release\x64\TigerClaw.dll
release\Win32\TigerClaw.dll
release\Models\sentence-ngram-v2.bin
release\sentence\TigerClaw.Sentence.exe
release\sentence\TigerClaw.Sentence.Native.dll
release\sentence\Models\sentence-qwen-q8.gguf
release\虎爪输入法-限期YYYYMMDD.7z
```

`publish.bat` copies the raw n-gram `.bin`, Qwen Q8 GGUF, and native llama.cpp wrapper, then invokes the tracked `pack_release.bat`. Both models remain unencrypted and are mapped directly from their release files at runtime. The package contains both TSF architectures, the optional sentence sidecar, the models, and the `虎整句` schema. The clean distribution defaults are maintained in `dist_config.txt`; do not package a developer's live `config.txt`.

Windows on ARM development build:

```batch
publish_arm64.bat
```

ARM64 development output:

```text
release_arm64\
```

The Windows on ARM work is still under development and is not the main release path. Its default experiment uses an ARM64X in-process wrapper (`TigerClaw.dll`) with ARM64 and x64 sidecar TSF DLLs. ARM64 direct registration and the out-of-process `TigerClaw.TsfServer.exe`/`LocalServer32` route are diagnostic alternatives selected by their dedicated install scripts. `publish_arm64.bat --diagnostic` enables embedded TSF text logging; the normal ARM64 build follows `text_log_enabled` in `publish_config.txt`.

Manual smoke test:

```batch
next\build_next.bat
next\register_dev_corepath.bat
next\_run\Debug\net48\TigerClaw.Core.exe --with-overlay
```

Then test in target apps and unregister if needed:

```batch
next\unregister_dev_corepath.bat
```

Core mixed-input decoder and commit behavior have zero-dependency automated coverage in `next/TigerClaw.Core.Tests/`.

Offline sentence-input experiments live under `tools/`. `test_sentence_ngram.py` can
stream the local brightmart corpus into a sampled character n-gram model and run
two-code-constrained Beam Search, with optional large-word-frequency reranking;
the `sentence_neural_*`/`prepare_sentence_neural_data.py` tools add an offline
character-Transformer training and reranking path. See `tools/README_sentence_neural.md`.
`tools/SentenceNgramTrainer/` is the Windows-native .NET 10 external-counting trainer;
it uses bounded parallel local counters and sorted-run merging so the full corpus can
be counted without loading every n-gram into memory. Its `build-model` subcommand
exports the Modified Kneser-Ney V2 format consumed directly by Core.
The selected 228 MiB model keeps count-30 terms and softly restores high-confidence
count-20--29 terms at 25% observed weight to avoid hard-threshold regressions.
The active runtime uses the exported compact n-gram and Qwen Q8 GGUF artifacts, while training and evaluation remain offline.
Generated runtime models remain outside Git under `C:\Archive\tigerclaw_sentence_ml\runtime`; debug and publish scripts copy them into `Models\` and `sentence\Models\`.

An experimental standalone Rime 虎整句 pack lives under `rime/tiger_sentence/`. It is not part of the Windows TSF runtime. Regenerate it with `python3 tools/export_tiger_sentence_rime.py`. The scheme keeps TigerClaw selection suffixes and scores a local model in pure Lua. Mobile deployments prefer the lossless context-paged TCSKNM02 file (`sentence-ngram-mobile.bin`, still outside Git), generated by `tools/convert_sentence_ngram_mobile.py`; its sparse resident index and 8 MiB LRU avoid loading the whole model. The old TCSKNM01 file remains a fallback. Decode reuses the lattice incrementally; append expands only edges crossing the old raw end, high-ambiguity buckets aggregate duplicate text adaptively, deterministic exact Top-K avoids sorting discarded states, and processed buckets return to compact frozen arrays that later reads reuse directly. Beam states no longer concatenate segmented-code strings; emit reconstructs segmentation only for the visible 20 candidates. Rank logarithms are memoized per lexicon candidate, and an empty supplemental matcher is skipped outside the character loop. Incremental and full rebuild confidence mass must remain identical. The decoder keeps an exact KN log-probability cache bounded to 32768 entries; EOS is applied only when emitting candidates. Emit also subtracts the same rare-character isolation penalty as Core (rank > 3000, constant 2, left or right observed KN bigram cancels). Inputs of at most four keys may use every lexicon rank, with first-choice paths kept ahead of later ranks. See `rime/tiger_sentence/README.md`.

Rime early-commit work is experimental and has not been validated in Fcitx/iOS clients. It is exposed as the `tiger_sentence_early_commit` schema switch and defaults on. `rime/tiger_sentence/lua/tiger_sentence.lua` keeps only committed text/raw length in Rime context properties; the last three proposals with raw-boundary evidence and manual-navigation suspension live on the stable processor `env` so normal keys do not trigger redundant property/UI updates. Confidence covers the complete retained 200-state final beam, log-sums alternate paths with the same text, and includes completed lattice states followed by a legal-but-incomplete code prefix without changing visible candidates; a truncated contributing beam is ineligible for early commit. The Lua decoder likewise retains only the evidence summary after calculating full confidence mass, reuses complete-path ending scores, and selects the visible 20 candidates with deterministic exact Top-K. After raw length > 4 it takes the longest common text-element prefix of three exact consecutive one-key confidence proposals, requires the prefix to keep the same raw-code boundary in all three, permits one or more new committed characters, and still requires at least three live raw keys. The entire unstable suffix, including at least the final candidate character, remains in composition. The 128-key cap applies only to the live tail. It decodes the complete committed-prefix plus live-tail raw code, filters candidates to the committed prefix before yielding up to 20, and trims candidate preedit to the live tail. Rime's Lua API still requires partial commit to rebuild composition with `commit_text`, `clear`, and `push_input`; commit granularity/cooldown reduce visible refreshes, but frontend-specific single-frame flicker must be validated in each client. It sets `Candidate.preedit` for segmented-code display; do not assign `seg.prompt` from a Lua translator because Weasel 0.17.4 passes a const segment and the setter aborts candidate generation. Encoding stays in the candidate window by default: the pack must not force `inline_preedit` or `preedit_type: composition` in its schema or custom configs. If users enable composition preedit themselves, keep `Candidate.preedit` correct, including the live tail after a partial commit. The Lua decoder keeps bounded exact transition/observed-bigram caches, memoizes lexicon rank filtering and UTF-8 candidate splitting, and the paged model reader caches parsed context locations; preserve these fast paths when changing ranking. The same files were copied to the current user directory `C:\Users\yc\AppData\Roaming\Rime`; after any further change, redeploy schema/config changes and restart the Rime client so Lua modules reload. Do not assume the Rime implementation is production-ready.

## Common Workflows

Add or change IPC:

1. Update `Protocol/messages.md`.
2. Update `next/TigerClaw.Core/ProtocolHandler.cs`.
3. Update the caller side: TSF `PipeClient.cpp`, Dialog `CorePipeClient.cs`, Overlay shared-state reader, or Native Hook `PipeClient.cpp`.

Modify key behavior:

1. Start with `next/TigerClaw.Core/InputMethodEngine.cs`.
2. Check response shaping and UI publication in `next/TigerClaw.Core/ProtocolHandler.cs`.
3. Verify TSF handling in `BimeTSF2/SampleIME/KeyEventSink.cpp` only when physical key capture or TSF commit behavior changes.

Modify display-only code rendering:

1. Prefer `next/TigerClaw.Core/ProtocolHandler.cs`.
2. Keep raw Core input state separate from masked outward display state.
3. Do not change protocol shape unless callers truly need a new field.

Modify settings:

1. UI: `next/TigerClaw.Dialog/ConfigWindow.xaml` and `ConfigWindow.xaml.cs`.
2. Persistence/defaults: `next/TigerClaw.Core/CoreRuntimeState.cs`.
3. Preserve grouped layout, search/filter, dirty-state feedback, and empty-string config values.

Modify release packaging:

1. Edit `publish.bat`.
2. Keep `.bat` files CRLF.
3. Confirm `release\TigerClaw.Core.exe`, `TigerClaw.Overlay.exe`, `TigerClaw.Dialog.exe`, `TigerClaw.exe`, `TigerClaw.Shared.dll`, `x64\TigerClaw.dll`, and `Win32\TigerClaw.dll`.

## Documentation Policy

This `AGENTS.md` is the single handoff entry. Keep it current and concise.

Still-useful external documents:

- `Protocol/messages.md`: detailed pipe message fields.
- `用户使用说明书.md`: user-facing install/use/config guide.
- `更新日志.txt`: release notes.
- `BimeTSF2/SampleIME/BRIDGE_ONLY_NOTES.md`: TSF bridge-only note.
- `reference/README.md`: explains reference-only source trees.
- `rust/TigerClaw.Core.Rust/HANDOFF.md`: Rust Core-specific architecture,
  parity gaps, build commands and replacement smoke-test procedure.
- `docs/FCITX5_ANDROID_PORTING_PLAN.md`: implementation and acceptance plan
  for the standalone native Fcitx5 Android sentence-input plugin. Stages 0–3
  plus Fcitx wiring/config live in `/home/yc/fx5/fcitx5-android` plugin
  `tigerclaw`; device performance and signed release remain outstanding.
- `docs/FCITX5_ANDROID_BASELINE.md`: frozen commits and model hashes for that port.
- `docs/sentence_golden_v1.md`: JSONL golden-snapshot format used to lock
  C# decoder behavior before the C++ core is written.

Archived or historical documents live under `docs/archive/` when present. Do not treat them as current implementation guidance.

Generated files, release payload files, and `reference/` are not the active project handoff surface unless a task specifically asks for them.

## Style And Safety Notes

C#:

- Use existing project style, 4 spaces, Allman braces where surrounding code does.
- Import order: System namespaces, third-party namespaces, project namespaces.
- Private fields use `_underscorePrefix`; locals use camelCase.

C++:

- When debugging window-title or IME compatibility issues, log unambiguous values such as hex code points in addition to human-readable text.
- Keep high-frequency caret/key diagnostics disabled by default once targeted debugging ends.
- Do not rely on raw C++ source-file Chinese string literals for cross-machine runtime matching. Prefer code-point construction and add an end-of-line readable comment, for example `// Pain打器` or `// 跟打`.

Repository hygiene:

- The worktree may be dirty. Do not revert unrelated user changes.
- Prefer `rg`/`rg --files` for search.
- Use `apply_patch` for manual file edits.
- All `.bat` files must keep CRLF line endings. For encoding-sensitive rewrites with Chinese filenames or CRLF preservation, prefer a short Python script over PowerShell string replacement.
