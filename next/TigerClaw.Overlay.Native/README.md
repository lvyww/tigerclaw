# Native Overlay (in development)

C++17 / Win32 / Direct2D / DirectWrite replacement for the WPF Overlay only.
Core, TSF, Native Hook and Dialog remain unchanged. The resulting executable is
named `TigerClaw.Overlay.exe`, so the existing Core launcher can use it without
a protocol or settings migration. WPF remains the default published frontend.

## Build

Renderer reuse measurements and the isolated pixel/cache regression procedure
are documented in [OverlayRenderBench](../../tools/OverlayRenderBench/README.md).
Text format/layout caching is bounded to the current display; window resizing
retains the drawing target but still allocates an exactly-sized bitmap.

From a Visual Studio developer terminal with CMake and the Windows 10/11 SDK:

```powershell
cmake -S next/TigerClaw.Overlay.Native -B next/_run/OverlayNative/ARM64 -A ARM64
cmake --build next/_run/OverlayNative/ARM64 --config Release
ctest --test-dir next/_run/OverlayNative/ARM64 -C Release --output-on-failure
```

Use `-A x64` and a separate `next/_run/OverlayNative/x64` directory for x64.
The CMake target copies the three existing `sounds/*.wav` files beside the exe.
Initialize the pinned `third_party/llama.cpp` submodule if its vendored
`nlohmann/json.hpp` header is missing. Only that MIT-licensed header is used;
no llama.cpp library or model is linked into Overlay.

Portable display-rule tests also run on Linux:

```sh
cmake -S next/TigerClaw.Overlay.Native -B /tmp/tigerclaw-overlay-native-build
cmake --build /tmp/tigerclaw-overlay-native-build
ctest --test-dir /tmp/tigerclaw-overlay-native-build --output-on-failure
```

## Preview and replacement

Package an existing MSVC Release build from the repository root:

```sh
python3 tools/package_overlay_native.py --arch ARM64
python3 tools/package_overlay_native.py --arch x64
```

The independently usable ZIPs are written to `next/_run/OverlayNativePackages/`.
Each includes the replacement exe, separately named safe preview, sounds,
license notice, replacement/rollback instructions and a SHA256 manifest.
Packaging checks PE architecture, preview/replacement byte identity, source
timestamps, ZIP CRC and payload bytes. It does not build or deploy, and these
integrity checks do not replace the live acceptance checks below.

`TigerClaw.Overlay.Native.Preview.exe --demo` displays an isolated synthetic candidate/status
pair. It does not connect to Core, publish the production heartbeat, consume
the production menu event, change settings or send input. Right-click offers
theme previews and exit; middle-click cycles candidate layout; the wheel adjusts
font size. The status window can be dragged without activating it.
The `TigerClaw.Overlay.Native.Preview.exe` filename also selects demo mode
automatically, so double-clicking the preview cannot attach to production IPC.
Cross-compiled test binaries are staged separately in
`next/_run/OverlayNativePreview/{ARM64,x64}/`, with sounds and runtime licenses.
Newer MSVC-built binaries and Windows test logs are under
`next/_run/OverlayNative/{ARM64,x64}/Release/`; prefer these for current acceptance.

For live acceptance, use a separately prepared test runtime directory. Stop its
WPF Overlay, preserve its executable as a rollback copy outside the launcher
path, and put the native executable at `TigerClaw.Overlay.exe`. Keep `sounds/`
and optional `字体/` beside it. Starting Core then launches the replacement.
Restore the preserved WPF executable to roll back. Never copy either binary
over a running process. No build command in this directory deploys, kills a
process, registers TSF, edits Core settings or touches `release_arm64/`.

The native process refuses to coexist with an already present production
Overlay. It does not terminate another process to take over. CMake also creates
the separately named preview copy. `--demo` refuses the production filename:
Core discovers Overlay by process name, and the WPF legacy instance guard also
terminates same-name processes. An isolated preview must avoid both mechanisms.

## Implementation and acceptance scope

- `Model`: all 28 UI-state fields, UTF-8/UTF-16, literal escapes, Hook code
  visibility, candidate/index/annotation formatting, highlight offsets,
  per-composition reveal deadlines, themes and size metrics.
- `Renderer`: bounded per-window premultiplied DIB, Direct2D DC render target,
  DirectWrite font fallback/color glyphs, optional private folder fonts, and
  selection rectangles, asymmetric cyberpunk corners and bounded soft shadows
  (not pixel-identical to WPF's blur). Software Direct2D is intentional for these small
  transparent windows; there is no continuous GPU swap chain.
- `Transport`: latest-only MMF mailbox, own heartbeat, Core-stale exit, menu
  event, background FIFO menu/config requests with bounded pipe I/O and drained
  cancellation. Updated Core publishes an additive mutex-protected snapshot and
  change event; native reads it immediately, without a second polling interval.
  Legacy Core still uses the two-matching-poll heuristic (not an atomic guarantee).
  See [UI state protocol](../../Protocol/ui_state.md); both Core and Overlay must
  be updated to enable the fast path, while WPF keeps its existing v1 map.
  Layout-setting sequences run as a FIFO batch, stop on first failure, and use
  Core-published state rather than an optimistic successful-looking local state.
- `Sound`: lazy XAudio2, three PCM WAV samples and six playback voices, replacing
  the six WPF MediaPlayer slots without losing key overlap or volume control.
  Failed initialization/playback releases partial resources and retries on a
  later key after a two-second backoff; no idle-unload timer is introduced.
- `main`: no-activate windows, caret anchoring and above/below placement, status
  drag, menus, middle-click layout and wheel font-size changes.
- `Placement`: portable caret validation, pinned anchors, direction-preserving
  anchor refresh, bottom-edge flipping, negative-monitor and oversized-window
  clamping tests. Invalid-to-valid caret transitions force a fresh render.

Current validation (2026-09-08): portable model tests and 12,000 deterministic
random states compared against the actual linked WPF formatter pass, as does
the previously run full Core test suite. Both x64 and ARM64 compile using
LLVM-MinGW and native Visual Studio 2026 / Windows SDK 10.0.26100.0. MSVC uses
the static CRT and the SDK's `xaudio2.lib` import name (MinGW uses `xaudio2_9`).
Windows interop has recovered. MSVC model and isolated transport tests each
passed five consecutive runs for both targets on Windows ARM64 build 28000
(x64 executed under Windows emulation, not on separate x64 hardware).
Runner exit codes are 0 and SHA256/OS details are recorded in each Release
directory's `native-overlay-tests.log`; CTest records repeated runs in
`Testing/Temporary/LastTest.log`. This does not establish visual, live-IME or
memory acceptance.
The attempt to pipe 12,000 parity requests from WSL into Windows `dotnet.exe`
timed out after 120 seconds. Re-running the same comparison entirely through
Windows Python passed all 12,000 cases; use a Windows-side driver for this
mixed-runtime test, rather than attributing the WSL-driver timeout to formatting.

ARM64 preview was actually displayed and captured with `Inspect-Preview.ps1`.
The horizontal -> vertical -> code-only -> horizontal cycle preserves foreground
focus and restores the original 1031x73-pixel layout on the tested monitor.
This exposed and fixed a demo-only code-visibility preference initialization
bug. Screenshots, process/binary identity and measurements are under
`next/_run/OverlayNative/ARM64/inspection-fixed/`. Sample working set was
27,099,136 bytes and private commit 9,666,560 bytes. This is synthetic preview
only (no Core IPC or audio initialization), not a like-for-like WPF benchmark.
The inspector accepts only the separately named preview process, sends messages
only to that process's own candidate window and captures only its window bounds.

The opt-in `overlay_window_tests.exe` also passed on both ARM64 and x64. Unlike
the synthetic demo, it drives the real application through unique MMFs and its
real StateSource worker. It checks initial invalid/late caret rendering, content
updates, pinned and revised anchors, composition hide/show, bottom-edge flipping,
lost/restored caret, delayed candidate and annotation reveals, unchanged focus,
overlay heartbeat and clean process exit after the test Core heartbeat goes stale.
The child is a separate `TigerClaw.Overlay.Native.Test.exe`; that filename requires
a bounded `--test-session` ID and cannot attach to production IPC. Test sessions
use unique window titles and mutexes as well as unique pipes/maps/events. The
test-copy build target refreshes the child even when only application code changes.
These GUI tests are deliberately not in unattended CTest or Run-IsolatedTests;
run them explicitly on an interactive Windows desktop. They briefly show their
own windows and close only their own child process afterwards.

`overlay_sound_tests.exe` passed on both targets with an available audio endpoint:
five silent load/release cycles exercise the actual three WAV files, mastering
voice and six source voices; missing-file handling is also checked. This tests
initialization and cleanup, not audible playback quality. Run it explicitly; it
is not part of unattended CTest because headless machines may have no endpoint.
The ARM64 preview menu opened/cancelled twice and could be reopened without
changing foreground focus (`inspection-menu/preview-inspection.json`). Physical
outside-click dismissal and menu commands against a live Core remain separate.

Before declaring the replacement complete:

- Keep running the isolated Windows transport tests after IPC changes. They
  cover state delivery/coalescing, malformed snapshots,
  sequence reset, menu/heartbeat signals, split pipe responses, response sequence
  validation, failed setting batches, stalled-read disposal and absent Core.
  Every endpoint uses a unique test-only name and only a message-only HWND is
  created. Run `powershell -NoProfile -File .\Run-IsolatedTests.ps1` in the staged
  architecture directory; it writes `native-overlay-tests.log` there.
  The runner records executable SHA256 and OS identity, retains native stderr,
  and limits each test process to 45 seconds. The earlier `Exec format error`
  environment blocker is resolved; visual/live acceptance remains outstanding.
- Review native menu dismissal on Windows.
  Menu opening now waits asynchronously for a fresh schema query (falls back
  to cached entries on failure); command IDs use an immutable open-menu snapshot.
- Finish Windows rendering acceptance for private fonts and multi-monitor DPI;
  test actual menu actions and typing sounds, plus live Core/TSF/Native Hook
  replacement and rollback. The isolated window/heartbeat tests above cover
  the shared IPC path but do not substitute for live frontend acceptance.
- Compare working set, private commit, handles, CPU and candidate latency under
  the same font/theme/sound/input workload as WPF. Executable size is not a
  runtime-memory measurement.

Do not enable native Overlay in the main publish scripts until these checks
pass. Keep protocol/display changes aligned with WPF tests.

The isolated [2026-09-08 latency experiment](../../tools/OverlayLatencyBench/RESULTS-20260908.md)
reproduced a pre-render regression: matched-input read P50 24.4 ms native versus
8.5 ms WPF. An unsafe diagnostic copy without the second matching-snapshot poll
measured 9.8 ms. This is not a deployed fix or a photon-latency measurement;
prioritize reliable publication/notification without the extra polling wait.
