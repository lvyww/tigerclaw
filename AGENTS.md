# TigerClaw Agent Handoff

This file is the single project handoff entry for future agents. Treat it as the current source of truth before reading older documents. External documents are referenced only where they are still useful.

Last reorganized: 2026-08-09.

## Current Architecture

TigerClaw is a Windows input method built around a split-process runtime.

```text
Windows TSF
  -> BimeTSF2/SampleIME/TigerClaw.dll
  -> named pipe \\.\pipe\BimeIPC
  -> next/TigerClaw.Core.exe
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
- `next/TigerClaw.Hook.Native/`: experimental native hook frontend. It is built and published, but it must not replace the default TSF workflow unless explicitly launched.

Deprecated root implementations `bime/` and `BimeTSF/` have been removed. Upstream/reference-only trees live under `reference/` and must not be wired into active build or publish scripts.

## Runtime Contracts

Main IPC:

- Pipe: `\\.\pipe\BimeIPC`
- Encoding: UTF-8 JSON, one message per line
- Contract reference: `Protocol/messages.md`
- Main handler: `next/TigerClaw.Core/ProtocolHandler.cs`

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
- Code masking (`编码伪装`) is display-only and is applied in `ProtocolHandler` before TSF/Overlay see outward display code.
- Candidate display is owned by Overlay. TSF legacy candidate UI is not the active path.
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

Debug output:

```text
next\_run\Debug\net48\
next\_run\Debug\native\
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
```

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
None of these experiments is connected to the active runtime.

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
