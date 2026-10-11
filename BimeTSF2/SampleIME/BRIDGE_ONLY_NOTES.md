# BimeTSF2 Bridge-Only Notes

## Scope

BimeTSF2 now runs in bridge-only mode:

- Capture TSF key/focus/caret events.
- Forward to TigerClaw Core through `\\.\pipe\BimeIPC`.
- Apply Core response (`handled`, `commit_text`, `keyboard_open`) back to TSF/UI.

TSF side is **not** the source of IME state, composition state, or candidate logic.

Connections do not verify Core executable hashes or query the server process
path. Hello handshakes and physical-key replay identities retain
their existing behavior. The menu action still queries the pipe server PID for
best-effort foreground permission; failure does not reject the connection.

Core launch is allowed only for non-service users in nonzero sessions on
`WinSta0\Default`. Both the TSF launch scheduler and worker check this before
inheriting the host's token/desktop. Core repeats the check at entry, before
registration checks, singleton acquisition or IPC. On 2026-09-12 a boot-time
SYSTEM Core and its SYSTEM Overlay had live heartbeats but no windows on the
user desktop; Core's command line matched the TSF launch path. The original
host had exited, so its identity was not confirmed. The independent Core check
also protects installations whose older TSF DLLs still lack the launch guard.
`tools/test_tsf_startup.bat` covers service SIDs and a real isolated alternate
desktop; Core's `--startup-context-tests` covers session/account/desktop policy
and the current process's native context query.
Recovery on 2026-09-12 deployed only the verified ARM64 Core entry guard and
restarted the runtime as the interactive user. Core, Sentence and Overlay owners
then matched the user; Overlay's UI thread was on `Default` and its status
window was visible. The existing configuration was hash-identical during
replacement. The old Core and recovery report are under
`next/_run/overlay-startup-diagnosis/`. TSF Win32/x64/ARM64 builds and Core's
full suite passed; the TSF launch guard is built but has not been installed.
Cold-boot acceptance of this fix remains pending.

Protected-input bypass (2026-09-12): four key entrypoints check before modifier
tracking, cached replies and key diagnostics. Secure TSF activation, service or
non-default desktop contexts, TSF keyboard-disabled contexts, and standard
EDIT/RichEdit password styles bypass key forwarding. Replay, enqueue and commit
application recheck; bypass clears local queued keys, cached replies, pending
event identities, key-up forwarding and refresh/replay timers. No password text
or window title is queried. The ordinary uncertain-response retry policy stays
intact. This does not identify custom password fields lacking OS metadata.
`tools/test_tsf_protected_input.ps1` compiles the actual entry prefixes and
cleanup method with stubbed context state to verify bypass/cache/queue behavior;
`tools/test_tsf_startup.bat` tests actual password controls and an alternate
desktop. These are isolated checks, not real credential-entry acceptance.
The protected-input TSF builds are staged under
`next/_run/overlay-startup-diagnosis/TSF-{Win32,x64,ARM64}/`; they have not been
installed or copied to the daily runtime. Replacing Core alone does not enable
these TSF key-path changes.

2026-09-11 validation: Release TSF Win32/x64/ARM64 and Native Hook x64/ARM64
built in `next/_run/hash-removal-validation/` (per-target logs beside outputs).
`tools/test_tsf_pipe.bat` and `tools/test_hook_native.bat` passed, including
isolated connection/reconnection to the test executable. Metadata and publish
tests are `tools/test_publish_arm64.ps1` and `tools/test_publish_tsf.ps1`.
No registration or deployment was performed. Loaded-DLL identity, input,
candidates, commits and reconnect in 32-bit WPS remain untested.

## Source Of Truth

- Chinese/English mode source: **TigerClaw Core**.
- TSF language bar icon: display-only mirror of Core `keyboard_open`.
- Any external/system compartment drift is corrected back to the cached Core state.

## Build Set (active in `BimeTSF2.vcxproj`)

- `ActiveLanguageProfileNotifySink.cpp`
- `BaseWindow.cpp`
- `Compartment.cpp`
- `DllMain.cpp`
- `DisplayAttributeInfo.cpp`
- `DisplayAttributeProvider.cpp`
- `EnumDisplayAttributeInfo.cpp`
- `EnumTfCandidates.cpp`
- `EditSession.cpp`
- `FunctionProviderSink.cpp`
- `Globals.cpp`
- `KeyEventSink.cpp`
- `LanguageBar.cpp`
- `PipeClient.cpp`
- `Register.cpp`
- `RegKey.cpp`
- `Server.cpp`
- `SampleIMEBaseStructure.cpp`
- `SampleIME.cpp`
- `TfInputProcessorProfile.cpp`
- `ThreadMgrEventSink.cpp`
- `TipCandidateList.cpp`
- `TipCandidateString.cpp`

`BaseWindow.cpp` is still active because `Globals.cpp` uses it for TSF window-class registration.

Legacy SampleIME documents and inactive composition/candidate/dictionary source files have been removed from this tree. Reference copies remain under `../../reference/SampleIME/`.

Current builds have no embedded expiry or calendar-based input gate. Previously
installed DLLs keep their old checks until replaced.

## Read-only candidate UIElement / Pain compatibility

`CandidateUi.h` projects optional Core `candidate_ui` snapshots through
`ITfCandidateListUIElement`. Core still owns candidate generation, ordering,
selection, paging, commit and learning. No `ITfCandidateListUIElementBehavior`
is exposed; mouse selection through the host is not implemented.

Begin publishes all six update flags, subsequent notifications describe changed
strings/count/selection/page starts/current page. Getters never call Core or pump
messages; `GetPageIndex(nullptr, 0, &count)` succeeds for Pain's first size query.
Document references and retained COM objects remain valid through End. Commit
with remaining input updates the same object; cancel, loss of focus, context
replacement, deactivation and disconnect end it. Local focus/connection epochs,
Core instance/version and the echoed request context reject stale snapshots.

This first version intentionally permits Pain and TigerClaw Overlay together.
Begin's `pbShow=FALSE` does not suppress updates. `Show(FALSE)` does not hide
Overlay, and `IsShown` queries the actual `TigerClawCandidate` window visibility.
Do not advertise this as complete UILess support. Old Core without the optional
protocol continues without a candidate UIElement. No registration flags or user
settings change.

Isolated verification (no installation or TSF registration):

- `tools/test_candidate_ui.bat x86` (also `x64` / `arm64`): actual COM getters,
  notification order, `pbShow=FALSE`, reference lifetimes/reentry, pipe framing,
  malformed snapshots, old Core fallback and stale focus context.
- `tools/test_pain_candidate_bridge.ps1`: compiles Pain's existing presenter
  read-only and a copy of its test fixture, injecting the native COM object into
  the real Begin/Update/Render path. Runs as x86; requires the x86 factory above.
- CTest `core_native_candidate_snapshot`: display parity, full ordinary pages,
  Unicode, reverse lookup, quick symbols, residual input, auto commit, empty code,
  export limits, replay/focus and asynchronous sentence projection.

Artifacts/logs: `next/_run/CandidateUi-20261011/`. These checks do not claim physical
typing acceptance in the actual running Pain application.
