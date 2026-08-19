# TigerClaw Core Rust Handoff

Last updated: 2026-08-19

## Purpose and Status

`rust/TigerClaw.Core.Rust/` is the parallel Rust implementation of the
current `next/TigerClaw.Core` process. It is a replacement candidate, not the
default production Core. It must not replace or modify the C# Core implicitly.

The Rust process uses the production pipe and shared-memory names so it can be
run in place of C# Core during controlled Windows smoke tests:

```text
TSF/Dialog/Native -> \\.\pipe\BimeIPC -> TigerClaw.Core.Rust.exe
                         |-> Local\TigerClaw.UiState.v1
                         |-> Local\TigerClaw.Heartbeat.v1
                         |-> TigerClaw.Sentence.v1 (optional sidecar)
```

## Implemented

- JSON-lines UTF-8 named-pipe protocol on `BimeIPC`.
- `hello`, `query_state`, key input, Ctrl+Space, focus/caret/IME notifications,
  reload and basic Dialog commands.
- Stable `client_session` + `event_id` key replay cache, preventing duplicate
  physical-key execution after a retry.
- Basic candidates, selectors, navigation, commit, backspace, Esc and mixed
  Chinese/English raw-input handling.
- Config loading, hot reload, supported key/value updates and persistence.
- Common two-column code-table loading, add-word append, reverse lookup and
  commit history.
- Basic sentence segmentation, read-only n-gram loading and optional Qwen
  sidecar reranking fallback.
- UI-state and Core-heartbeat MMFs with C#-compatible header layout, PascalCase
  JSON fields, initial publication and Windows monotonic timestamps.
- Overlay startup supervision with heartbeat-based restart attempts.
- `show_menu`, `show_config`, and `show_addci` process launch commands.

## Source Map

- `src/main.rs`: startup, resource discovery and stdio/Windows entry points.
- `src/protocol.rs`: request dispatch, response shaping and key behavior.
- `src/state.rs`: process state and idempotent replay cache.
- `src/config.rs`: supported configuration parsing.
- `src/lexicon.rs`: code-table loading and candidate storage.
- `src/sentence.rs`, `src/ngram.rs`, `src/qwen.rs`: sentence decode/model/scorer.
- `src/windows_pipe.rs`: Windows named pipe, MMF publication and Overlay
  supervision.
- `src/ui_state.rs`: Windows file-mapping writer.

## Known Gaps Versus C# Core

These gaps are intentional handoff items; do not claim full behavioral parity
until they are implemented and tested on Windows:

1. Code-table parity: `.dict.yaml` metadata, frequency/stable sorting, split
   maps, full/construct maps, and the C# user-adjustment operations/files
   (add/delete/pin/move) are incomplete.
2. Schema/config parity: schema listing/current-schema switching, complete
   configuration keys, custom selection-key config persistence, send-history
   count, official/table-folder opening and table export remain incomplete or
   placeholder responses.
3. TSF composition parity: `composition_tracking`/`composition_pending`,
   callback generations, test-key failure replay, timeout behavior and use of
   scan/repeat/extended/NumLock fields are not fully reproduced.
4. Sentence parity: decoding is currently synchronous/basic; the C# latest-
   generation worker, pending-result retention, incremental lattice/cache,
   selector/one-key/four-key rules, display segmentation spaces, EOS and rare
   character penalties are not all equivalent.
5. UI lifecycle parity: Dialog supervision, shutdown child cleanup, all retry
   backoff details and complete Overlay settings/audio/annotation projection
   are not fully equivalent.
6. Interoperability: Rust MMF JSON has not yet been read by the shipped WPF
   Overlay/Dialog on a real Windows installation, and the complete TSF -> Rust
   Core -> WPF -> Sentence chain has not received a Windows smoke test.

## Build and Publish

Dependency-free tests:

```bash
cargo test --manifest-path rust/TigerClaw.Core.Rust/Cargo.toml
```

The current non-Windows environment has verified GNU cross builds:

```bash
cargo build --release --target x86_64-pc-windows-gnu --manifest-path rust/TigerClaw.Core.Rust/Cargo.toml
cargo build --release --target i686-pc-windows-gnu --manifest-path rust/TigerClaw.Core.Rust/Cargo.toml
```

On Windows Developer Command Prompt, use:

```batch
publish_rust_core.bat
publish_rust_core_arm64.bat
```

These scripts are intended to produce `release\TigerClaw.Core.exe` and
`release_arm64\TigerClaw.Core.exe`. MSVC/ARM64 publication remains a Windows
environment task; GNU cross-compilation does not validate MSVC packaging.

## Controlled Replacement Smoke Test

1. Build and publish the existing C# package and preserve its Core executable.
2. Place the Rust executable beside the C# runtime under a test directory.
3. Stop C# Core, launch Rust Core, and keep the existing TSF DLL, Overlay,
   Dialog and optional Sentence sidecar unchanged.
4. Verify `hello`, normal composition/commit, selectors, backspace/Esc,
   Ctrl+Space, config reload, add-word, menu/config/add-word windows, MMF UI
   updates, Overlay heartbeat restart and sentence fallback.
5. Restore C# Core after the test and unregister only test registrations.

Do not overwrite the tracked C# Core or production release payload as part of
this comparison.
