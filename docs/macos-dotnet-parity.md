# macOS Port Parity Matrix

The C# Core is the behavioral authority. “Implemented” means an exact fixture
passes against the frozen baseline, not that a similar UI outcome was observed.

| Capability | C# reference evidence | TigerClaw.Engine (.NET) | macOS frontend | Evidence required to mark complete |
| --- | --- | --- | --- | --- |
| Basic code input and commit | `InputMethodEngine` | implemented in the NativeAOT slice for the frozen basic path | physical IMK path verified in TextEdit | broaden client matrix and exact fixture export |
| Candidate selection and paging | `InputMethodEngine` | suffix normalization, digit/`;`/`'` selection, page metadata and `PagePrevious`/`PageNext` | AppKit panel with caret anchoring (including no-inline-session fallback) and engine-backed mouse selection; physical `2`, `=`, and mouse selection verified | multi-display fixtures |
| Mixed input | `FixedLengthMixedInputDecoder` and engine tests | implemented for authoritative raw code, resolved prefix, active tail, literal English, casing, cross-segment backspace, raw/Chinese commit and current-page soft hint | snapshot fields, persistent host switch, Avalonia setting and installed-host smoke implemented | full physical keyboard fixture set |
| Key replay/idempotency | \`KeyRequestReplayCache\` tests | pending | missing | repeated event identity produces one logical result |
| Schema and lexicon lifecycle | `CoreRuntimeState` | partial: deterministic bundled-schema registry and persisted selected-schema boundary | selected schema is used when a new IMK controller creates its runtime; current bundle has one real schema | multi-schema discovery, external reload and sorting fixtures |
| Runtime basic configuration | `CoreRuntimeState` | partial: persisted bounds-checked ABI settings for candidates, page size, max code length, auto-commit, selection keys and mixed input | Avalonia Settings invokes the installed host's validated command interface; controller rebuilds its session on the next activation | App Group settings bridge and full legacy config semantics |
| User dictionary/adjustment | `CoreRuntimeState` | partial: host-owned tab-separated user dictionary is injected into each NativeAOT runtime and promotes exact-code entries | sandboxed host CLI and Avalonia Settings can list/add/remove entries; controller reloads it on next activation | adjustment ranking, import/export and App Group Settings integration |
| Focus/composition lifecycle | engine and protocol handling | partial: deactivation clears engine state and fixed-code continuations retain the next preedit after an automatic commit | IMK controller now implements composed/original text plus system commit/cancel requests and clears its panel/state during deactivate/close | real focus-switch, click-outside and client-cancel fixture set |
| Sentence n-gram | \`SentenceInputDecoder\` | pending | missing | exact candidate, segmentation and score-order fixtures |
| Async sentence behavior | \`InputMethodEngine\` | pending | missing | generation, pending-retention and stale-result fixtures |
| Qwen rerank | engine and \`SentenceRerankClient\` | pending | missing | generation-safe top-five rerank fixtures |

## Completion rules

1. Each fixture names the frozen C# source test and baseline revision.
2. A capability is complete only when every fixture in its declared set passes
   in the reference exporter, TigerClaw.Engine and relevant macOS integration
   layer.
3. Platform-only behavior is split from engine behavior: macOS tests validate
   key mapping, marked text, commit delivery and UI lifecycle without
   reimplementing decoding in Swift.
4. Model-dependent cases additionally pin the model hash from
   \`docs/macos-dotnet-baseline.md\`.
5. Any known gap remains \`missing\` or \`partial\`; percentages are not used.

## Initial fixture set

`tests/engine_cases/` contains the first portable representatives for basic
real-code commit, mixed input, sentence selection/commit and protocol replay.
The Phase 1 experimental slice consumes the basic fixture; broad C# reference
export and full TigerClaw.Engine fixture replay are later implementation tasks.

## Phase 1 seed status

The first platform-neutral input contract exists in
`spikes/real-engine-slice/TigerClaw.Engine.Experimental/Input/`. The basic
real-code fixture is executable in that Phase 1 slice and verifies real Rime
dictionary load, `a` preedit/candidate generation and Space commit. The later
NativeAOT slice and Swift IMK host now cover the Phase 2–6 vertical path,
including candidate presentation and the mixed-input behaviors listed above.
The Phase 7 host configuration and user-dictionary boundaries are now exercised
through the Phase 8 Avalonia Settings control client. This is still not a broad
engine parity pass: external schema reload, adjustment ranking, sentence
behavior, replay identity and the full client matrix remain pending.
