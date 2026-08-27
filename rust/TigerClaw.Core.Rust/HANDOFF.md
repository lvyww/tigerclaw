# TigerClaw Core Rust Handoff

Last updated: 2026-08-27

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

- JSON-lines UTF-8 named-pipe protocol on `BimeIPC`, using the message-type
  pipe mode required by the production TSF client's `PIPE_READMODE_MESSAGE`.
- `hello`, `query_state`, key input, Ctrl+Space, focus/caret/IME notifications,
  reload and basic Dialog commands.
- Stable `client_session` + `event_id` key replay cache, preventing duplicate
  physical-key execution after a retry.
- Basic candidates, selectors, navigation, commit, backspace, Esc and mixed
  Chinese/English raw-input handling.
- C#-compatible Chinese/English punctuation maps, Shift punctuation, smart
  quote alternation, `/输出顿号`, and the ASCII period-after-digit rule.
  Punctuation commits the active normal/sentence candidate and never repeats
  an already emitted sentence early-commit prefix.
- The explicit `ctrl_space` request, the physical Ctrl+Space chord (including
  release-order grace/repeat disarming), and the bare-Shift language toggle
  commit the raw composition before closing it; pass-through Ctrl/Alt/Win
  shortcuts cancel stale compositions. Candidate publication and sentence
  traversal obey the configured page size.
- Config loading, hot reload, supported key/value updates and persistence.
  Reload restores `默认中文`, reloads the configured schema lexicon, and
  notification-only messages publish their UI changes immediately.
- Common two-column code-table loading, add-word append, reverse lookup and
  commit history.
- Sentence lattice Beam Search with C# legal-split rules (one-key only for
  whole input, two-key minimum otherwise, `;`/`'`/digit selectors, all ranks
  when raw length `<= 4`, first-choice otherwise, whole-code paths such as
  `gyfs`/`gyfs2`).
- TCSKNM01 V2 KN interpolation (`log P` with unigram/bigram/trigram lambdas),
  BOS/EOS, emitted-character reward `2.0`, rank penalty, rare-character
  isolation (`rank > 3000`, λ `2`, observed KN bigram cancels), and
  supplemental Aho-Corasick rewards `clamp(9 + 2 ln(weight/1000), 0, 16)`.
  Optional Qwen sidecar reranking still falls back to n-gram/beam order.
- Embedded `sentence_char_ranks.txt` (same file as C# Core) and optional
  per-schema `补充语料.txt` beside the code table.
- Sentence protocol display uses candidate segmentation spaces (display-only).
  Digits, numpad digits, `;` and `'` append into raw code instead of picking
  Overlay candidates. Enter commits the live raw tail.
- Early-commit evidence and runtime match the C# gate: raw length `> 4`,
  three consecutive one-key generations, confidence mass `>= 0.995`, stable
  raw boundary, retain the last character, navigation/backspace suspend,
  Windows default off (`整句自动提前上屏`).
- Incremental lattice: append expands only edges that cross the previous raw
  end; backspace reuses the prefix buckets; one-key and `<= 4` rank-rule
  changes force a full rebuild. Processed Beam positions freeze back to a
  compact list; high-ambiguity buckets aggregate duplicate text; visible
  candidates and Beam pruning use deterministic exact Top-K. Segmentation
  strings are rebuilt only for the emitted candidates.
- Allocation-free KN caches: log-probability `1<<18` and observed-bigram
  `1<<16` inline slots with C# mixing, so cache replacement allocates no
  per-miss objects.
- Production stdio/Windows entry starts a latest-generation sentence worker
  so key responses do not wait for Beam Search. Overlay keeps the previous
  candidate list while pending, stitches the live raw suffix onto the last
  segmented display, and hides the window on first-key pending. Unit tests
  stay on the synchronous path unless they opt in. Qwen rerank is requested
  off the key path and applied only when the generation still matches.
- UI-state and Core-heartbeat MMFs with C#-compatible header layout, PascalCase
  JSON fields, initial publication and Windows monotonic timestamps.
- Windows production entry uses the GUI subsystem, acquires the same
  `Local\TigerClaw.Core.SingleInstance` mutex as C# Core, and publishes the
  first heartbeat before starting its timer. Repeated TSF launch attempts
  therefore exit without creating console windows or parallel pipe servers.
- Overlay startup supervision with heartbeat-based restart attempts.
- `show_menu`, `show_config`, and `show_addci` process launch commands.
- Full C# config key set (get/set/persist), schema listing/current-schema
  switching (`当前码表`, Ctrl+m), automatic sentence mode when the schema name
  contains `整句`, display-only code masking, `composition_tracking` /
  `composition_pending`, Shift and Ctrl+Space toggles, Ctrl+= add-word,
  Overlay theme/font/mask/delay fields, and `open_official` /
  `open_mb_folder` / `export_mb`.
- C#-compatible `自定义选重键.txt` loading, default creation, Dialog get/set
  protocol, decimal/hex/`VK_*` tokens, empty slots, left/right modifier
  resolution and consumed modifier key-up handling.

## Source Map

- `src/main.rs`: startup, resource discovery and stdio/Windows entry points.
- `src/protocol.rs`: request dispatch, response shaping and key behavior.
- `src/state.rs`: process state and idempotent replay cache.
- `src/config.rs`: supported configuration parsing.
- `src/lexicon.rs`: code-table loading and candidate storage.
- `src/decoder.rs`: legal lattice Beam Search, isolation, supplements,
  early-commit evidence, incremental lattice cache and exact Top-K.
- `src/ranks.rs`, `src/supplement.rs`, `src/early.rs`: character ranks,
  supplemental matcher and early-commit state machine.
- `src/sentence.rs`, `src/ngram.rs`, `src/qwen.rs`: sentence decode/model/scorer.
- `src/windows_pipe.rs`: Windows named pipe, MMF publication and Overlay
  supervision.
- `src/ui_state.rs`: Windows file-mapping writer.

## Known Gaps Versus C# Core

These gaps are intentional handoff items; do not claim full behavioral parity
until they are implemented and tested on Windows:

1. Code-table parity: Rust now has a baseline `.dict.yaml`/text loader with
   metadata, frequency/stable sorting, split/full/construct maps,
   comments/annotations, display-versus-commit entries, encoding detection,
   user adjustments and directory scanning that excludes `补充语料.txt`.
   Focused unit tests cover those paths; shipped-table and Windows acceptance
   are still open.
2. Remaining Dialog/config acceptance: pinyin reverse lookup, user adjustment
   persistence, transactional config changes and exact recent-history counting
   now have Rust implementations/unit coverage. The real Windows Dialog path,
   frontend key delivery and full C# message acceptance still need testing.
3. TSF composition parity: Rust now tracks physical Ctrl+Space, CapsLock,
   quote key-up fallback, caret metadata, scan/repeat/extended/NumLock fields,
   and stable key replay identities. `composition_tracking`/
   `composition_pending` are published. Installed-TSF callback and timeout
   traces remain to be accepted against C#.
4. Rust centralizes Unicode text-element handling for composition metadata,
   early-commit boundaries, isolation and history, with a dependency-free
   approximation of .NET `StringInfo`; exhaustive .NET edge-case comparison
   remains open. Overlay audio sequence ticks are now driven from accepted key
   events, but WPF rendering/playback acceptance remains open.
5. UI lifecycle: shutdown child cleanup now has an ownership-based Rust
   implementation, Hook Native receives its cooperative exit event, and the
   Overlay heartbeat supervisor is present. Dialog supervision, exact retry
   timing and Windows shutdown acceptance remain open.
6. Interoperability: the TSF -> Rust Core -> WPF Overlay chain has received
   controlled ARM64 smoke testing, but the full Dialog/Sentence matrix and a
   C#-versus-Rust message golden diff are still required before claiming a
   production Core replacement.

## Parity Work Queue

This is the implementation handoff checklist. Keep each item open until its
stated end-to-end acceptance test passes; a direct protocol call is not enough
when the production TSF sends a different message sequence.

### P0: blocks normal replacement testing

- [ ] `RUST-P0-001` Physical Ctrl+Space chord recognition. *(implementation
  landed, Windows/TSF acceptance pending)*
  - Rust now tracks physical Ctrl/Space down/up state separately from the
    language-bar `ctrl_space` request, gates each chord to one toggle, handles
    either release order, suppresses repeats, and disarms on Shift/Alt/Win or
    Ctrl shortcuts. Protocol tests cover both release orders, repeats,
    blocking modifiers and Ctrl+M/Ctrl+=/Ctrl+number; the language-bar path is
    also covered.
  - Acceptance still requires both physical release orders through the
    installed TSF and confirmation that the bridge's actual key metadata does
    not alter the result.

- [ ] `RUST-P0-002` Build a C#-versus-Rust physical-key trace differential
  harness. *(implementation landed, captured-TSF/C# adapter acceptance
  pending)*
  - `tools/compare_core_key_trace.py` replays the same JSONL events to Rust
    and a C# adapter, compares handled/commit/composition/candidates/selection,
    keyboard/cancellation fields, rejects malformed traces, and requires
    scan/repeat/extended/NumLock metadata unless explicitly overridden.
  - Remaining work is to capture real installed-TSF traces, provide the C#
    adapter, and run the comparison across representative applications; no
    synthetic single-call run closes this item.

- [ ] `RUST-P0-003` Normal-code boundary rollover and empty-code behavior.
  *(implementation landed, Windows/TSF acceptance pending)*
  - `append_normal_code` now examines the extended code at the configured
    boundary, preserves legal terminal/non-terminal paths, performs unique
    max-length auto-commit, applies `空码自动清屏`, and rolls an illegal
    extension into a new composition without losing the physical letter.
    `normal_max_code_rolls_boundary_letter_and_preserves_empty_code_setting`
    covers valid, invalid and empty-code-disabled cases.
  - Acceptance still requires continuous valid/invalid boundary typing through
    the installed TSF and comparison of commit/preedit sequencing with C#.

- [ ] `RUST-P0-004` Honor the configured schema on process startup.
  *(implementation landed, Windows startup/schema acceptance pending)*
  - `main.rs` loads/bootstraps `config.txt` before resolving the schema,
    reserves `--lexicon` for an explicit override, and loads the selected
    directory atomically including secondary `*.txt` files while excluding
    `补充语料.txt`. `startup_resolves_configured_schema_and_loads_secondary_tables`
    covers a non-default schema and secondary word.
  - Acceptance still requires restarting the packaged Windows Core with a
    non-default schema and verifying the same snapshot without `reload_mb`.

### P1: user-visible input behavior

- [ ] `RUST-P1-001` Finish TSF key-state-machine parity. *(implementation
  landed, Windows/TSF acceptance pending)*
  - Rust now handles CapsLock composition commit/pass-through, quote key-up
    fallback, one-shot action re-arm, consumed modifier key-up, repeat
    suppression, NumLock/numpad behavior, and scan/extended virtual-key
    resolution. Protocol tests cover CapsLock, pass-through shortcuts,
    modifier selection, repeat/blocking Ctrl+Space and punctuation paths; the
    quote fallback is implemented at the same state-machine boundary.
  - The remaining acceptance is a real TSF callback trace through
    `KeyEventSink.cpp`, including frontends that omit quote key-down and the
    exact test/commit compensation sequence.

- [ ] `RUST-P1-002` User candidate adjustment and persistence. *(implementation
  landed, Windows/Dialog acceptance pending)*
  - Rust handles Ctrl+number pin/top, Ctrl+Shift+number delete, Alt+number
    advance, durable `用户调整.txt` writes and reload ordering. Automated
    protocol coverage exercises all three operations and reloads the resulting
    table. Confirm the exact reordered candidate behavior through the shipped
    Dialog on Windows before closing this item.
  - C# reference: `TryUserTop`, `TryUserDelete`, `TryUserAdvance` and the
    adjustment storage in `CoreRuntimeState`.

- [ ] `RUST-P1-003` Pinyin reverse lookup. *(implementation landed,
  Windows/TSF acceptance pending)*
  - Rust loads the `拼音反查码表` snapshot, supports backtick entry,
    candidate display, active-table annotations, backspace editing and
    candidate commit. Automated protocol coverage exercises that complete
    path. Confirm frontend key delivery and real TSF rendering on Windows
    before closing this item.

- [ ] `RUST-P1-004` Unlimited mixed-input decoder parity. *(implementation
  landed, C# differential/Windows acceptance pending)*
  - Rust keeps `raw_input` authoritative, rebuilds completed fixed-width
    segments after every edit, preserves unresolved literal-English segments
    and letter casing, retains a selected segment's preferred candidate, and
    restores the correct cross-segment state on backspace. Protocol tests cover
    raw/display prefix handling and completed-segment backspace restoration.
  - Run the physical-key differential harness against long mixed sessions to
    finish acceptance for casing, repeated edits and all boundary candidates.

- [ ] `RUST-P1-004A` Uppercase/literal composition mode. *(implementation
  landed, Windows/TSF acceptance pending)*
  - Rust has an explicit uppercase/literal state entered by Shift+letter,
    preserves subsequent casing and digits, supports timer and Chinese-currency
    macros, and commits via Space/Enter/punctuation. The
    `uppercase_mode_keeps_literal_case_and_currency_timer_macros` test covers
    literal, currency and timer paths.
  - Verify Shift delivery, macro text and commit sequencing through the
    installed TSF before closing the item.

- [ ] `RUST-P1-004B` Quick-symbol code paths. *(implementation landed,
  Windows/TSF acceptance pending)*
  - Rust indexes semicolon, slash, left-bracket and `z` short-symbol heads,
    distinguishes one-key auto-symbols from composition paths, and resolves
    the configured slash output before ordinary punctuation. Protocol coverage
    exercises the slash configuration and punctuation handoff; lexicon tests
    cover short-symbol indexing.
  - Verify all four physical key paths and shipped-table metadata in Windows.

- [ ] `RUST-P1-004C` Right-Control one-key English switch. *(implementation
  landed, Windows/TSF acceptance pending)*
  - Rust handles an idle bare `VK_RCONTROL` as the C# one-key English switch
    after physical Ctrl+Space chord tracking, while retaining generic/custom
    modifier behavior when it is part of a chord or active composition.
  - Confirm the legacy behavior with the installed TSF and real right-control
    scan/extended metadata before closing this item.

- [ ] `RUST-P1-005` Normal candidate paging parity. *(implementation landed,
  Windows/TSF/Dialog acceptance pending)*
  - Rust maintains a normal/pinyin page index, implements the configured
    `- =`, `[ ]`, `Shift Tab/Tab` and `PageUp/PageDown` schemes, applies page-
    relative selection, and resets the page when the code changes or the
    composition clears. Normal Tab/Up/Down pass-through and sentence traversal
    remain separate. `candidate_publication_obeys_page_size` and selector
    protocol tests cover the base behavior.
  - Verify every configured scheme, page-relative selection and the Overlay
    selected-index presentation through the Windows TSF/Dialog path.

- [ ] `RUST-P1-006` Code-table and candidate metadata parity. *(implementation
  landed, shipped-table/Windows acceptance pending)*
  - Rust now loads common text-first/code-first tables and `.dict.yaml`,
    detects UTF-8/UTF-16/Windows text encodings, applies stable frequency
    ordering, reads split/full/construct maps and comments, preserves
    display-versus-commit entries, and scans a schema deterministically while
    excluding resource files. Lexicon tests cover column orders, sorting,
    construct overrides, metadata, Unicode text elements and display/commit
    separation.
  - Compare all shipped schemas and candidate annotations against C# on
    Windows; unsupported legacy metadata variants remain an acceptance risk.

- [ ] `RUST-P1-007` Dialog-visible lexicon operations. *(implementation
  landed, Windows/Dialog acceptance pending)*
  - Rust supports add/delete/pin/move, deterministic construct-code output,
    schema switching, export/error responses, and custom selection-key
    configuration. `get_last_ci` now treats `history_len` as the number of
    most-recent text elements in chronological order, and construct lookup
    uses explicit/fallback maps, removal tokens and Unicode text elements.
    Protocol and lexicon tests cover Dialog commands, adjustments, construct
    output, metadata and selection-key persistence.
  - Exercise the real Dialog against the shipped Core and compare exact
    history, ordering, schema-switch and error responses with C#.

- [ ] `RUST-P1-008` Reload the complete active schema. *(implementation landed,
  Windows/schema acceptance pending)*
  - Rust stores the active schema directory as the reload target, loads every
    supported table file atomically, and refreshes supplements, custom
    selection keys, pinyin data, annotations, construct maps and sentence
    snapshots with the new lexicon generation. Startup and lexicon reload
    tests cover multi-file loading and version changes.
  - Verify pathless `reload_mb` after edits in the real Windows schema
    directory and compare the complete snapshot with C#.

- [ ] `RUST-P1-009` Key-attached caret and fresh-caret publication.
  *(implementation landed, Windows/TSF/Overlay acceptance pending)*
  - Rust applies `caret_x`, `caret_y`, `width` and `height` from key messages
    before processing, records standalone caret notifications, and briefly
    suppresses a newly opened candidate window until a fresh anchor arrives.
    `key_caret_releases_fresh_anchor_suppression` covers the state transition.
  - Verify the actual TSF caret coordinates and Overlay placement across focus
    changes on Windows.

- [ ] `RUST-P1-010` Qwen sidecar request lifecycle and early-commit coupling.
  *(implementation landed, Windows/Sentence acceptance pending)*
  - Rust now uses a latest-only rerank worker, bounds sidecar connection and
    read waits, validates `success`/sequence/generation/raw-code/score count
    and finiteness, and falls back to n-gram order on any failure. It sends
    full committed-prefix candidate text to Qwen and records matching neural
    results as the early-commit constraint for that generation.
  - The source is covered indirectly by generation/early-commit decoder tests;
    run the real Sentence executable and Qwen model on Windows to verify pipe
    timeout, rerank ordering and late-result suppression.

- [ ] `RUST-P1-011` Protocol/version and frontend-specific response parity.
  *(implementation landed, Windows/TSF/Native acceptance pending)*
  - Rust now reports protocol version 2, distinguishes Hook Native from TSF
    and generic callers, emits `ensure_system_layout_en` only on a Hook Native
    language-state transition, and strips candidate arrays/extra fields for TSF
    stages, Hook Native, or responses near the 4096-byte bridge limit. The
    handshake and key protocol paths have automated coverage; compact-shape
    behavior still needs a Windows bridge trace.
  - Verify the exact installed TSF and Native Hook message sequence, including
    long sentence responses and system-layout switching, before closing this
    item.

- [ ] `RUST-P1-012` Core exit/export request semantics. *(implementation
  landed, Windows pipe/UI acceptance pending)*
  - Rust replies successfully before setting the shutdown wake-up path; the
    Windows accept loop is released through a short-lived same-pipe connection
    instead of remaining blocked in `ConnectNamedPipe`.
  - Hook Native receives the existing named exit event. Overlay, Dialog and
    Sentence children are tracked by the actual `Child` handles Rust started,
    receive a grace period, and are then cleaned without a process-name scan
    that could terminate an unrelated application.
  - `export_mb` now writes the C#-shaped UTF-8-BOM file and, on Windows, asks
    Explorer to select it; write/launch failures return `success:false` with
    `error`. Linux protocol tests cover shutdown state, export bytes/path and
    truthful write errors. Verify the named-pipe wake, Explorer selection and
    child cleanup on Windows before closing this item.

- [ ] `RUST-P1-013` Focus/cancel state reset parity. *(implementation landed,
  Windows/TSF acceptance pending)*
  - Rust resets Ctrl+Space/Shift chord state, page tracking, digit-period and
    composition transients on changed focus and external cancellation, while
    preserving `processName`, `className` and `windowTitle` in state for
    downstream compatibility. Caret/anchor state is reset at the same boundary.
  - Verify focus switches and cancellation ordering from real TSF/Hook Native
    notifications; direct protocol coverage does not replace that trace.

- [ ] `RUST-P1-014` Config-change cancellation scope. *(implementation landed,
  Windows/Dialog acceptance pending)*
  - Rust classifies `set_config` changes: decoder/state switches invalidate an
    active composition and emit `cancel_composition`, while UI-only theme,
    font, delay and selector changes remain live. Config reload keeps the C#
    full-reset behavior, and no-composition changes do not report a cancel.
    The transactional setter path is covered by the config/protocol tests.
  - Verify the complete Dialog setting matrix, persistence and cancellation
    edge cases against C# on Windows.

- [ ] `RUST-P1-015` Transactional config/lexicon mutation and truthful errors.
  *(implementation landed, Windows/Dialog acceptance pending)*
  - Rust stages schema reloads and config writes before publishing the new
    snapshot, restores the old state on failure, reports actionable errors for
    missing schemas and failed `add_ci`/config writes, routes object-form
    values through canonical keys, and bootstraps a missing `config.txt` at
    startup. The active schema/lexicon version changes only after a successful
    load or write.
  - Exercise missing-directory, unwritable-file, alternate object-form and
    first-run config cases through the real Dialog on Windows; Linux unit
    coverage does not validate Windows ACL/path behavior.

### P2: fidelity, UI and lifecycle

- [ ] `RUST-P2-001` Exact send-history semantics. *(implementation landed,
  Windows/Dialog acceptance pending)*
  - Rust records handled commits and guessed pass-through text as Unicode
    text elements, handles backspace deletion and smart-quote state, supports
    `{重复上屏}`, and implements chronological `get_last_ci` concatenation
    with the C# 20-element API limit. Protocol tests cover punctuation,
    pass-through history, quote deletion and Dialog history behavior.
  - Verify the complete Dialog history sequence and long-history retention
    against C# on Windows, including the local bounded-history safeguard.

- [ ] `RUST-P2-002` Unicode text-element parity. *(partial implementation
  landed, exhaustive .NET/Windows acceptance pending)*
  - Rust routes candidate metadata, construct-code handling, early-commit
    boundaries, isolation and send history through `text_elements`, with unit
    coverage for combining marks, ZWJ emoji and regional-indicator flags.
  - The helper is a dependency-free approximation of .NET `StringInfo`, so
    exhaustive Unicode edge cases and C# differential results remain a code
    parity risk; validate them before closing this item.

- [ ] `RUST-P2-003` Overlay annotations, splits and typing sound.
  *(implementation landed, WPF Overlay acceptance pending)*
  - Rust derives candidate annotations/splits from the active lexicon and
    publishes them in the UI-state MMF. Accepted key-downs advance `SoundSeq`
    and publish `SoundVk`, volume and delay settings. Lexicon metadata and
    pinyin-annotation tests cover the producer-side data.
  - Verify WPF rendering, annotation expansion and actual sound playback with
    the shipped Overlay on Windows.

- [ ] `RUST-P2-003A` Candidate visibility and selected-index parity.
  *(implementation landed, Windows/TSF/Overlay acceptance pending)*
  - Rust keeps a non-sentence code-only composition visible (subject to fresh
    caret, pending and hide-candidate rules), while sentence visibility still
    follows candidate availability. The Overlay MMF publishes selected index
    `-1` for normal composition and the sentence index only for sentence mode.
    Fresh-caret and page-size protocol tests cover the producer state.
  - Verify code-only rendering, pending-window timing and selected-index
    presentation through the installed TSF and WPF Overlay.

- [ ] `RUST-P2-004` Process/UI lifecycle parity. *(partial implementation
  landed, Dialog/Overlay acceptance pending)*
  - Rust now tracks and cleans Core-owned Overlay/Dialog/Sentence children,
    signals Hook Native cooperatively, consumes `hook_native_disabled`,
    publishes `IsOff`/`IsNativeHook` status, and retries Overlay startup from
    its heartbeat. The remaining Dialog supervision and exact C# retry/backoff
    behavior are not implemented as a complete lifecycle equivalent.
  - Verify shutdown ordering, status rendering, heartbeat restart and Dialog
    lifetime on Windows; do not use process-name scans for unrelated children.

- [ ] `RUST-P2-005` Complete frontend interoperability matrix.
  - Test TSF, ARM64X/x64 sidecar TSF, Dialog, Overlay, Sentence and Native Hook
    in representative desktop, browser, terminal and packaged applications.

- [ ] `RUST-P2-006` Text encoding parity. *(implementation landed,
  Windows legacy-code-page acceptance pending)*
  - Rust routes config, code-table and auxiliary-file reads through the shared
    detector for UTF-8 BOM/no-BOM and UTF-16 LE/BE, with Windows ACP fallback
    and a deterministic non-Windows fallback. `text` and supplemental-loader
    tests cover BOM/UTF-16 and invalid-byte behavior.
  - Verify legacy Windows code-page files and mixed-encoding shipped schemas
    against C# on Windows before closing this item.

### Confirmed aligned during this audit

Do not reopen these as gaps without a failing trace:

- Sentence Ctrl+number is intentionally passed through with
  `cancel_composition=true`; Rust and the C# test contract agree.
- A duplicate `client_session` + `event_id` returns the cached first response,
  preserves its original `commit_text`, substitutes only the current `seq`,
  and does not execute sentence early commit twice.
- Rust already generation-checks completed Beam and rerank results and limits
  Qwen input to the first five candidates. The remaining Qwen lifecycle gaps
  are the narrower items in `RUST-P1-010`.

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

These scripts produce `release\TigerClaw.Core.exe` and
`release_arm64\TigerClaw.Core.exe` for a controlled replacement test. The
ARM64 script keeps the first existing Core as
`release_arm64\TigerClaw.Core.csharp.exe` so the C# executable can be
restored without republishing. MSVC/ARM64 publication remains a Windows
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
