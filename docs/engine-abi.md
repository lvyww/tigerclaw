# TigerClaw Engine C ABI Contract

Status: Phase 1 target contract for the macOS .NET Hybrid route. The C#
NativeAOT library introduced in Phase 2 must implement this contract; Swift and
future Windows adapters must not depend on managed object layout details.

## Versioning and handles

- Every exported symbol is prefixed `tigerclaw_` and reports ABI version `1`.
- `tigerclaw_runtime_t` and `tigerclaw_session_t` are opaque handles. Callers
  never allocate, copy, dereference, or free their storage directly.
- A session belongs to exactly one runtime. Destroying a runtime invalidates
  all its sessions; subsequent calls return `TIGERCLAW_ERR_INVALID_HANDLE`.
- The core lifecycle is `runtime_create`, `session_create`, `session_activate`,
  `session_process_key`, `session_deactivate`, `session_destroy`, and
  `runtime_destroy`.

## Data and ownership

- All ABI text is UTF-8. Strings are byte slices (`ptr`, `len`), are not NUL
  terminated, and reject invalid UTF-8 with `TIGERCLAW_ERR_INVALID_UTF8`.
- Input pointers are borrowed only for the call. A null pointer is permitted
  only when its paired length is zero.
- `session_process_key` returns one opaque immutable snapshot. It owns all
  candidate/preedit/commit bytes and is released exactly once by
  `tigerclaw_snapshot_release`.
- Snapshot accessors borrow their returned slices until `snapshot_release`; a
  snapshot remains valid across later commands and resource reloads.
- No managed object pointer, nested allocated pointer graph, or runtime
  allocator callback is exposed to Swift/C++.

## Snapshot and commit semantics

Each accepted key event returns a monotonically increasing session `revision`.
A snapshot contains the input-mode state, marked/preedit text, caret, visible
candidates, selected index, status, and at most one commit update.

A commit is delivered only by the snapshot returned for the accepted event.
Reading that snapshot repeatedly is side-effect free. Poll/query snapshots
never replay an old commit. If a frontend retries the same event identity, the
adapter returns the original result/revision rather than executing the key
again. This is required for the existing Windows `client_session + event_id`
replay contract and for macOS frontend retry safety.

## Threading and errors

- Calls for one session are serialized by the adapter/runtime. The ABI does
  not promise concurrent calls on the same session.
- Distinct sessions may be used concurrently. Resource reload and schema
  change obey the epoch barrier defined in `engine-architecture.md`.
- Functions return a stable `tigerclaw_status_t`; diagnostics are fetched as a
  borrowed UTF-8 error message valid until the next call on that handle.
- Required statuses: `OK`, `INVALID_ARGUMENT`, `INVALID_UTF8`,
  `INVALID_HANDLE`, `INVALID_STATE`, `OUT_OF_MEMORY`, `RESOURCE_UNAVAILABLE`,
  `STALE_GENERATION`, and `INTERNAL_ERROR`.

## Key event ABI shape

The ABI key-event payload mirrors `TigerClaw.Engine.Input.InputEvent`:

- `key`: TigerClaw semantic key enum, never a macOS key code or Windows VK;
- `action`: key down or key up;
- `modifiers`: bit flags;
- `text`: borrowed UTF-8 logical text, empty for non-text keys;
- `physical_key_code`: frontend physical key identity;
- `scan_code`: Windows scan code when present, otherwise `0`;
- `is_extended`: Windows extended-key marker when present;
- `repeat_count`: at least `1`; greater than `1` marks repeated input.

The adapters may keep extra platform fields internally, but the engine must be
able to decide basic composition behavior from this payload.

## Phase 2 implementation boundary

The NativeAOT dynamic library exports the lifecycle, key-event, status, and
immutable snapshot surface specified here. `session_process_key` creates a real
session snapshot from the new `EngineRuntime` path; snapshot preedit,
candidates, selected index, and event-bound commit data are copied into
ABI-owned storage.
It still does **not** replace the Windows JSON/named-pipe runtime.

The C/C++ declaration will be shipped beside the NativeAOT project, for example
under `macos/TigerClaw.Engine.Native/include/tigerclaw_engine.h`. It is the ABI
authority for Swift bridging headers and future Windows native adapters;
managed type layout remains private.

### Current experimental surface

The executable macOS spike currently publishes
[`tigerclaw_engine_nativeaot.h`](../spikes/engine-nativeaot/TigerClaw.Engine.NativeAot/tigerclaw_engine_nativeaot.h).
It uses the shorter `tc_` prefix while the long-form ABI naming above remains
the production target. The current surface has runtime/session/snapshot
lifetime, explicit UTF-8 slices, immutable runtime configuration (including
unlimited mixed input), and snapshot accessors for preedit caret,
visible-candidate page metadata, `composition_prefix`, and
`active_input_code`. The last two fields let Swift render mixed input without
reproducing segmentation or casing rules. `tc_engine_config` also accepts an
optional `user_dictionary_path`; its tab-separated `text<TAB>code` entries are
promoted ahead of bundled candidates for that exact code. It does not
yet implement ABI version negotiation, replay identity, resource epochs,
reload/schema barriers, or the final diagnostics/status family. Consumers must
therefore treat it as an append-only experimental ABI rather than the macOS 1.0
compatibility promise.

`tc_session_select_candidate` chooses a zero-based candidate from the current visible page without faking a keyboard digit. It is used by AppKit mouse selection so every configured page entry, including a tenth candidate, follows the normal Engine commit path.

Every ABI operation that can report status must catch managed exceptions and
convert them to `INTERNAL_ERROR`; void destruction functions make the same
containment attempt. Invalid or already-freed raw handles remain caller
contract violations and cannot be made safe by exception handling.

The initial canonical key values deliberately align letters and common editing
keys with their historical numeric values, but they are TigerClaw ABI constants,
not macOS key codes or Windows API types. Native adapters must map their own
events to the constants in `tigerclaw_engine.h`.

## Exception and compatibility policy

An unhandled engine exception must not silently terminate the input-method host.
Exported entry points catch managed exceptions, convert them to
`INTERNAL_ERROR`, invalidate only the affected operation when possible, and
record diagnostic context. If a platform/toolchain cannot support that policy,
the library must be isolated in a helper process before it is used by the IME.

ABI v1 is append-only. New optional accessors may be added behind a negotiated
capability/version query; changing ownership, UTF-8, lifetime, status values,
the `snapshot_release` ownership model, or snapshot commit semantics requires a
new ABI version.
