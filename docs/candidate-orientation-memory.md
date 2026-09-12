# Cross-composition candidate orientation

This change adds the direction-memory policy used by Tigirl to the maintained
TigerClaw C# Core / WPF Overlay runtime. It does not change candidate ranking,
input commits, lexicons, the sentence decoder, configuration defaults or installs.
The reference implementation and release directories are untouched.

## Positioning policy

The initial placement prefers below the caret. If that does not fit, it flips
above. After an accepted above placement, the same input environment retains the
above preference across normal commits/cancels and new words while the caret Y
stays within a small band or moves down. An upward move beyond the band releases
that preference and evaluates current available space; it does not force below.
A fitting below position wins over an above position that cannot fit.

The band is rounded 3 DIP in monitor pixels, capped at one quarter of the smaller
current/reference caret height. Carets only 1–3 pixels tall get a zero band.
Samples inside the band do not replace the stable reference, so cumulative slow
upward movement is not mistaken indefinitely for jitter. Actual Y coordinates
follow the live caret; only the side decision has hysteresis. This does not lock
the candidate's top edge. The composition X anchor and explicit automatic-commit
anchor refresh remain; a real line or host change refreshes X too.

TigerClaw's existing 5-physical-pixel caret gap, 2-pixel right and bottom reserves,
Start-menu corner exception and oversized-work-area clamp are retained. These
reserves are intentionally not replaced with Tigirl's zero bottom reserve. The
old 50-pixel movement suppression is replaced by the direction-specific band.

## Lifetimes and cross-process boundary

`CandidateWindowPositioner.EndComposition()` clears only the transient caret
anchor and pending publication. It does not erase accepted direction memory.
`Reset()` additionally erases the direction and environment.

Core publishes a unique process instance ID plus a monotonically increasing
`CandidateEnvironmentRevision`, active-input flag and focused HWND/process ID.
Normal commits/cancels and repeated identical focus/mode updates do not advance
that revision. Focus-window/process changes, genuine TSF document/context hints,
reconnects, frontend changes, activation and Chinese/English transitions do.
Consequently even an inactive/active sequence faster than the MMF reader can be
recognized by its final revision. Core restarts cannot reuse a prior instance ID.

The native TSF bridge adds optional `candidate_environment_changed:true` to a
known, no-response `focus` message for genuine document/context changes. This
hint must match the stored focus HWND/process. It does not overwrite ordinary
focus metadata or run the engine's focus-reset path. Existing caret tracking,
key replay and commit handling are unchanged. Popped tracked contexts are also
recognized when their document lookup is no longer available.

Overlay observes environment changes even while candidates are hidden or idle.
It additionally compares foreground/focused child HWND, process, physical window
bounds, monitor, DPI and working area. Unknown host geometry is not inherited.
A newer Core snapshot pointing to a different foreground application is rejected.
Existing legacy Core snapshots (no epoch fields) remain display-compatible, but
cannot provide the new cross-process focus-generation guarantees. Full behavior
requires rebuilding Core, Shared, Overlay and the TSF bridge together.

`CandidateAnchorRevision` remains distinct: automatic/partial commits can refresh
the caret anchor without invalidating orientation. The new optional UI members
are appended after the existing serialization contract; no existing field order
or pipe framing is changed.

## Publication and visibility

Calculation occurs on a copy of the orientation. SetWindowPos must succeed (or
current geometry already match), the state/lifecycle must still be current, and
`ShowCandidate` must complete before `ConfirmShown` accepts the copy. A hidden,
failed or superseded frame cannot teach the next word a new direction. Measuring
a newly revealed WPF window keeps it transparent and non-interactive until its
position is ready; no first-frame flash at the prior position is intentional.
Missing/invalid carets do not become Y=0 and do not mutate orientation memory.

## Reproducible checks

Requires .NET 10 SDK; WPF tests and the actual Core protocol checks require Windows.
No test registers an IME, installs files, injects global input or uses a live MMF.

```text
python tools/test_candidate_orientation.py
python tools/test_candidate_orientation.py --windows-ui --platform x64
python tools/test_candidate_orientation.py --windows-ui --platform x86
python tools/test_candidate_orientation.py --protocol
```

The portable executable compiles the production C# orientation, state observer,
serialized UI contract and environment tracker. It checks stable/cumulative Y,
100 repeated word sequences per environment, 8 working areas, 4 DPIs, randomized
geometry, numeric extremes and independent environment changes. A compiled
mutation removing remembered-above preference must fail for the expected reason.

The Windows executable runs the production positioner on real WPF windows. Its
sizes and most environment inputs are controlled, and move/capture failures can
be injected. It checks hidden-to-shown placement before accepting the first
frame, repeated EndComposition, jitter/up/down, uncertain/moved hosts, pending
state, reentry, corner placement and legacy capture. An EndComposition-reset
mutation must fail. It does not pretend to render real candidate lists or drive
full TSF applications. A separate test drives the real Core ProtocolHandler and
checks commit/cancel, same-HWND hints, focus metadata preservation, fast mode
transitions and reconnects.

CI also compiles the actual Overlay, Dialog and x64/Win32 TSF bridge, and runs the
existing Core regression executable. Build artifacts are isolated; the Python
runner retries scratch cleanup and reports persistent file locks without masking
actual test failures. Check CI for execution results, not this test inventory.
QQ/WeChat, physical multi-monitor movement and installed-app focus behavior remain
manual acceptance checks. No installation or merge is performed by this change.

## Mainline Native Overlay

The feature branch was synchronized with main `538096b` after the native Overlay
became available during this change. No mainline settings, instant visibility or
geometry-animation configuration is rolled back. WPF remains a supported fallback.

`next/TigerClaw.Overlay.Native/Placement.h` owns the same direction policy.
`EndComposition` releases its X anchor but retains the direction/reference;
`Reset` discards the entire environment. `Model` reads the five optional Core
fields and rejects display in an explicitly inactive environment. Legacy Core
payloads retain their existing visibility contract and best-effort foreground
geometry checks. Monitor/work area, DPI, owner/foreground/focus HWND and window
rectangles fence geometry reuse. Ordinary hiding does not discard direction.
Unknown owner geometry disables inheritance; a conflicting known foreground PID
hides rather than displaying stale candidates over another process.

Application layout resolves on a copy. Failed Prepare/Present/show does not
accept direction. Visible animations publish their first sample before accepting
the target, rather than treating a scheduled timer as a successful frame.
Reentrant layout is deferred and invalidates the older frame's acceptance.
Environment changes do not animate from old screen/window coordinates. Existing
show/hide remains immediate and the 2-pixel bottom/right reserves are retained.

Native validation:
- `python tools/test_native_orientation.py --cxx g++` and
  `--cxx clang++ --sanitize`: production C++ geometry plus separate no-memory and
  per-composition-reset mutations. Each positive run includes 122,124 positions,
  302,495 checks, 100,000 fixed-seed random rectangles and word-size sequences.
- CMake `overlay_orientation_publication`: real production Application/Renderer
  with test-owned layered HWNDs, controlled clock and injected publication/show
  failures. No running Core or global input is used.
- Existing `overlay_window_tests`: the actual isolated native executable and MMF
  transport, extended with cross-word, jitter, cumulative upward and epoch-change
  regressions. Existing visibility/animation/menu checks remain in place.
- Existing CTest model and transport, plus production native/TSF bridge builds,
  WPF builds/tests and actual Core protocol regression are also run in CI.

Core DataMember orders 29/30/33 (existing animation compatibility) are retained;
the new environment fields use 34 through 38. Full Windows UI/TSF injection in
QQ/WeChat and physical mixed-DPI dragging still require installation acceptance.
