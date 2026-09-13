# Overlay latency experiment

Measured findings: [2026-09-08 Windows ARM64 results](RESULTS-20260908.md).

The driver uses Windows Python and launches only isolated, separately named
test executables. It never replaces `release_arm64`, starts Core, injects keys,
or stops processes by name. The test windows are visible and may cover part of
the desktop while running. Do not interact with their menus during a run.

## Prepare and run

From the repository root, run `python3 tools/OverlayLatencyBench/prepare.py`.
It prints a unique staging directory under `next/_run/OverlayLatency/`.
The source copies retain the actual application code, with mechanical,
exact-match substitutions for isolated endpoints, startup guards/window names,
and timestamp acknowledgements. `manifest.json` records source hashes.
Production source and runtime files are not modified.

In a Windows terminal, substituting that staging directory for `<stage>`:

```powershell
dotnet build <stage>/TigerClaw.Overlay/TigerClaw.Overlay.csproj -c Release -p:PlatformTarget=ARM64
cmake -S <stage>/TigerClaw.Overlay.Native -B <stage>/native-build -A ARM64 -DBUILD_TESTING=OFF
cmake --build <stage>/native-build --config Release --parallel 2
python tools/OverlayLatencyBench/run.py <stage> --count 1000 --rounds 2
```

Use Windows Python, not WSL Python, for named mappings and process APIs. Do not
compile or run another benchmark concurrently with measurement. Both builds
must use the same architecture; the recorded experiment uses Windows ARM64,
native ARM64 C++ and ARM64 .NET Framework 4.8 WPF.

The order is WPF/native, then native/WPF. Four workloads cover nominally fixed
code length, changing width, vertical candidates, and long sentence candidates.
Each group discards 50 warm-up updates and retains 1,000 acknowledgements.
All use the same JSON, font, font size, no sound, and zero reveal delays.
Hexadecimal code text changes each generation; its actual glyph advances can
vary even in the nominally fixed-length case. Long candidates are a stress
case and may be clipped differently at screen bounds.

`results-raw.json` retains sequence IDs and QPC timestamps. `results.json` includes
binary hashes, count, P50/P95/P99/max, working set/private commit and child CPU.
CPU and elapsed time include the 50 warm-ups and pacing delays; they are not
instantaneous CPU percentages. Memory is an end-of-group sample, not a peak.

## What the timestamps mean

- `start`: immediately before the publisher writes the legacy header/payload.
- `published`: after payload publication and FlushViewOfFile.
- `read`: state has been read and parsed, just before notifying the UI.
- `ui`: the UI begins applying that state.
- `done`: that application update returns.

`start -> read` and `read -> ui` have matching meanings in both implementations.
`ui -> done` is **not equivalent rendering work**: native includes synchronous
Direct2D/UpdateLayeredWindow; WPF schedules deferred layout/render work. Neither
endpoint proves desktop presentation or photons. Never label `start -> done`
as measured visible latency, or claim native rendering is slower solely from
that number. Desktop-frame capture/high-speed video is a separate experiment.

This is a one-outstanding-state experiment. Zero missing acknowledgements does
not demonstrate burst/coalescing performance. The writer waits for each update,
then uses deterministic randomized pacing; this is not a real typing trace.
The legacy sequence-before-payload writer order is retained, but these small,
normally completed writes are not a torn-publication correctness stress test.

## Causal diagnostic control

`prepare.py --single-poll` produces a **separate unsafe diagnostic copy** of
native Overlay which omits the second matching-payload poll. Build it separately
and run `run.py <stage> --variants native --scenarios fixed --rounds 2`.
Its manifest/results mark `single_poll_diagnostic: true`.

This measures the latency cost of the extra poll, not a production-ready fix.
Do not deploy this executable: removing the check can accept partially written
legacy snapshots. A proper fix must preserve state consistency, preferably by
defining an explicit safe publication protocol and change notification.

## Synchronized-channel verification

With updated sources, run `prepare.py` without the unsafe option and build that
native staging target. The old `--single-poll` mechanical patch intentionally
fails if its exact legacy patch site has changed; use the archived diagnostic
stage for historical reproduction, not a guessed patch against newer code.

Build the current Core.Tests, then use the actual C# publisher in an isolated
helper process (no Core startup, production IPC, models or keyboard injection):

```powershell
python tools/OverlayLatencyBench/run.py <stage> --variants native --scenarios fixed --count 1000 --rounds 2 --label synchronized-actual --publisher-assembly next/_run/Tests/Debug/net10.0-windows/TigerClaw.Core.Tests.dll
```

`--dotnet` optionally selects the Windows dotnet executable. The helper accepts
only bounded test-session identifiers. It reports QPC timestamps around the
actual `UiStatePublisher.Publish` call; unlike the Python baseline, this timing
also includes the production serializer. The helper is a managed test build,
not the deployed Native AOT Core executable. `--snapshot-publish` is a separate
Python protocol mirror and cannot be combined with the actual publisher option.
