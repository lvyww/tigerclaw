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
