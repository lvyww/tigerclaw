# TigerClaw Real IME NativeAOT Host Spike

Phase 3–6 InputMethodKit vertical slice used for the current physical macOS
development build.

This spike is intentionally isolated under `spikes/real-ime-nativeaot-host/`.
It builds a macOS IMK app bundle that starts an `IMKServer`, creates and
activates a NativeAOT runtime session, maps `NSEvent` key data into
`tc_input_event`, applies snapshot preedit as marked text, commits the snapshot
commit text, presents candidates in an AppKit `NSPanel`, and records candidate
list order in
`~/Library/Logs/TigerClaw/nativeaot-imk-spike.log`.

The build script itself does not mutate the user's installed input sources.
Development builds may be signed, copied to `~/Library/Input Methods`, and
registered with `macos/scripts/register-input-source.swift` as a separate
explicit installation step. The registration script also verifies the real
`TextInputMenuCore` menu roster. Carbon's `TISEnableInputSource` can otherwise
report success while leaving a ghost input source that runs under the ABC menu
item.

## Build

Run from the repository root:

```sh
zsh spikes/real-ime-nativeaot-host/scripts/build_package.sh
```

The script publishes the Phase 2 NativeAOT dylib, copies the published C header
and dylib into this spike's `TigerClaw/Vendor/`, copies the test lexicon into
the app resources, then runs `xcodebuild`.

Output:

```text
spikes/real-ime-nativeaot-host/dist/TigerClawRealImeNativeAotHost.app
```

## Offline Smoke Test

```sh
zsh spikes/real-ime-nativeaot-host/scripts/smoke.sh
```

Expected marker:

```text
NATIVEAOT_IMK_SMOKE_PASS
Scenario=a -> 来 -> Space
MixedScenario=aA -> 来A -> 来来
```

The smoke test runs the app executable with `--nativeaot-smoke` and
`--nativeaot-windows-parity-smoke`; it exercises the same bridge/session/
snapshot code path without registering the input method or requiring a text
client. The parity pass checks the selected-schema sentence contract shared
with Windows: table-local `用户调整.txt`, `补充语料.txt`, bracket fast symbols,
and quick-phrase expansion.

## Mixed-input development switch

The installed executable persists the runtime switch in its application
defaults domain:

```sh
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-mixed-input on
```

Use `off` to return to the basic fixed-length path. A newly created IMK
controller reads this setting when it creates its NativeAOT runtime.

## Configuration and schema diagnostics

The same executable exposes a development-only configuration boundary. It is
stored in the sandboxed host's preferences and becomes active when an IMK
controller next activates:

```sh
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-config show
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-config set page-size 7
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-schema show
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-schema list
```

Supported configuration includes the ordinary candidate and key settings, plus
the sentence controls `sentence-optimal-code-high-frequency-limit`,
`sentence-full-code-whitelist`, and
`sentence-allow-duplicate-single-characters`. The defaults mirror Windows:
the first 1500 high-frequency characters use only their preferred code, the
Windows full-code whitelist is retained, and duplicate single-character paths
are allowed. The current bundle has one registered real schema,
`tiger_sentence`; schema selection is deliberately kept as a registry boundary
until more portable code tables are available.

## Development user dictionary

The host owns a UTF-8 tab-separated user dictionary inside its sandboxed
application-support data root. Additions are promoted ahead of bundled
candidates for that exact code and become active when the IMK controller next
activates:

```sh
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-user-dictionary add a 自定义
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-user-dictionary list
"$HOME/Library/Input Methods/TigerClawRealImeNativeAotHost.app/Contents/MacOS/TigerClawRealImeNativeAotHost" --tigerclaw-user-dictionary remove a 自定义
```

The Phase 8 development Avalonia Settings app reads and changes entries through
these validated host commands, rather than opening the sandbox file. Production
sharing still requires the App Group signing gate described in
`docs/macos-data-sharing.md`.

## User Interaction E2E

Full IMK text-client E2E is not automated because macOS only
routes real application key events to an input method after the bundle is
registered, enabled, and selected as an input source. Offline smoke therefore
proves the packaged bridge; physical client checks remain a separate gate.
