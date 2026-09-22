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

Native Overlay also uses the existing `CandidateBackgroundUntil` timestamp as
an ordinary-word commit capability. Core publishes commit tick + configured residence duration and
revokes it on subsequent keys or focus/cancel/config messages. The experimental
empty-frame residence is controlled by optional `CandidateResidenceDurationMs`
(0..60000, default 0/off), requires
a previously visible ordinary composition followed by Chinese idle, and never
extends the deadline on caret updates. It preserves geometry with no candidate
content and permits a subsequent ordinary frame to use the existing transition.
Fresh-caret gating (`CompositionState=2`, nonempty input, `CandidateVisible=false`)
and candidate reveal delay may intervene before that transition. The existing
blank frame survives those waits within the original deadline; it does not show
the pending candidate text. A second input-session change invalidates the wait.
A new ordinary commit may interrupt the transition or that wait: its fresh
capability starts another configured-duration residence at the currently published rectangle,
without requiring a completed animation or a newly drawn candidate frame.
This is independent of pending sentence retention below; the duration field requires an updated Core. Older Core without that field
keeps immediate hiding. The setting is `上屏后候选窗驻留时间(毫秒)`.

Optional `CandidateHoldWhilePending` (default false) accompanies
`CandidateVisible=false` when a Chinese sentence composition has an empty
projected candidate list and its decoder has not finished. Core captures the
list and decode-pending flag under one engine lock. The existing fresh-caret
barrier, focus loss, deactivation and cancellation must not emit a hold hint.

Native Overlay may retain an already visible, successfully published candidate
frame while this hint is true. It never shows a new placeholder, revives a hidden
window, or restores committed entries into the engine's selectable candidates.
The current caret must be usable and remain on the published frame's monitor,
effective DPI and work area. An already published nonempty input/code-only frame
also qualifies; a pending state never creates a new placeholder. Explicit
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

### Display-continuity identity and current-frame eligibility

`CandidateFrameSession` is an optional opaque string identifying candidate-frame
continuity. Core captures it under the same engine lock as candidates and pending
state. Clearing/restarting composition or losing focus/activation changes it;
ordinary decoding and automatic prefix commit within continuing sentence input
do not. It is freshly randomized across engine instances, not a process-local
counter reused on restart. Every snapshot carries it, so Native does not have to
see an intermediate hidden snapshot. Missing/empty identity disables retention
conservatively (including when talking to older builds of this PR).

Native requires an exact identity match to the successfully published frame.
A changed identity resets only the current display/reveal session and horizontal
anchor; it does not clear or partition the geometric 100-decision history. This
identity is not a TSF context ID and is not used for placement direction.

Frame eligibility describes the currently published pixels, not whether a
candidate appeared at some earlier point. Nonempty CodeOnly/InputOnly frames
remain eligible, preventing hide/reappear when another key follows an empty
decode result. Hidden/empty frames are ineligible, including animation samples
and reentrant publication. A failed
publication leaves the eligibility of untouched pixels intact; a reentrant reset
cannot be rolled back. Preparing a future layout alone changes no eligibility.

The review regressions generate real identical-code commit/cancel/Escape/Backspace
end/new-input sequences and deliberately omit hidden snapshots from Native
consumption. They additionally exercise both code-only modes after candidates,
intermediate/final animations, publication failure, missing identity and reentry.
Two compiled negative controls remove session checks and exclude input-only
frames, and must fail for the corresponding reported defects. The real Core
trace also includes an empty decode followed by another pending key.


Full-pinyin internal v1 uses `CompositionState = 6`. Optional `CandidateSelectionToken` identifies the current selectable candidate page and is empty while decoding is pending. It changes on decode/application/menu navigation and is invalidated on clear. Native Overlay hit-tests the actual DirectWrite candidate text ranges, preserving foreground focus, and routes clicks through the updated TSF FIFO/receipt path documented in messages.md. Older overlays ignore this field. The existing pending-frame contract also applies to state 6.
