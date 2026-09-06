# Architecture Comparison

## Current Recommendation State

`NEED_MORE_EVIDENCE`

The .NET hybrid path is materially stronger after this spike because Swift -> C# NativeAOT C ABI passed and Avalonia Settings passed. The remaining blocker is real `IMKInputController` integration in TextEdit.

## Route Matrix

| Dimension | Pure .NET IMK | Swift + C# sidecar | Swift + NativeAOT C# | Rust |
|---|---|---|---|---|
| Existing Core reuse | High if IMK bindings work | High | High | Low/medium; parity rewrite |
| Rewrite LOC | Low for engine, unknown host | Medium host/IPC | Medium bridge/engine extraction | High |
| IMK risk | High, not validated | Low/medium, Swift host already viable | Low/medium, Swift host already viable | Low/medium host, high engine parity |
| Bridge complexity | Low if bindings exist | Medium, process lifecycle + IPC | Medium, C ABI ownership + bundle loading | Medium, C ABI/FFI |
| Runtime memory | Unknown | Higher, sidecar process | Lower than sidecar; NativeAOT library is 5.8M in dummy spike | Likely low |
| Startup | Unknown | Sidecar startup cost | In-process init, expected low | In-process init, expected low |
| Input latency | Unknown | IPC overhead | Passed command-line smoke at about 1.06 us per ABI call | Likely good |
| Windows regression | Low if extracted cleanly | Low if engine extracted cleanly | Low if engine extracted cleanly | High parity burden |
| Packaging | Unknown | More moving parts | dylib bundling and signing required | dylib bundling and signing required |
| Debugging | Unknown | Easier process isolation | Mixed Swift/native managed-AOT debugging | Rust/Swift debugging plus parity |
| Long-term maintenance | Best only if official IMK binding is stable | Acceptable fallback | Best current candidate if TextEdit IMK passes | Expensive unless C# reuse fails |

## Evidence Collected

- Environment: `.NET SDK 10.0.400`, Xcode `26.6`, `osx-arm64`, `macos` workload installed.
- NativeAOT C ABI: `PASS` in command-line Swift client.
- Bridge symbols: all six expected `_engine_*` exports present in `DummyEngine.dylib`.
- Bridge smoke/perf: `SWIFT_DOTNET_C_ABI_PASS elapsed_us=1386 avg_call_us=0.6919620569146281`.
- Avalonia Settings: `PASS` build, self-contained publish, `.app` packaging, and startup smoke.
- Core portability scan: `13,928 LOC`, with the largest behavior-bearing files falling into minor-change/platform-abstraction buckets rather than Windows-only buckets.

## Missing Evidence

- Pure .NET IMK is unavailable in the installed .NET 10 macOS binding surface:
  the bounded `net10.0-macos` probe cannot resolve `InputMethodKit`, and the
  local reference pack contains neither `IMKServer` nor `IMKInputController`.
- Real TextEdit input was not tested in this run.
- No actual `IMKInputController` has called the NativeAOT engine yet.
- No real TigerClaw C# engine slice has been extracted into a host-neutral `net10.0` project yet.

## Decision

Stay at `NEED_MORE_EVIDENCE`, but narrow the next decision gate to one experiment:

`Swift IMK host -> NativeAOT C# dummy engine -> TextEdit marked text on "a" -> TextEdit commit on Space`.

If that passes, the working recommendation should move toward `RECOMMEND_DOTNET_HYBRID` with `Swift InputMethodKit + C# modern .NET engine + Avalonia Settings`.
