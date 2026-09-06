# TigerClaw Cross-Platform Engine Architecture

Status: Phase 1 contract, frozen against the C# baseline in
[`macos-dotnet-baseline.md`](macos-dotnet-baseline.md). This document defines the target
boundary; it does not authorize a Core refactor before Phase 2.

## Runtime shape

```text
EngineRuntime
├── SharedResources (immutable, versioned snapshots)
│   ├── configuration and schema registry
│   ├── lexicon/user-adjustment snapshot
│   └── sentence n-gram and optional Qwen resources
├── InputSourceGlobalState
│   ├── selected schema and persisted user adjustment
│   └── Chinese/English mode (`keyboard_open`)
└── SessionManager
    ├── Session A
    ├── Session B
    └── Session C
```

`EngineRuntime` is process-scoped. A session is an input-client composition,
not an application process, window, or operating-system input source.

## State ownership matrix

| State | Owner | Contract |
| --- | --- | --- |
| Raw input buffer, resolved prefix, marked/preedit text | Session | Never shared; cleared on deactivation or host cancellation. |
| Candidates, selected index, paging, commit delivery revision | Session | Snapshot output for one session only. |
| Mixed-input state and literal casing | Session | Raw input stays authoritative per composition. |
| Sentence lattice, incremental cache, generation and async rerank state | Session | Every result carries `session_id + generation_id`; a stale result is discarded. |
| Focused client, caret, application/focus generation | Session | Frontend supplies and owns the client identity; it is not globally inferred. |
| Lexicon, n-gram, Qwen model, immutable configuration and schema registry | SharedResources | Read through a pinned resource epoch; never copied per session. |
| User adjustments and schema selection | InputSourceGlobalState | Persisted per user/input source. A schema change is a global barrier, not a per-session preference. |
| `keyboard_open` / Chinese-English mode | InputSourceGlobalState | The C# Core exposes one central `_engine.IsChinese` through `response.keyboard_open` and Windows mirrors it into the TSF keyboard-open compartment. macOS therefore treats it as one selected-input-source mode, not a separate per-client flag. |
| Reload request | EngineRuntime | Produces a new immutable resource epoch atomically; see below. |

## Platform-neutral input event

Phase 1 starts with an explicit input contract instead of letting Windows VK
remain the engine language. The initial .NET contract lives under
`spikes/real-engine-slice/TigerClaw.Engine.Experimental/Input/` and records
both semantic and physical data:

- semantic key (`Character`, `Digit`, `Space`, `Backspace`, candidate-selection
  punctuation, arrows and paging keys);
- logical text, preserving Shift/Caps-derived casing when text exists;
- key action (`KeyDown` or `KeyUp`);
- modifier flags (`Shift`, `Control`, `Option`, `Command`, `CapsLock`,
  `NumLock`);
- physical key identity from the frontend, including Windows scan/extended data
  when available;
- repeat count/repeat marker.

Windows maps its current `vk`, `scan`, `extended`, `repeat` and modifier fields
through `WindowsInputEventMapper`. macOS maps `NSEvent` through
`MacKeyMapper.swift` before the event reaches the future ABI layer. Neither
adapter should discard platform physical identity, because custom selection
keys and retry/idempotency behavior depend on distinguishing logical text from
the actual key that was pressed.

Caret has two separate meanings and must stay split:

- engine preedit caret/selection belongs in the session snapshot;
- screen position for candidate placement belongs to the Swift/AppKit host and
  is not part of the engine's text state.

## Minimum host-neutral dependency closure

Phase 1 must not copy all of `CoreRuntimeState` into the macOS runtime. The
first implemented basic-input slice lives under `spikes/real-engine-slice/`.
It consumes the real Rime dictionary fixture, normalizes the Rime export's
selection suffixes into ordered candidates (`a`, `a2`, `a;`), and covers basic
candidate selection/paging in isolation. It intentionally stops before mixed
input, schema reload, user dictionary updates or sentence mode. The next useful
closure for broader parity is:

- lexicon lookup and deterministic candidate ordering;
- readonly engine configuration needed by `InputMethodEngine` for basic code
  input, selection keys, paging and commit/clear behavior;
- schema identity plus max-code/selection-key settings;
- user lexicon/user adjustment as an abstract provider, initially no-op or
  fixture-backed for tests;
- per-session composition state: raw buffer, resolved prefix, candidate list,
  selected index and pending commit.

The following concerns remain outside the host-neutral engine boundary:

- Windows named pipes and JSON response shaping;
- memory-mapped overlay state and heartbeat;
- WPF/Avalonia process launching;
- Registry/default path probing;
- macOS App Group or sandbox path decisions.

## Lifecycle and composition policy

```text
create → inactive → activate → active
       → input/focus → deactivate → inactive → destroy
```

macOS mapping is fixed as follows:

| InputMethodKit callback | Engine operation |
| --- | --- |
| `IMKInputController.init` | `create_session` |
| `activateServer` | `session_activate` |
| `handle(event)` | `process_key` |
| `deactivateServer` | `session_deactivate`, clear marked text, hide UI |
| controller close/deinitialization | `destroy_session` after deactivation |

An unconfirmed composition is **cleared, never committed**, on deactivation,
input-source switch, app/client loss, or an explicit host composition-cancel.
The frontend clears its marked text first and tells the engine to invalidate the
same session generation. This matches the C# bridge's `composition_canceled`
notification path and prevents accidental text insertion into the next client.
`deinit` is only a final cleanup fallback and is never the composition policy.

Fixture policy to implement in Phase 2/4:

- focus move with a live composition: no commit; old marked text/candidates vanish;
- input-source switch: no commit; old session becomes inactive;
- explicit host cancel: same clear result, idempotently;
- app switch then return: a new active generation, never a stale candidate list.

## Reload and schema-change barrier

Configuration/lexicon reload builds a complete `SharedResources` snapshot off
the session executors. It is published by one atomic epoch swap. Sessions pin
the epoch while a command runs; the next command observes the new epoch.

Changing the selected schema, or any reload that can alter candidate meaning,
is stronger than an ordinary read: the runtime serializes the change, clears
all active compositions without commit, publishes the new epoch, and then lets
new input start from empty buffers. Existing sessions do not retain a mutable
reference to old resources. User adjustments follow the same snapshot/epoch
rule; writes are persisted first, then published as a new immutable view.

## Threading

Each session has one serial command executor. Calls for different sessions may
run concurrently after obtaining their immutable resource epoch. A resource
reload/schema barrier takes precedence over new key commands only at command
boundaries; it never interrupts a currently executing `process_key`.

Async sentence/Qwen work may run elsewhere, but its completion must include
the originating `session_id`, `generation_id`, resource epoch, and output
revision. The session executor accepts it only when all four still match.

## Windows adapter contract

The current Windows Core owns one `InputMethodEngine`; BimeIPC has
`client_session` and `event_id`, but no create/destroy-session messages.
Phase 1 therefore freezes the existing Windows behavior as **one logical
`windows-primary` Engine session**. `client_session + event_id` is an idempotent
physical-key identity for `KeyRequestReplayCache`, not permission to infer a
new engine session. Focus/caret and `ime_active` update that one session.

Phase 2 may introduce multi-session Windows routing only with an explicit
adapter migration and regressions proving that a retry with the same
`client_session + event_id` executes once, timeout replay returns the original
response, focus/IME-active semantics remain intact, and no existing TSF IPC
message shape changes.
