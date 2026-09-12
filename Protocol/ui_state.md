# Overlay UI state publication

Core's `UiStatePublisher` is the sole production writer. All payloads are UTF-8
JSON `OverlayUiState`. Integers below are little-endian; map capacity is 128 KiB.

## Legacy v1

Optional `CandidateBackgroundUntil` is a superseded background-dwell experiment
and is ignored by current Native Overlay and WPF.

Optional `CandidateAnimationEnabled` (default true) and
`CandidateAnimationDurationMs` (default 200) control Native geometry animation.
Movement, growth and shrinkage of a visible candidate window share this duration.
Show and hide are always immediate. Duration is bounded to 0..60000 ms; zero or
disabled means immediate geometry updates. Missing fields use defaults. Former
`CandidateAnimationShowMs` and `CandidateAnimationHideMs` fields are ignored.
WPF ignores these additive fields.

Name: `Local\TigerClaw.UiState.v1`.

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | int64 | positive publication sequence |
| 8 | int64 | GetTickCount64 publication timestamp |
| 16 | int32 | payload byte length, 1..131052 |
| 20 | bytes | JSON payload |

Existing deployed WPF readers use this map. Its sequence-before-payload write
order is preserved for compatibility; v1 alone has no atomic-snapshot guarantee.
The native legacy fallback requires two matching polls as a defensive heuristic,
not a synchronization guarantee. Oversized JSON is dropped, never truncated.

## Additive synchronized snapshot v2

For a legacy map name `N`:

- snapshot map: `N.Snapshot.v2` (same capacity/header/payload layout);
- named mutex: `N.Snapshot.v2.Lock`;
- auto-reset change event: `N.Snapshot.v2.Changed`.

Production `N` is the v1 name above. Tests inject a unique `N`, so all derived
objects remain isolated. Both sides may create the mutex/event; only Core creates
and writes the snapshot map. This is a single-consumer notification, with timed
polling retained for reconnect, missing/coalesced notifications and legacy Core.

After publishing v1, Core tries to acquire the mutex without waiting. If acquired,
it sets snapshot sequence to zero (incomplete), writes timestamp, length and
payload, performs a memory barrier, and writes the final positive sequence last.
It releases the mutex before setting the event. The snapshot uses exactly the
same sequence and timestamp as that v1 publication. No snapshot FlushViewOfFile
is necessary: mutual exclusion provides memory synchronization, not disk I/O.

A paused reader must not stall Core: if the mutex is busy, Core skips this
optional snapshot update, keeps the v1 update and still signals the event.
Optional-channel creation/write failure leaves v1 available. An abandoned mutex
can be acquired; sequence zero invalidates a writer that died mid-publication.

Native Overlay waits on stop/change events with a 5 ms fallback timeout. When v1
has a new `(sequence,timestamp)`, it tries the snapshot mutex without waiting,
copies only a complete, bounded snapshot matching that v1 pair, releases the
mutex, then parses JSON. A matching snapshot needs no second poll. Busy mutexes
defer until another notification/poll. Missing, invalid or mismatched snapshots
use the legacy path: this also prevents a retained v2 map from serving stale
state after Core is downgraded to an old v1-only build.

The existing latest-only UI mailbox and coalesced PostMessage behavior are
unchanged. This protocol does not change TSF/Hook pipe messages, candidate
ordering, WPF formatting, heartbeat or menu contracts.

Tests: `TigerClaw.Core.Tests --ui-snapshot-tests` exercises the actual C# writer;
native `overlay_transport_tests` covers first-read acceptance, paused/abandoned
writers and downgrade matching; opt-in `overlay_window_tests` drives the real
native application with the new snapshot and event.

## Pending sentence candidate frames

Optional `CandidateHoldWhilePending` (default false) accompanies
`CandidateVisible=false` when a Chinese sentence composition has an empty
projected candidate list and its decoder has not finished. Core captures the
list and decode-pending flag under one engine lock. The existing fresh-caret
barrier, focus loss, deactivation and cancellation must not emit a hold hint.

Native Overlay may retain an already visible, successfully published candidate
frame while this hint is true. It never shows a new placeholder, revives a hidden
window, or restores committed entries into the engine's selectable candidates.
The current caret must be usable and remain on the published frame's monitor,
effective DPI and work area. Code-only placeholders do not qualify. Explicit
candidate hiding and disabled/English modes take precedence.

Retention does not call Present, move/resize the window, advance placement history,
reset reveal clocks, or accept a pending animation target. Completion drives the
next layout; there is no extra wait duration, polling loop, or timer. Status UI
continues updating. A completed empty result takes the ordinary display path,
not this retention path. Pending snapshots are not reusable candidate data.

This additive field is only on Core-to-Overlay UI snapshots: no TSF/Hook pipe
change. Older Core omits it; older Native/WPF ignores it and keeps its previous
immediate-hide behavior. The fix requires rebuilding both Core and Native
Overlay, with Shared source included. The existing latest-only mailbox and its
coalescing semantics are unchanged; it is not a cross-context frame cache.

Regression: `tests/TigerClaw.CandidateFrame.Tests` generates isolated MMF snapshots
from real asynchronous sentence auto-commit (vu -> \u8fd9, then pending j).
`overlay_pending_frame_tests` consumes that wire trace with real, test-owned
nonactivating layered windows, controlled time and publication faults. The
without-hint control must reproduce the original hide. No production IPC,
registration, input injection or daily-runtime replacement is performed.
