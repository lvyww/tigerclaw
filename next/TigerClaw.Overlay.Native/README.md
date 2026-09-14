# Native Overlay (mainline)

C++17 / Win32 / Direct2D / DirectWrite replacement for the WPF Overlay only.
Core, TSF, Native Hook and Dialog remain unchanged. The resulting executable is
named `TigerClaw.Overlay.exe`, so the existing Core launcher can use it without
a protocol or settings migration. Native is the default published frontend as
of the user's mainline decision on 2026-09-09; WPF remains an explicit fallback
and display-parity reference. This promotion does not claim completion of every
multi-monitor/private-font acceptance scenario listed below.

`publish.bat`, `publish_arm64.bat` and `next/build_next.bat` all use
`next/build_overlay.bat ARCH OUTPUT_DIR [CONFIGURATION] [JOBS]`. It builds the
native executable by default and copies sounds plus the third-party notice.
The helper never stops running processes or deploys to a release directory by
itself; provide an isolated build output path. The release scripts retain their
usual explicit deployment behavior.

For a WPF fallback build in a Windows command prompt:

```batch
set TIGERCLAW_OVERLAY_BACKEND=wpf
call next\build_overlay.bat ARM64 next\_run\OverlayFallback\ARM64 Release
set TIGERCLAW_OVERLAY_BACKEND=
```

The same environment variable selects WPF when invoking the full publish or
debug scripts. An unset value (or `native`) selects C++; invalid values fail
the build rather than silently selecting another backend. WPF source and the
legacy v1 UI protocol remain maintained for rollback; Dialog still uses WPF.

Build-entry regressions (including architecture, payload, paths with spaces,
WPF fallback, stale config removal and invalid backend rejection):

```powershell
powershell -NoProfile -File tools/test_overlay_build.ps1 -Build
```

This writes only isolated build outputs under `next/_run/OverlayMainlineCheck/`
and `next/_native_build/`; it does not deploy or launch the Overlay.

## Build

Pending sentence decoding retains an already published nonempty frame from the
same `CandidateFrameSession`, including input/code-only frames after an empty
decode. It does not show a placeholder, move the window or replay old candidates.
Clear, session/focus changes and explicit hiding still invalidate the frame.
Regression: `tests/test_pending_frame.py` with the real Core-generated trace.

Candidate animations apply to every input mode and only interpolate geometry
while the candidate window is already visible. Movement, growth and shrinkage
share one duration (default 200 ms). Appearing publishes the full candidate
frame immediately. Ordinary word commits experimentally leave an empty frame
for the configured `上屏后候选窗驻留时间(毫秒)` (0..60000, default 0/off);
other disappearing states hide immediately.
Interrupted geometry changes continue from the actual displayed rectangle.

The ordinary-word residence reuses Core's existing `CandidateBackgroundUntil`
commit capability (commit time + configured duration), together with the optional
`CandidateResidenceDurationMs` field. Missing/zero duration disables residence.
It requires a previously published ordinary composition (state 2), followed by
Chinese idle (state 1) with a fresh commit hint. The blank frame keeps the actual
published rectangle, background and border, with no text or selection. Caret
updates do not move it or extend its deadline. Another ordinary composition in
the interval acquires a fresh caret anchor and animates from this visible blank
rectangle using the existing animation settings.
New ordinary input may first publish `CandidateVisible=false` while waiting for
a fresh caret. This gap and the configured candidate reveal delay preserve the
blank frame, without prematurely displaying the new text or extending the
original expiry. The first new input session is pinned; another session change
still invalidates residence.
An ordinary commit during an unfinished transition freezes its current published
rectangle and starts a fresh configured-duration blank residence, even before the animation's
first tick. A resumed ordinary input committing during its caret/reveal wait also
renews the blank residence; ordinary caret/idle updates cannot renew it.
Sentence/pinyin/uppercase input,
cancel, focus/session changes, English/off, explicit hiding and invalid geometry
do not start or preserve this residence. A 25 ms timer checks expiry/foreground;
blank publication failure hides immediately rather than retaining old text.
No placeholder appears without an existing candidate window. Fully transparent
themes remain transparent; actual movement still requires enabled animation.

Windows regression: `overlay_pending_frame_tests.exe --blank-residence` uses
test-owned nonactivating windows, the production renderer and a controlled clock.
It covers default-off, custom 1..60000 ms durations, disabling while resident, 1500 ms expiry, unchanged geometry, resumed transitions, cancellation,
stale timers, failed presentation and reentry. This does not replace typing
acceptance in the user's applications.

The Candidate settings category exposes one compact row: animation checkbox and
duration in milliseconds (`候选窗动效时间(毫秒)`, default 200).
Values accept 0..60000; zero or disabling animation gives immediate updates.
Empty/invalid configuration falls back to 200. The old separate show/hide
settings are ignored and hidden from the settings UI.
Old Core uses the new default automatically; custom duration requires updated
Core and Dialog. `TIGERCLAW_OVERLAY_TRANSITION=0` remains a diagnostic override.
Intermediate frames retain normal-size text and selection. Monitor-based cadence
is unchanged (requested 2..4 ms; WM_TIMER minimum is 10 ms). Late callbacks skip
frames rather than queue playback; no global timer-resolution changes are used.

Candidate redraws prepare the complete offscreen frame before publishing pixels,
size and final caret-relative position in one `UpdateLayeredWindow` call. A failed
prepare/present retains the last published window frame; up to three 100 ms
retries are allowed, with fresh state permitting another attempt. Explicit hidden
states and invalid carets still hide immediately. With the experiment disabled,
this does not delay shrinking or animate; neither path guarantees that real
candidate-count changes are imperceptible.
`overlay_render_probe --present-check` verifies preparation leaves window geometry
unchanged, incomplete frames are rejected, and successful publication combines
size and position without showing an intentionally hidden window.

Context menus try to temporarily activate their owner, as described by
[TrackPopupMenuEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenuex)
for outside-click dismissal. If focus has moved to another application when the
menu closes, it is left there; otherwise the previous foreground is restored.
Normal candidate/status updates remain non-activating. If Windows denies menu
activation, the menu still opens with an independent outside-click/Escape monitor
instead of relying on foreground-owner dismissal. Every menu uses a dedicated
transparent host, independent of candidate/status visibility changes from Core.

`overlay_menu_tests.exe` is an explicit opt-in interactive test: it launches only
the synthetic preview, moves the pointer and clicks its own two test windows,
checks outside-click dismissal and cancel/reopen, then restores the pointer.
It is not in CTest. On 2026-09-10 the ARM64 and x64 interactive tests passed, including
forced foreground denial, outside-click dismissal, cancellation and reopening.
The earlier hit-test failure came from using STATIC text controls as mouse
targets; the fixture now uses ordinary registered top-level windows. The passive
real-popup and isolated IPC/offline-menu tests passed too. The user subsequently
confirmed the taskbar right-click fix passed live testing on 2026-09-10.

Renderer reuse measurements and the isolated pixel/cache regression procedure
are documented in [OverlayRenderBench](../../tools/OverlayRenderBench/README.md).
Text format/layout caching is bounded to the current display; window resizing
retains the drawing target but still allocates an exactly-sized bitmap.
Status startup positioning uses the rendered physical-pixel size and the
monitor work area, with a two-pixel inset. DPI, display and work-area changes
clamp the status window back inside that area without resetting a valid dragged
position. This avoids mixing the 26-by-50 DIP status size with pixel coordinates.

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
- Keep native menu behavior in live taskbar TSF regression checks.
  Root menu opening no longer waits for a schema query; command IDs are frozen
  to the list shown when the schema submenu opens. Isolated foreground-denial
  and offline-menu tests pass; the user also confirmed this taskbar fix.
- Finish Windows rendering acceptance for private fonts and multi-monitor DPI;
  test actual menu actions and typing sounds, plus live Core/TSF/Native Hook
  replacement and rollback. The isolated window/heartbeat tests above cover
  the shared IPC path but do not substitute for live frontend acceptance.
- Compare working set, private commit, handles, CPU and candidate latency under
  the same font/theme/sound/input workload as WPF. Executable size is not a
  runtime-memory measurement.

Native is now enabled in the main publish scripts by explicit user decision.
Taskbar right-click uses targeted foreground permission transfer from TSF through
Core to the sibling Overlay before the menu event. All menus use a separate
transparent menu owner, hidden again after dismissal.
The root menu opens immediately; schema refresh runs in the background. Expanding
the schema submenu snapshots the latest cached list and its command IDs; later
replies never change an already displayed submenu or remap the user's choice.
Foreground activation is best-effort,
not a prerequisite for showing the menu: taskbar TSF callbacks can lack permission
even after delegation. A 20 ms menu-lifetime timer detects new outside clicks
(including right clicks) and Escape, matching WPF's independent dismissal policy.
Submenu rectangles count as inside; opening button holds are ignored. Timer work
stops when the menu closes. Transient taskbar foreground changes during schema
refresh no longer silently discard the request.
`overlay_menu_tests` includes denied-foreground, hidden-window cancellation and outside-click cases;
it requires an interactive desktop and is not an unattended CTest target.
Its `--passive` option tests real popup open/hide/cancel/reopen without injecting
input or moving the pointer. `overlay_window_tests` additionally exercises the
isolated menu event with no Core pipe (below 800 ms, without the 3 s pipe timeout)
and candidate/status hiding while that menu remains open.
Keep the outstanding checks above as regression/acceptance work, and keep
protocol/display changes aligned with WPF tests.

The isolated [2026-09-08 latency experiment](../../tools/OverlayLatencyBench/RESULTS-20260908.md)
reproduced a pre-render regression: matched-input read P50 24.4 ms native versus
8.5 ms WPF. An unsafe diagnostic copy without the second matching-snapshot poll
measured 9.8 ms. These are historical diagnostic results, not photon-latency
measurements. The production fix is the mutex-protected snapshot/change-event
path described above; the unsafe diagnostic reader is not used.
