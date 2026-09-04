# TigerClaw Agent Handoff

This is the single project handoff entry. Read it before older notes. Detailed
protocol, user, porting and model documents are indexed in `docs/README.md`.

Last reorganized: 2026-08-28.

## Current Status

TigerClaw is a Windows input method with a split-process runtime. The C# Core in
`next/TigerClaw.Core/` is the sole maintained implementation.

The experimental Rust Core line was stopped and removed on 2026-08-28. Its useful
source and handoff notes are stored outside this repository in
`TigerClaw-RustCore-Source-Archive-20260828.7z`. Do not restore or resume that line
unless the user explicitly requests it.

```text
Windows TSF
  -> BimeTSF2/SampleIME/TigerClaw.dll
  -> \\.\pipe\BimeIPC
  -> next/TigerClaw.Core.exe
       -> optional \\.\pipe\TigerClaw.Sentence.v1
       -> next/TigerClaw.Sentence.exe
  -> UI state MMF / heartbeat
  -> next/TigerClaw.Overlay.exe
  -> next/TigerClaw.Dialog.exe
```

Active components:

- `BimeTSF2/SampleIME/`: bridge-only TSF DLL. It captures key, focus, caret and
  activation events, forwards them to Core, and applies Core responses.
- `next/TigerClaw.Core/`: configuration, lexicons, input state, candidates,
  sentence decoding, IPC and process lifecycle. It is a .NET 10 Native AOT
  executable; there is no maintained .NET Framework Core configuration.
- `next/TigerClaw.Overlay/`: WPF status/candidate UI and typing sounds.
- `next/TigerClaw.Dialog/`: settings, add-word and selection-key UI.
- `next/TigerClaw.Shared/`: shared constants, build identity, MMF and guards.
- `next/TigerClaw.Sentence.Native/`: optional native C++ Qwen reranking sidecar,
  including the named-pipe host and statically linked llama.cpp scorer. Failure
  always falls back to n-gram order.
- `next/TigerClaw.Hook.Native/`: experimental native-hook frontend. Do not make
  it the default workflow unless explicitly requested.

`reference/` is upstream/reference-only. It must not be wired into active build
or publish scripts.

## Runtime Contracts

Main IPC:

- Pipe: `\\.\pipe\BimeIPC`
- Encoding/framing: UTF-8 JSON, one object per line
- Contract: `Protocol/messages.md`
- Handler: `next/TigerClaw.Core/ProtocolHandler.cs`

Sentence reranking:

- Pipe: `\\.\pipe\TigerClaw.Sentence.v1`
- Contract: `Protocol/sentence_messages.md`
- Client/server: `SentenceRerankClient.cs` /
  `TigerClaw.Sentence.Native/SentenceHost.cpp`

UI state:

- `Local\TigerClaw.UiState.v1`: Overlay state
- `Local\TigerClaw.Heartbeat.v1`: Core heartbeat
- `Local\TigerClaw.OverlayHeartbeat.v1`: Overlay heartbeat
- `Local\TigerClaw.ShowMenu.v1`: status-window menu trigger

## Behavior That Must Stay Aligned

- Raw code is authoritative. Mixed input keeps the complete raw composition,
  decodes it again after every edit, performs case-insensitive lookup, and
  preserves casing for display and literal commits. Code masking is display-only.
- Sentence segmentation spaces are display-only. The raw code never contains
  them. Up/Down and Tab/Shift+Tab traverse visible sentence candidates; sentence
  mode leaves Ctrl+number to the target application.
- A one-key sentence segment is legal only when the whole input is one key.
  Other segments consume at least two keys. `;`, `'` and digits select explicit
  lexicon ranks. An implicit non-first rank is legal only when the whole input is
  consumed by one lexicon edge; segmented paths use first ranks unless selection
  is explicit. Empty-code automatic-commit continuations also use first ranks;
  probabilistic early commit must preserve the already-ranked full-sentence paths,
  including eligible non-first single-character segments.
  Multi-character lexicon entries are legal edges. `允许单字重码组句` (default
  on) additionally allows non-first single characters without a selector on
  segmented paths and ranks those paths by language-model score so they can
  become the visible first candidate. A whole-input single lexicon edge still
  keeps first-rank characters ahead of later ranks. It does not bypass
  `高频字仅使用最优码组句` or the full-code whitelist, and explicit digit/`;`/`'`
  rank selection still works when it is on. Multi-character words still need an
  explicit selector on segmented paths.
- Sentence mode is controlled only by `自动启用整句模式` (default on). It
  activates when the current schema name contains `整句`.
- Sentence decoding is latest-generation-only and asynchronous. A stale Beam or
  Qwen result must never replace newer composition state. Pending UI keeps the
  previous candidate list and stitched live raw suffix.
- Modified Kneser-Ney V2 uses `sentence-ngram-v2.bin`, mapped read-only. Beam
  expansion adds `2.0` per emitted Unicode character. Supplemental entries use
  `clamp(9 + 2 * ln(weight / 1000), 0, 16)` and affect sentence ranking only.
- Qwen3 0.6B Q8 reranks exactly the first five n-gram candidates with weight
  `0.84`. Its GGUF is mapped directly and is not encrypted.
- Known limitation: isolated Qwen scoring can worsen very short candidate sets,
  because it scores surface text without Tiger code context. Fix the scorer or
  skip short-text reranking if this becomes material; do not patch candidate
  identity/order plumbing without a failing trace.
- Windows early commit is experimental and defaults off. It requires raw length
  greater than four, exact consecutive one-key generations, confidence mass at
  least `0.995`, and the same raw boundary. Confidence is tracked independently
  for every `(text prefix, raw boundary)` from the visible candidates, plus
  dropped incomplete-tail lattice states when present. A raw
  boundary is eligible when the confidence mass of visible candidates that also
  have a segmentation boundary there reaches `0.99999`; their text need not
  agree. This weighted check prevents negligible crossing paths from vetoing an
  otherwise stable prefix. Two consecutive generations suffice when both prefix
  shares reach `0.99999`; otherwise three evidence generations are required.
  When several prefixes mature, commit the longest,
  then the higher-share and earlier-boundary prefix. Backspace, contradictory
  complete evidence and manual navigation invalidate or suspend evidence. When
  the current key is an incomplete code tail, merge the already computed
  dropped-tail lattice states into the confidence pool so competing prefixes
  such as `上午` and `上窦` are compared; supported closed-boundary prefixes in
  that merged generation also increment evidence. This deliberately accepts a
  small probability of a later segmentation flip in exchange for substantially
  earlier commits. A complete generation whose best
  visible candidate confidence is below `0.995` is also a comparison-only gap
  unless a visible prefix still reaches `0.995` after the merge. Keep a tracker
  only while current merged prefixes still support it; a stronger fork after a
  shared stem drops it. These gaps add no evidence, do not break a supported
  sequence and may span at most three generations. Keep the complete unstable
  suffix and at least
  three uncommitted raw code characters from the completed decode generation.
  `保留最少编码数量` defaults to `0` (no extra limit). A value greater than zero
  raises the leftover-raw-code floor for both probabilistic early commit and
  empty-code automatic commit to that count; probabilistic commit still never
  goes below three.
  `test整句` `uriczwxmjou` must not commit transient
  `可佛`; Tiger `nuusvbbhoi` must finish as `左手匕首` rather than `左手要好自`;
  `iejryfenahbmsp` may commit `新人` but must finish as `新人上午来面试`, never
  `新人上窦...`.
- Empty-code automatic commit is a fixed part of `整句自动提前上屏` (default
  off); there is no separate switch. Before the new key, accept either exactly one group-eligible candidate
  or a group-eligible candidate that is also the visible first candidate and has
  untruncated confidence share at least `0.99999`. Appending an ordinary letter
  must then leave no complete lexicon path; first check whether the selected last
  lexicon segment is still a proper code prefix. Whole-input non-first ranks shown
  for manual selection do not create implicit group ambiguity unless a rank
  selector is present. Defer
  while the segment can grow; if the actual extension goes dead, commit the saved
  candidate's uncommitted suffix, preserve the committed sentence context, and
  retain every appended letter as the next composition. Collecting confidence
  evidence while this setting is enabled may inspect the existing visible list,
  but its key-path check must stay a lightweight lexicon-path test rather than a
  synchronous n-gram/Beam decode.
- Windows early-commit confidence/mass aggregation must run over the already
  narrowed visible/top-candidate list, not the full beam pool. Incomplete-code
  tails may merge the already computed `states[consumedLength]` lattice into the
  evidence pool; supported closed-boundary prefixes may count as a new evidence
  generation, but they must not re-widen the common complete-code path. Re-widening
  the complete path was a real performance regression once (beam pool up to 100x
  the visible list on every keystroke). Fcitx5 Android `tigerclaw_sentence_core`
  received these 2026-09-01 independent-prefix, dropped-tail comparison and
  closed-boundary rules. The Rime Lua port received the same tracker rules,
  the duplicate-single-character switch (default on) and the empty-code
  continuation split on 2026-09-04.
- TSF key requests carry stable `client_session` + `event_id`; timeout retries
  reuse them and Core returns the cached first response without executing a
  physical key twice.
- Overlay owns candidate display. It pins the first caret anchor for a composition
  and flips above the caret when needed. TSF legacy candidate UI is not active.

When changing sentence behavior, update tests and the standalone Rime/C++ ports
only where they intentionally share that invariant. The implementation and tests
remain the final authority for lower-level cache and Beam details.

## Key Code Paths

Startup and IPC:

- `next/TigerClaw.Core/Program.cs`
- `next/TigerClaw.Core/ProcessLauncher.cs`
- `next/TigerClaw.Core/ProtocolHandler.cs`
- `BimeTSF2/SampleIME/PipeClient.cpp`

Input behavior and data:

- `next/TigerClaw.Core/InputMethodEngine.cs`
- `next/TigerClaw.Core/CoreRuntimeState.cs`
- `next/TigerClaw.Core/SentenceInputDecoder.cs`
- `next/TigerClaw.Core/SentenceNgramModel.cs`
- `next/TigerClaw.Core/SentenceSupplementModel.cs`

TSF/UI:

- `BimeTSF2/SampleIME/KeyEventSink.cpp`
- `next/TigerClaw.Overlay/MainWindow.xaml.cs`
- `next/TigerClaw.Overlay/MainWindow.Candidate.cs`
- `next/TigerClaw.Overlay/OverlayStateSource.cs`
- `next/TigerClaw.Dialog/ConfigWindow.xaml.cs`
- `next/TigerClaw.Dialog/CorePipeClient.cs`

Native hook:

- `next/TigerClaw.Hook.Native/App/main.cpp`
- `next/TigerClaw.Hook.Native/App/HookRuntime.cpp`
- `next/TigerClaw.Hook.Native/Hook/KeyboardHook.cpp`

## Build And Test

Initialize the pinned llama.cpp dependency once:

```bash
git submodule update --init --recursive
```

Debug build and smoke test:

```batch
next\build_next.bat
next\register_dev_corepath.bat
next\_run\Debug\x64\TigerClaw.Core.exe --with-overlay
```

After testing:

```batch
next\unregister_dev_corepath.bat
```

Core tests are in `next/TigerClaw.Core.Tests/` and have no external test
framework dependency.

Release commands:

```batch
publish.bat
publish_arm64.bat
```

The main release is `release/`. Windows on ARM development output is
`release_arm64/`; its default uses an ARM64X wrapper with ARM64 and x64 TSF
sidecars. `--diagnostic` enables embedded TSF logging.

`publish_core_arm64.bat` publishes only `next/TigerClaw.Core/` as a
self-contained ARM64 Native AOT executable and replaces `TigerClaw.Core.exe` in
an existing `release_arm64/`, skipping Overlay, Dialog, Sentence, Hook.Native,
Shared.dll and both TSF DLLs for faster Core-only iteration. It does not touch
`EmbeddedBuildInfo.h` or rebuild the TSF DLL. If the deployed TSF DLL was built
with `core_hash_verify_enabled=1`, it embeds the old Core.exe's SHA256 and will
disconnect the pipe against the new binary (`SampleIME.cpp`,
`VerifyCoreExecutableHashCached`/`_EnsurePipeConnected`). Either set
`core_hash_verify_enabled=0` in `publish_config.txt` and rebuild the TSF DLL
once via `publish_arm64.bat`, or accept that a full `publish_arm64.bat` run is
required whenever the embedded hash must match.

Important local rule: this checkout's `release_arm64/` is the user's daily
runtime, not disposable build output. Never delete, clean, replace or partially
rebuild it unless the user explicitly asks. In particular, do not run a broad
`git clean -X` in this repository. Its config, code tables and user adjustments
may exist only in that ignored directory.

Generated models live outside Git under
`C:\Archive\tigerclaw_sentence_ml\runtime`; publish scripts copy them into the
release tree. Keep `.bat` files CRLF.

## Related Ports And Experiments

- `rime/tiger_sentence/`: standalone experimental Rime pack. Regenerate its
  plain-text data files with `python3 tools/export_tiger_sentence_rime.py`.
  The pack includes `symbols.yaml` as its directly-committing punctuation
  default; the schema imports that preset instead of Rime's `default` preset.
  The code table, character ranks and full-code whitelist are runtime-loaded
  txt files (`tiger_sentence.codes.txt` and siblings) so users can edit or
  import other shape-code tables without re-running the exporter; the
  high-frequency optimal-code limit is the schema config
  `tiger_sentence/high_freq_limit` (default 1500, 0 disables) and the exporter
  only cross-checks that default against `dist_config.txt`. Auto commits
  rebuild the composition with one atomic input assignment (no intermediate
  `clear`) to avoid candidate-window flicker. Its synchronous Lua decoder keeps
  a 200-wide Beam through raw position 24 and uses 48 thereafter, preventing
  long low-confidence input from queuing candidate generations. It is not part
  of Windows TSF.
- Open-source mirror `tiger-sentense-rime`
  (https://github.com/lvyww/tiger-sentense-rime, GPL-3.0, public): the
  standalone release of the Rime pack above. Publishing rules:
  - `rime/tiger_sentence/` in this repo is the source of truth. Published
    runtime files (schema, `lua/tiger_sentence.lua`, the three data txt
    files, supplement, `symbols.yaml`, `rime.lua`, `default.custom.yaml`)
    must stay byte-identical to it; only `tools/` (test + bench) gets path
    adaptation (`/rime/tiger_sentence/lua` -> `/lua`, data dir -> repo
    root). The exporter is never published; it is TigerClaw-internal.
  - The mirror README is a standalone rewrite: installation, model download
    from Releases, data-file customization. Do not leak internal paths
    (`release_arm64/`, `dist_config.txt`, dev model paths) into it; the Lua
    module keeps its inert dev fallback paths to stay byte-identical.
  - The n-gram model never enters git (224 MB, above the 100 MB limit); it
    ships only as a Release attachment. Canonical local copy:
    `C:\Archive\tigerclaw_sentence_ml\runtime\sentence-ngram-mobile.bin`
    (md5 77b1d38760fd5efcbdbedf9289c4d6d1).
  - Release procedure per version: sync files into the mirror layout ->
    run the full test suite from the mirror layout (Lua 5.4 with model and
    luajit no-model) -> tag `vX.Y.Z` and push (SSH works) -> build the
    end-user zip `tiger-sentense-rime-vX.Y.Z.zip` from the mirror layout
    plus `LICENSE` and the model, excluding `tools/` (verify CRC, Lua
    byte-identity, model md5) -> upload the Release. This environment has
    no `gh` CLI and no stored HTTPS token, so Release assets are uploaded
    manually by the user on the web UI. v1.0.0 was published this way on
    2026-09-04.
- `/home/yc/fx5/fcitx5-android`, plugin `tigerclaw`: standalone native Fcitx5
  Android port. Core stages and wiring are implemented; real-device performance,
  packaging and signed release acceptance remain. See
  `docs/FCITX5_ANDROID_PORTING_PLAN.md`.
- `tools/SentenceNgramTrainer/`: Windows .NET 10 external-counting trainer.
- Offline neural/model experiments are summarized in
  `tools/README_sentence_neural.md`; generated corpora and models stay outside Git.

## Common Workflows

IPC change:

1. Update `Protocol/messages.md` or `Protocol/sentence_messages.md`.
2. Update `ProtocolHandler.cs` or the Sentence client/server.
3. Update every affected TSF/Dialog/Hook caller.

Key behavior change:

1. Start in `InputMethodEngine.cs`.
2. Check response/UI shaping in `ProtocolHandler.cs`.
3. Change `KeyEventSink.cpp` only for physical capture or TSF commit behavior.
4. Add a regression in `TigerClaw.Core.Tests`.

Settings change:

1. Dialog UI in `ConfigWindow.xaml(.cs)`.
2. persistence/defaults in `CoreRuntimeState.cs` and `dist_config.txt`.
3. Preserve grouped layout, search, dirty-state feedback and empty values.

Release change:

1. Update `publish.bat` or `publish_arm64.bat`. `publish_core_arm64.bat`
   covers Core-only iteration; keep it in sync if `TigerClaw.Core.csproj`'s
   build inputs change.
2. Preserve CRLF.
3. Verify required executables, both TSF architectures, models and licenses.
4. Package `dist_config.txt`, never a developer's live `config.txt`.

## Documentation Policy

- Keep this file as the concise current handoff.
- `docs/README.md` is the document index.
- Put stable message fields in `Protocol/`, user behavior in the user manuals,
  and port-specific details beside the port.
- Delete superseded stage notes instead of adding another entry document.
- Historical documents belong in `docs/archive/` only when they retain real
  diagnostic value. Generated/release payloads are not documentation sources.

## Style And Safety

- The worktree may be dirty. Never revert unrelated user changes.
- Prefer `rg`/`rg --files`; use `apply_patch` for manual edits.
- C#: 4 spaces, surrounding Allman style, `_privateField`, `camelCase` locals.
- C++: log unambiguous code points when debugging IME/window matching; disable
  high-frequency diagnostics afterwards. Avoid raw Chinese source literals for
  runtime matching; construct code points and add a readable end comment.
- Do not wire `reference/` into active builds.
- Do not delete ignored runtime data merely because Git labels it generated.
