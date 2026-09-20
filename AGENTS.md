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

- `next/TigerClaw.Core.Native/`: user-requested parallel C++ Core, **paused at the
  user's request on 2026-09-09 due to usage cost**. Do not automatically resume;
  wait for an explicit user request. The README's opening pause/resume section
  records the latest state, evidence and unfinished work. Compact tables,
  lexicon/config runtime, mixed/sentence decoding and shared event routing are
  implemented and tested in isolation. Main IPC/UI publishing and complete
  frontend hosting/acceptance are not done; it cannot replace the running Core.
  C# remains production default and behavioral authority. Build/output/commands
  are isolated; no production IPC or release deployment. Full completion gates
  and current evidence are in that directory's README; do not equate this initial
  data layer with completing the parallel Core.
- `BimeTSF2/SampleIME/`: bridge-only TSF DLL. It captures key, focus, caret and
  activation events, forwards them to Core, and applies Core responses.
- `next/TigerClaw.Core/`: configuration, lexicons, input state, candidates,
  sentence decoding, IPC and process lifecycle. It is a .NET 10 Native AOT
  executable; there is no maintained .NET Framework Core configuration.
- `next/TigerClaw.Overlay.Native/`: mainline C++ Win32/Direct2D/DirectWrite
  status/candidate UI and typing sounds, promoted at the user's request on
  2026-09-09. Release x64/ARM64 and debug builds default to this implementation.
  `next/build_overlay.bat` is the shared build entry; it does not deploy.
  `--demo` remains isolated from production IPC. See its README for validation.
- `next/TigerClaw.Overlay/`: retained WPF fallback and display-parity reference.
  Set `TIGERCLAW_OVERLAY_BACKEND=wpf` before building to explicitly use it.
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

Native Overlay's low-latency path adds `Local\TigerClaw.UiState.v1.Snapshot.v2`
with a `.Lock` mutex and `.Changed` event. Core retains v1 for WPF, then publishes
a mutex-protected complete snapshot and signals after unlock. Native reads it
without a second polling interval; old Core retains the legacy fallback. Never
block Core on a paused reader. Contract: `Protocol/ui_state.md`.

## Behavior That Must Stay Aligned

- Main and pinyin candidate tables use immutable `CompactLexicon` binary images
  with pooled UTF-16 text and insertion-order-preserving candidate ranges.
  Pinyin is runtime-owned, never schema-snapshot-owned: recent-schema switching
  shares it; full reload replaces it. User edits publish a new main-table image
  and preserve existing adjustment persistence. Ordinary/pinyin pagination reads
  only the requested range. TXT/YAML remain authoritative; no disk cache or idle
  unloading is introduced. See `docs/COMPACT_LEXICON.md` and compact lexicon tests.
- Raw code is authoritative. Mixed input keeps the complete raw composition,
  decodes it again after every edit, performs case-insensitive lookup, and
  preserves casing for display and literal commits. Code masking is display-only.
  After sentence auto commit, literal-code exits (Enter, CN/EN switching and
  CapsLock) emit only the uncommitted raw suffix, never the retained decoder context.
- Schema switching through shortcuts or `set_config` migrates only uncommitted
  raw code, clears old candidate preferences and sentence prefix constraints,
  and rebuilds for the target schema. Preserve raw casing across sentence mode.
  With mixed input disabled, short codes use ordinary composition; an imported
  code longer than maximum code length uses a temporary mixed composition.
- Sentence segmentation spaces are display-only. The raw code never contains
  them. Up/Down and Tab/Shift+Tab traverse visible sentence candidates; sentence
  mode leaves Ctrl+number to the target application.
- Windows, Rime and Fcitx5 sentence input: Tab/Shift+Tab highlights without submitting. The next
  code letter locks that candidate's text and consumed raw boundary, retaining
  language-model context and preventing later resegmentation across the boundary.
  With early commit enabled, this manual confirmation immediately submits only
  the uncommitted selected text; confidence and retained-code floors do not delay
  it. Otherwise Backspace unlocks when it reaches the noncommitted boundary.
  Rank selectors still edit the current segment rather than confirm a Tab lock.
  Up/Down alone does not arm locking. Clear/schema migration discards locks;
  literal exits still emit only live raw code. Rime stores the locked text/raw
  boundaries in a length-framed context property shared by the separate processor
  and translator environments; confidence trackers remain processor-local.
- A one-key sentence segment is legal only when the whole input is one key.
  Other segments consume at least two keys. `;`, `'` and digits select explicit
  lexicon ranks. An implicit non-first rank is legal only when the whole input is
  consumed by one lexicon edge; segmented paths use first ranks unless selection
  is explicit. Empty-code automatic-commit continuations hide whole-input
  non-first edges, but retain decoder-approved segmented duplicate-single paths
  when `允许单字重码组句` is on (for example `xrxbj` must retain `反刍` after
  committing `反`); non-first multi-character words still need a selector.
  Probabilistic early commit must preserve the already-ranked full-sentence paths,
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
  Manual sentence navigation freezes Qwen ordering for that generation; editing
  raw code re-enables reranking. Apply a Beam generation only once, including
  when a synchronous key-path completion races the asynchronous worker.
- Modified Kneser-Ney V2 prefers `sentence-ngram-mobile.bin` (TCSKNM02), with
  `sentence-ngram-v2.bin` (TCSKNM01) compatibility; both are mapped read-only.
  Search Models/ then runtime root for mobile, then the same legacy locations.
  Mobile context-position caches belong to query sessions and are bounded;
  model pages remain OS-managed. Preserve zero-valued observed records, empty
  contexts' backoff weights, and includeUnigram=false scoring. Windows release
  packages use only legacy v2 layout by user preference (download size); they
  retain the current fused model parameters. Debug builds and Rime use mobile.
  Core-only upgrades do not replace models. Beam
  expansion adds `2.0` per emitted Unicode character. Supplemental entries use
  `clamp(9 + 2 * ln(weight / 1000), 0, 16)` and affect sentence ranking only.
  A whole-input single-character candidate gets a ranking-only `5.0` reward
  when the unsplit input is that character's shortest available code (source
  order breaks equal-length ties), regardless of its rank under that code. The
  reward does not enter confidence mass or apply to an explicitly selected rank.
- Compact final ranking adds `2.0 × code length` for primary-code single-character
  edges, removes isolation penalties only for primary/explicit single-character
  edges of at least four codes, and gives the original Top-5 a bounded 50k-word
  Bloom-filter vote. None of these enter Beam or confidence mass; without an
  n-gram model they are disabled. Qwen consumes the resulting base Top-5 and
  keeps its existing fusion formula. See `docs/COMPACT_RANKING_PRIORS.md`.
- Qwen3 0.6B Q8 reranks exactly the first five n-gram candidates. If the
  pre-Qwen base winner has 2..6 text elements, score each candidate by
  `(1-alpha)*BaseScore + alpha*QwenScore`: its own length 2 uses alpha=0.15,
  3..6 uses 0.30. Candidate lengths outside that range use the original
  lambda converted to alpha=lambda/(1+lambda) (one character lambda=0.30,
  longer/unknown lambda=0.84). If the base winner itself is outside 2..6,
  preserve the original whole-set additive policy, without extrapolation.
  This supersedes the intermediate base-length shared-weight calibration.
  The 2026-09-08 grouped experiments are documented in
  `tools/SentenceLengthEval/README.md`. A subsequent frozen 50,000-case,
  record/text-disjoint confirmation achieved 99.142% versus the original
  99.006% and intermediate 99.066%; its net +38 over the intermediate policy
  came from mixed-length sets. Three-character targets still regress versus
  the intermediate policy (99.03% vs 99.16%), but beat the original 98.83%.
  These are context-free snippet benchmarks, not real typing acceptance.
  Its GGUF is mapped directly and is not encrypted.
- Sentence residency follows both schema and settings: preload in the background
  only when sentence input and neural reranking are enabled; otherwise cancel
  pending work and release the owned Sentence process. Never unload on idle.
  Rapid off/on transitions must finish release before reloading; late scores
  from the previous lifecycle must not reach the active composition.
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
  lexicon segment is still a proper code prefix. Non-first multi-character words
  shown for manual selection do not create implicit group ambiguity without a
  rank selector. With duplicate-single grouping enabled, eligible non-first
  single characters (including whole-input edges) and decoder-approved segmented
  paths participate in both uniqueness and strong-confidence comparisons.
  The exact precommit query must apply the same eligibility, independently of
  Beam pruning: `ot` = `是/题`, `qm` = `目` must not falsely commit `是` at `otq`.
  Defer
  while the segment can grow; if the actual extension goes dead, commit the saved
  candidate's uncommitted suffix, preserve the committed sentence context, and
  retain every appended letter as the next composition. Collecting confidence
  evidence while this setting is enabled may inspect the existing visible list,
  but its key-path check must stay a lightweight lexicon-path test rather than a
  synchronous n-gram/Beam decode.
  A single visible group-eligible candidate is not proof of uniqueness after
  Beam pruning. Immediately before committing through the unique-candidate
  branch, check the base raw code for another group-eligible output using an
  exact lexicon-path query independent of Beam. Different segmentations of the
  same text do not count as different outputs. The strong-confidence branch
  keeps its existing acceptance rule. Windows, Rime and Fcitx5 share this check.
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
  continuation split on 2026-09-04. Fcitx5 Android received the duplicate-single
  switch, continuation split, full-code whitelist and minimum-retained-code
  setting on 2026-09-05. Its native decoder keeps Beam 2000 through raw position
  24 and uses 48 thereafter; host benchmarks and real-device acceptance remain
  separate because the runtime and UI scheduling differ from Rime.
- TSF key requests carry stable `client_session` + `event_id`; timeout retries
  reuse them and Core returns the cached first response without executing a
  physical key twice.
  Pipe requests reject reentry; TSF timers defer pipe work while a request is
  active. Read/write share one request deadline, with cancellation completion
  drained before releasing I/O storage (cleanup may exceed that deadline).
  Failed-key replay keeps event IDs and FIFO order, yields after 8 events or a
  60 ms batch budget, and resumes on a 30 ms timer under the existing queue TTL.
  Standalone Windows pipe fault tests: `tools/test_tsf_pipe.bat` (isolated pipe).
- Core startup is limited to a non-service user in a nonzero session on
  `WinSta0\Default`. TSF checks before scheduling and launching Core; Core
  independently checks before registration UI, singleton ownership or IPC so
  older installed TSF DLLs cannot launch a SYSTEM Core from the logon desktop.
  Startup checks: `tools/test_tsf_startup.bat` and Core tests
  `--startup-context-tests`. Do not bypass the guard to support a service host.
- TSF bypasses protected input before modifier tracking, cached responses or
  key IPC: secure activation, non-user desktop/account, standard password
  Edit/RichEdit controls and TSF keyboard-disabled contexts. Bypass clears
  local pending responses/events/replay queues and timers; enqueue, replay and
  response application recheck protection. Ordinary uncertain requests retain
  their retry/dedup policy. Tests: `tools/test_tsf_protected_input.ps1` and
  `tools/test_tsf_startup.bat`. Custom password controls without OS metadata
  cannot be identified reliably; no input text is inspected for detection.
- Native Hook also snapshots each physical key with stable replay identities.
  Uncertain requests are held and retried FIFO (128 events, 5 s TTL; batches of
  at most 8 events / 60 ms, resumed by the 200 ms state pump). Expiry, overflow
  or focus changes discard pending events and order composition cancellation
  before subsequent keys; never pass through a key whose result is unknown.
  Focus publication remains pending until sent and reconnects resynchronize it.
  Left/right modifiers are tracked independently. Diagnostics are opt-in via
  `TIGERCLAW_HOOK_DIAGNOSTICS=1` and omit input/commit text and window titles.
  Isolated frontend tests: `tools/test_hook_native.bat` (no global hook).
- Manual add-word and recent-schema switching keep their existing enable flags
  and use configurable exact modifier chords. Defaults remain `Ctrl+=` and
  `Ctrl+M`; TSF and Native Hook both route these actions through Core.
  Settings show only the two shortcut rows. Disabled bindings display `清空`;
  the `修改` dialog clears or restores defaults immediately into the unsaved
  settings page. Legacy enable flags remain internal compatibility data.
- Overlay owns candidate display. Ordinary word commits experimentally leave an empty native candidate frame
  at its published rectangle when `上屏后候选窗驻留时间(毫秒)` is positive
  (0..60000, default 0/off). Core publishes `CandidateResidenceDurationMs`
  and the absolute `CandidateBackgroundUntil` deadline. A new ordinary composition
  can animate from it; focus/cancel/English/off/hiding/invalid geometry revoke it.
  Fresh-caret gating and candidate reveal delay in the first resumed ordinary
  session preserve only the blank frame within the original deadline.
  A fresh ordinary commit during that wait or an unfinished animation starts
  another configured-duration residence at the actual displayed rectangle.
  It never preserves text or extends expiry on caret updates. Real-window probe:
  `overlay_pending_frame_tests.exe --blank-residence`.
  It retains an already published nonempty
  candidate/code-only frame while
  sentence decoding is pending in the same `CandidateFrameSession`. This also
  covers another key after a completed empty decode, without a hide/show flash.
  Clear/focus/session changes and explicit hiding still invalidate retention;
  no hidden frame is resurrected and no placeholder is created while pending.
  It pins the first caret anchor for a composition
  and flips above the caret when needed. TSF legacy candidate UI is not active.
  Native menus open without waiting for Core schema queries and use a dedicated
  temporary host independent of candidate/status visibility. Foreground permission
  is best-effort, not a display prerequisite; menu-lifetime outside-click/Escape
  monitoring provides fallback dismissal. Schema submenu command IDs are fixed
  to the list shown at expansion, never remapped by a later Core reply.
  Temporary pinyin reverse lookup always shows available splits, full codes and
  comments without annotation delay, irrespective of normal annotation settings.

When changing sentence behavior, update tests and the standalone Rime/C++ ports
only where they intentionally share that invariant. The implementation and tests
remain the final authority for lower-level cache and Beam details.

Windows sentence Tab learning from PR #4 is integrated locally with Smart mode
remaining removed. `整句Tab自学习` defaults on; only corrected text acknowledged
as successfully committed by the updated TSF is learned. Journals stay in each
schema source directory. Learning scores use immutable indexes; learned search
results cannot supply automatic-commit confidence. Existing candidate-length Qwen
weights remain in effect, with the learning reward added afterwards. Hook does
not gain learning receipts from this Windows change. The Rime and Fcitx5 ports
now share correction scoring, legal search retention and automatic-commit isolation;
they learn after host submission, not a Windows TSF/application insertion receipt.
Rime uses schema-scoped LevelDb and `tiger_sentence/tab_learning` (default on);
Fcitx5 uses an asynchronous TCL1 journal and `TabLearning` (default on). Both
settings also cover direct non-first candidate taps: compare against the current
first path, do not reinforce first-choice taps, and consume each submission once.
Rime and Fcitx5 correction weights now align with supplemental corpus weights:
each confirmation adds 1000, using `clamp(9 + 2 * ln(weight / 1000), 0, 16)`.
The 30-day half-life applies to weight. Existing journals are replayed under
this rule. Windows now uses the same 9/10.39/11.20 confirmation rewards and
16-point cap (2026-09-19), including replay of existing journals; confidence
maturity inverts this curve, and stable top1 reinforcement stops at 11 points.
Real-model `zhhbi` needs two corrections to promote
`虎娘` over `其父`; real librime tests cover taps, Tab/space and engine restart.
Rime's shared manual-confirm notification also covers keyboard selection. See each
port README for persistence and acceptance limits. See `TAB_LEARNING.md` and
`Protocol/messages.md`; tests include 10,000-record index pressure and score
equivalence. Actual application commit acceptance remains separate from tests.

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
- `next/TigerClaw.Overlay.Native/main.cpp`
- `next/TigerClaw.Overlay.Native/Renderer.cpp`
- `next/TigerClaw.Overlay.Native/Transport.cpp`
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

`publish_no_qwen.bat` runs the x64/Win32 release pipeline with `--no-qwen`:
keeps the n-gram model, Sentence host and llama.cpp license, skips the external
Qwen model/license requirement and copy, and produces `虎爪输入法-<版本>-no-qwen.7z`.
Packaging excludes all GGUF files, including leftovers in an existing release;
the source release model files are preserved. The normal package is unchanged.
Users can obtain the model through the Dialog's neural-rerank setting guide.
WSL packaging regression: `python3 tools/test_publish_no_qwen.py` (Windows 7-Zip
required, isolated fixtures only).

The main release is `release/`. Windows on ARM development output is
`release_arm64/`; its default uses an ARM64X wrapper with ARM64 and x64 TSF
sidecars. `--diagnostic` enables embedded TSF logging.

`publish_core_arm64.bat` publishes only `next/TigerClaw.Core/` as a
self-contained ARM64 Native AOT executable and replaces `TigerClaw.Core.exe` in
an existing `release_arm64/`, skipping Overlay, Dialog, Sentence, Hook.Native,
Shared.dll and both TSF DLLs for faster Core-only iteration. It does not touch
`EmbeddedBuildInfo.h` or rebuild the TSF DLL. Current TSF and Native Hook do
not bind connections to a Core executable hash or require querying its path.
Trial expiry checks and metadata have been removed; protocol handshakes remain
enforced. Legacy publish configuration keys for hash verification and expiry
are ignored. Previously installed frontends retain their old hash/expiry checks
until replaced with a new frontend;
replacing Core alone cannot change an old DLL's behavior.

`publish.bat` rebuilds TSF into `next/_run/Release/tsf/{Win32,x64}/` with
separate `obj/publish-x64-tsf/` intermediates, never selecting legacy output
paths. Before packaging it checks DLL PE architecture and source/copy identity.
Isolated packaging tests: `tools/test_publish_tsf.ps1`. These checks do not
replace real 32-bit WPS acceptance (loaded DLL identity, input, candidates,
commit and reconnect); that acceptance remains pending until tested in WPS.

`publish_arm64.bat` schedules dependency-aware build tasks with a default limit
of 2 (`--jobs N`, 1..16; `--serial` selects 1). Embedded metadata is independent
of Core and precedes every native frontend including the ARM64X wrapper. Overlay and
  Dialog remain serialized to support the WPF fallback's shared build/output files. Each
task has stdout/stderr logs and a timing CSV under
`next/_run/ReleaseArm64/logs/`. Failed builds drain active workers and never
enter deployment. `--build-only` validates artifacts but skips process shutdown
and release-directory writes; it still builds and updates build metadata.
Model files copy directly from their canonical sources at deployment, not via
the intermediate build outputs. Scheduler isolation tests:
`powershell -NoProfile -File tools/test_publish_arm64.ps1`.

Important local rule: this checkout's `release_arm64/` is the user's daily
runtime. Standing user authorization (2026-09-10): after each Overlay adjustment
is built and verified, deploy the ARM64 Overlay executable into `release_arm64/`
automatically, retaining a recoverable backup and restarting only that deployed
Overlay if needed. This authorization does not cover Core or other components,
configuration/code tables, or cleaning the release directory.

Outside that Overlay-only authorization, `release_arm64/` is the user's daily
runtime, not disposable build output. Never delete, clean, replace or partially
rebuild it unless the user explicitly asks. In particular, do not run a broad
`git clean -X` in this repository. Its config, code tables and user adjustments
may exist only in that ignored directory.

Generated models live outside Git under
`C:\Archive\tigerclaw_sentence_ml\runtime`; publish scripts copy them into the
release tree. Keep `.bat` files CRLF.

## Related Ports And Experiments

- `rime/tiger_sentence/`: standalone experimental Rime pack.
  Synced public runtime through PR #18, `bd83900`, on 2026-09-19.
  The distributed schema defaults to `tiger_sentence/memory_profile: compact`;
  `balanced` remains an explicit custom-patch alternative. Safe locked tail
  Backspace reuses the active lattice; text-only buffered deletion skips scoring.
  Optional `tiger_sentence_early_commit_to_preedit` (default off) buffers early
  confirmations in preedit until host submission. Backspace removes live code,
  then buffered Unicode characters without restoring codes. Deferred learning
  is cancelled on deletion/cancel; schema changes cancel the buffer. The native
  ASCII composer wrapper uses `tiger_sentence_ascii.schema.yaml` as a private
  configuration so raw/inline exits cannot emit the internal marker. Update
  rime.lua, both Lua modules and both schemas together. Real-host regression:
  `tools/test_rime_preedit_integration.py` and `tools/rime_preedit_probe.cpp`.
  The three sentence switches have no `reset`: user choices persist in
  `tiger_sentence.options.yaml`, with first-use `tiger_sentence/option_defaults`.
  New sessions restore preferences; existing sessions synchronize before input.
  Config I/O occurs only on initialization/toggles. Keep this user-owned file
  across upgrades; do not distribute it. Tests: `tools/test_rime_options_integration.py`.
  Regenerate its plain-text data files with `python3 tools/export_tiger_sentence_rime.py`.
  Keep Lua 5.5 compatibility: never assign to a `for` control variable; use a
  separate local for converted values. Run `tools/run_regressions.py` with
  Lua 5.5, Lua 5.4 and LuaJIT when changing the runtime module.
  Learning uses immutable per-code aggregate partitions and lazy score epochs;
  normal confirmation and minute refresh do not replay the whole journal.
  `learning.build` remains the independent full-replay test oracle. Clock
  rollback/future timestamps retain a replay fallback. Preserve database hash
  arithmetic, durable-write-before-publish and the 64-code prefix-search bound.
  Performance evidence and commands: `tools/RIME_PERFORMANCE.md`.
  The pack includes `symbols.yaml` as its directly-committing punctuation
  default; the schema imports that preset instead of Rime's `default` preset.
  The schema disables built-in `digit_separators` so punctuation after digits
  follows that table without a pending ASCII separator candidate. Lua retains
  the immediate decimal point after a digit.
  Lua predecodes mobile-model unigrams, lazily scores isolation from path
  prefixes without changing Beam pruning, materializes segmentation on display
  access, and reuses final candidates when adding same-generation evidence.
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
  received PR #1 on 2026-09-12 (merge `a19c38e`), synced back locally: caret-aware
  edits, lazy-menu Tab preparation and whole-decode model-failure fallback.
  Rime confidence now uses retained full Beam rather than display Top-20 and
  propagates ancestor truncation, potentially delaying automatic commits;
  Windows keeps its existing narrowed-pool policy. Details and tests:
  `rime/tiger_sentence/RIME_CORRECTNESS.md`, `tools/run_regressions.py`.
  The mirror
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
    (2026-09-19 fused/pruned 214.08 MiB; SHA256
    `23216acd8319885aa2431ffbf2231dab4677c5d4abb55a08a404450a15b865ca`).
    The legacy Windows TCSKNM01 copy holds the same parameters (259.97 MiB);
    Windows release packages use this v2 layout; Rime keeps mobile. Conversion,
    identities and evaluation limits are in `tools/README_sentence_neural.md`.
    Local replacement does not upload or replace public Release attachments.
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
   Category-internal display order is defined by Dialog `ConfigSettingOrder.cs`;
   fixed and dynamic rows share this order. Unknown keys sort last within their
   existing category, using ordinal case-insensitive key order.

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
