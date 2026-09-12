# Native Overlay: bounded geometric above-placement memory

This is the Native-only implementation of the 2026-09-12 geometry/100-decision
proposal. No TSF context identity, focus epoch, Core/Shared/transport field or WPF
change is required. It intentionally permits different input boxes/applications
to reuse above placement at the same screen height. It is an alternative to the
older cross-component direction-memory proposal, not a patch to stack on it.

## Policy

Keep at most 100 successfully displayed, distinct candidate-layout decisions in
a fixed-size ring. Each record contains physical caret Y/height, the selected
side and its reason. Only a fully fitting above placement with insufficient
space below is fresh overflow evidence. Inherited above placements do not renew
that evidence. Eviction precedes the next decision: evidence at decision 1 can
support decisions 2 through 100, but not 101. Another real overflow at 80 supports
through 179. Expiration re-evaluates space; it never forces a non-fitting below
placement. Repeated actual bottom overflow may renew evidence indefinitely.

The remembered direction survives ordinary hidden/new-input cycles and explicit
anchor refreshes. New input takes a fresh horizontal anchor. Within a visible
input the original horizontal anchoring remains, but Y and caret height use the
latest valid caret so real upward motion is observable. Below placement is no
longer artificially locked by an anchor refresh.

A stable reference Y uses rounded 3 DIP, capped to one quarter of the smaller
current/reference caret height. Samples within the band do not move the reference.
Clear old evidence on a clear upward move; advance the reference, retaining bias,
on a clear downward move. Above must fit; otherwise prefer a fitting below side.
If neither fits, choose more available space and clamp, without creating reusable
evidence. Right/bottom insets remain 2 physical pixels; caret gap remains 5.
Position arithmetic uses 64-bit intermediates. Existing invalid-coordinate bounds
and fallback caret-height handling are retained.

Only monitor identity, effective DPI and work-area bounds partition the history.
Monitor selection uses current caret coordinates, not the old horizontal anchor.
Changing any partition discards old evidence. Returning to a prior monitor does
not restore it. Font size/vertical mode changes are measured as new geometry,
not used as TSF/focus identifiers. Search/Start-menu special placement bypasses
the normal history and cannot seed it. No timer-based age or disk persistence.

## What counts as one decision

The key combines the current visible text/selection revision, measured popup
size, effective caret coordinates/height, anchor and display environment. New
input explicitly invalidates that key even when identical to the previous input.
Duplicate state snapshots, forced repaint, sound/status changes and retries of
an already accepted layout do not age or renew history. Invisible/invalid layouts
and preparation/publication/show failures do not append records.

This is an observed-layout window, not a count of physical keys or words. The
existing latest-only mailbox can coalesce intermediate states, including an
entire hidden/new-input boundary. With no protocol change, indistinguishable
coalesced snapshots cannot be counted as separate inputs. No stronger guarantee
is claimed. Different observed visible layouts still age the history normally.

Application calculates on a copy. An immediate full frame commits after Present
and successful nonactivating Show. Animated targets are held separately and
commit only on their successful final full frame: intermediate samples, replaced
or hidden targets and failed animation publication do not count. Position-only
updates use the same publication path. Reentrant refresh increments a revision,
defers renderer reuse and cannot overwrite memory with a superseded frame.

This locks neither the popup top nor its screen coordinates. Its top may move
when height changes even though it stays above the caret.

## Validation

- `python next/TigerClaw.Overlay.Native/tests/test_placement_history.py --cxx g++`
  or `--cxx clang++ --sanitize`: production portable header, exact 100/101 and
  179/180 boundaries, no self-renewal, jitter/cumulative drift, fresh anchors,
  environment resets, invalid/special/oversized geometry and 100,000 seeded random
  cases. Four compiled mutations must fail for specific assertions, not merely
  compile-fail/crash. Use `--build-dir` for retained isolated output; default scratch
  cleanup has bounded retries and does not mask test-body failures.
- Normal CMake/CTest also builds/runs `overlay_placement_history_tests` alongside
  the existing model and (on Windows) transport tests.
- `overlay_placement_publication_tests.exe` is explicitly invoked, not in CTest.
  It uses production Application/Renderer and test-owned nonactivating layered
  windows with controlled time and publication/show failure injection. No Core,
  TSF registration, production IPC, foreground activation, input injection or
  audio playback. It checks lifetime, first frames, 100-decision aging, animation,
  duplicate repaint, reentrancy, invalid caret and delayed reveal.
- CI builds Windows x64/x86/ARM64; x64/x86 execute the above suites. ARM64 is
  build-only on the x64 runner. QQ/WeChat use, physical multi-monitor/DPI dragging
  and ARM64 execution remain separate live acceptance checks. No build deploys
  to the user's daily runtime.
