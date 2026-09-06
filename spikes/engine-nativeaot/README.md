# TigerClaw Engine NativeAOT Spike

Phase 2 isolated spike for exposing the small Phase 1 real-engine slice through a
NativeAOT dynamic library. This directory is intentionally independent from the
Windows runtime and does not connect to IMK, Overlay, or Sentence.

## ABI

The C ABI is declared in
`TigerClaw.Engine.NativeAot/tigerclaw_engine_nativeaot.h`. It is copied to the
publish directory next to the dylib.

All strings are explicit UTF-8 pointer-and-length slices:

```c
typedef struct tc_utf8_slice {
    const uint8_t* data;
    size_t length;
} tc_utf8_slice;
```

The library never requires strings to be NUL-terminated. Runtime, session, and
snapshot values are opaque handles owned by the library.

- `tc_runtime_create(lexicon_path, out_runtime)` / `tc_runtime_release(runtime)`
- `tc_runtime_create_with_config(lexicon_path, config, out_runtime)` for immutable
  basic-input runtime settings (candidate cap, page size, max code length,
  auto-commit, `;`/`'` selection switches, and unlimited mixed input)
- `tc_session_create(runtime, out_session)` / `tc_session_release(session)`
- `tc_session_activate(session)` / `tc_session_deactivate(session)`
- `tc_session_process(session, TcInputEvent*, out_snapshot)`
- `tc_snapshot_get_*` accessors / `tc_snapshot_release(snapshot)`

`TcInputEvent` carries logical text, physical key identity, physical scan code,
extended-key state, repeat state, and repeat count.

Snapshot string slices are borrowed views into the immutable snapshot and remain
valid only until `tc_snapshot_release(snapshot)`.

Snapshots expose the visible candidate page plus the preedit caret, total
candidate count, page index, and page count. `PagePrevious`/`PageNext` are
semantic input keys; the macOS host maps `-`/`=` to them under the current
default paging convention. The ABI also preserves the Rime export's selection
suffixes: for example, `a`, `a2`, and `a;` become the ordered candidates
`来`, `那个` instead of three unrelated code strings.

Mixed-input snapshots additionally expose the resolved composition prefix and
the active raw-code tail. The engine keeps complete raw input authoritative,
preserves casing for literal/raw commits, rebuilds across segment-boundary
Backspace, keeps completed no-candidate segments as literal English, and carries
the current candidate page's first item forward as the soft choice when the next
segment begins.

Sessions are isolated mutable engine instances under one shared runtime. The
smoke test covers two active sessions with different input buffers, then
deactivates/releases session A and verifies session B can still commit. It also
covers mixed casing, resolved/active snapshot fields, page-choice carry-forward,
literal English, cross-segment Backspace, Enter raw commit, Space Chinese commit,
and activation cleanup.

Caller contract: release every non-null runtime, session, and snapshot handle
exactly once. This spike treats null handles as no-ops where documented by the
function behavior, but it does not attempt to detect double release or arbitrary
invalid handles safely.

## Verification

Run from the repository root:

```sh
/Users/wuzz/.dotnet/dotnet publish spikes/engine-nativeaot/TigerClaw.Engine.NativeAot/TigerClaw.Engine.NativeAot.csproj -c Release -r osx-arm64
/Users/wuzz/.dotnet/dotnet run --project spikes/engine-nativeaot/TigerClaw.Engine.NativeAot.Smoke/TigerClaw.Engine.NativeAot.Smoke.csproj -c Release
zsh spikes/engine-nativeaot/SwiftSmoke/build_and_run.sh
```

Expected marker:

```text
ENGINE_NATIVEAOT_PASS
ABI=UTF-8 ptr+len slices
Scenario=A:a -> 来, B:b -> 如, release A, B:Space -> 如
DylibSizeBytes=...
RuntimeInitMilliseconds=...
SteadyKeyCallMicroseconds=...
MemoryBaselineBytes=after-load:..., after-runtime:..., after-smoke:...
```

The Swift smoke test imports the published C header, links the published dylib,
and calls the same runtime/session/snapshot flow. It is intentionally not an
InputMethodKit host integration.

The size and timing numbers are local smoke-test measurements, not benchmark
claims. The boundary is one macOS arm64 process after loading the dylib, with no
IMK host, TSF bridge, Overlay, or Sentence sidecar.

Local measurement sample captured on 2026-08-27:

```text
DylibSizeBytes=1119824
RuntimeInitMilliseconds=9.869
SteadyKeyCallMicroseconds=0.527
MemoryBaselineBytes=after-load:39190528, after-runtime:52412416, after-smoke:54329344
MeasurementBoundary=macOS arm64 process working set; dylib loaded in-process; no IMK host, TSF bridge, Overlay, or Sentence sidecar
```
