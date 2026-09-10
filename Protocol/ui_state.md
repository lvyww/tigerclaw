# Overlay UI state publication

Core's `UiStatePublisher` is the sole production writer. All payloads are UTF-8
JSON `OverlayUiState`. Integers below are little-endian; map capacity is 128 KiB.

## Legacy v1

Optional `CandidateBackgroundUntil` is a Windows uptime-millisecond deadline
(`Environment.TickCount64` / `GetTickCount64`), default zero. Core sets it to
now + 2000 when any active composition commits and becomes Chinese idle, including
ordinary, sentence and temporary-pinyin modes. Partial commits with live remaining
composition continue showing candidates instead of entering background hold.
New key-downs and non-caret/state-query actions clear it; key-up never extends it.
This is a superseded experiment: current Native Overlay ignores this deadline
and independently animates every candidate hide over 200 ms and show over 20 ms,
regardless of input mode. There is no background dwell. WPF also ignores the
field. It remains optional for compatibility with previously built Core binaries;
current Native animations do not require a Core update.

Optional `CandidateAnimationEnabled` (default true), `CandidateAnimationShowMs`
(default 20), and `CandidateAnimationHideMs` (default 200) control Native animation.
Show/growth/movement share the first duration; hide/shrink share the second.
Durations are bounded to 0..60000 ms; zero or disabled means immediate.
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
