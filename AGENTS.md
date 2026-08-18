# TigerClaw Agent Handoff

This file is the single project handoff entry for future agents. Treat it as the current source of truth before reading older documents. External documents are referenced only where they are still useful.

Last reorganized: 2026-08-15.

## Current Architecture

TigerClaw is a Windows input method built around a split-process runtime.

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
- Sentence input is disabled by default. `自动启用整句模式` (default on) does not change the `整句输入` switch; when that switch is off and the current schema name contains `整句`, sentence mode still runs. When enabled, Core decodes on a latest-generation-only background worker so TSF key responses never wait for Beam Search; deterministic commit/selection actions synchronously ensure the latest result when necessary. The decoder reuses prefix lattice states across append/backspace (full rebuild when the one-key whole-input rule changes) and does not write EOS back into cached beams. Worker and sync complement share one locked decoder so incremental state stays consistent. While decoding is pending, Overlay keeps the previous candidate list so the window does not collapse, and the displayed code keeps the last segmentation spaces and only appends or trims the live raw suffix (those spaces stay display-only). TSF polls lightweight `query_state` so composition segmentation follows the same current first candidate as Overlay, including later neural reranking. First-key pending with nothing to hold keeps the Overlay candidate window hidden until the first result. A one-key segment is legal only when the whole input is one key. Other segments consume at least two keys including an optional selector (`;` selects rank 2, `'` selects rank 3, digits select an explicit rank). When the whole input has at most four encoding keys, a bare segment may use every lexicon entry on that code, but first-choice paths stay ahead of later ranks and those later ranks keep lexicon order; longer inputs still require an explicit selector for anything after rank 1. Semicolon and quote selectors enter the code only when their corresponding selection settings are enabled. Multi-character lexicon entries are legal edges. The top 1500 characters from the embedded frequency list keep the shortest first-choice code only; rarer single characters may also be reached by their non-primary codes. Core exclusively uses the full-corpus pruned Modified Kneser-Ney V2 model (`sentence-ngram-v2.*`); the old compact format is not loaded. Raw development n-grams are file-mapped, while protected release n-grams are authenticated and decompressed into an anonymous page-file mapping. Qwen3 0.6B Q8 reranks exactly the first five n-gram candidates with weight 0.84 on every accepted generation; its GGUF remains file-mapped and unencrypted. Reranking remains asynchronous and generation-checked.
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
release\Models\sentence-ngram-v2.tcmodel
release\sentence\TigerClaw.Sentence.exe
release\sentence\TigerClaw.Sentence.Native.dll
release\sentence\Models\sentence-qwen-q8.gguf
release\虎爪输入法-限期YYYYMMDD.7z
```

`publish.bat` protects the n-gram as an authenticated `.tcmodel` container, copies the unencrypted Qwen Q8 GGUF and native llama.cpp wrapper, then invokes the tracked `pack_release.bat`. The release key stays outside Git at `C:\Archive\tigerclaw_sentence_ml\runtime\model-protection.key` and is embedded only in release binaries. The package contains both TSF architectures, the optional sentence sidecar, the models, and the `虎整句` schema. The clean distribution defaults are maintained in `dist_config.txt`; do not package a developer's live `config.txt`.

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

An experimental standalone Rime 虎整句 pack lives under `rime/tiger_sentence/`. It is not part of the Windows TSF runtime. Regenerate it with `python3 tools/export_tiger_sentence_rime.py`. The scheme keeps TigerClaw selection suffixes and scores a local TCSKNM01 file (`sentence-ngram-v2.bin`, still outside Git) in pure Lua. Decode reuses the lattice incrementally and caches exact KN log-probabilities; EOS is applied only when emitting candidates. Emit also subtracts the same rare-character isolation penalty as Core (rank > 3000, constant 2, left or right observed KN bigram cancels). Inputs of at most four keys may use every lexicon rank, with first-choice paths kept ahead of later ranks. See `rime/tiger_sentence/README.md`.

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
