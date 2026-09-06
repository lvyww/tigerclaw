# TigerClaw macOS Host Spike

This directory contains the Phase 0.5 InputMethodKit host spike. It is a
standalone Swift/Xcode application with no Rust engine dependency and no input
or composition behavior yet.

The host keeps one `IMKServer` alive for the application lifetime. InputMethodKit
creates one `TigerClawInputController` per client input session; the controller
currently records creation, activation, deactivation, close, and received-event
lifecycle signals only.

## Build

```zsh
xcodebuild \
  -project macos/TigerClaw.xcodeproj \
  -scheme TigerClaw \
  -configuration Debug \
  -derivedDataPath /tmp/tigerclaw-macos-build \
  CODE_SIGNING_ALLOWED=NO \
  build
```

The target is Apple Silicon and has a deployment target of macOS 13.0.

## Local installation for the host spike

Use Xcode's automatic Apple Development signing for the local development copy,
then place the app in the per-user Input Methods directory. A free Personal
Team is sufficient for this local build; Developer ID is a distribution concern,
not a development prerequisite. An ad-hoc signature is acceptable only for a
local host-spike diagnostic, not for external distribution.

```zsh
ditto /tmp/tigerclaw-macos-build/Build/Products/Debug/TigerClaw.app \
  "$HOME/Library/Input Methods/TigerClaw.app"
```

The input-source IDs and connection name are intentionally stable. Do not
change either while testing because macOS caches input-source registrations.

The normal host startup does not alter input-source preferences. Its bundled
diagnostic installer uses only the public TIS APIs and runs each mutation from
a short-lived TigerClaw executable process: register, enable the parent,
confirm the parent in the enabled roster, enable the Hans mode, then confirm
both objects from a fresh enabled-only roster. This avoids stale TIS references
on macOS 26 and must not be replaced with direct edits to
`com.apple.HIToolbox` preferences.

Adding or selecting the input source is a user-visible system-preference
change. Do not modify `com.apple.HIToolbox` preferences directly or use private
registration APIs to bypass that step.

### System-wide registration comparison

When a user-domain installation is not discoverable, an administrator can run
the guarded comparison installer below. It refuses to overwrite an existing
system-wide TigerClaw installation.

```zsh
sudo macos/scripts/install-system-input-method.sh \
  "$HOME/Library/Input Methods/TigerClaw.app"
```

Pass `--replace` only to update the TigerClaw test copy created by this script.

## Current verification status

Phase 0.5's Platform Gate passed on macOS 26.5. The active diagnostic copy is
one Apple Development-signed user-domain bundle at
`~/Library/Input Methods/TigerClaw.app`, currently `CFBundleVersion` 3. The
old system-domain copy is unregistered and retained only as a `.disabled`
backup, so no duplicate bundle ID remains active.

After a log-out/log-in following this user-domain install, System Settings
listed **虎爪输入法（简体）**. Adding it through the normal UI, selecting its
Hans mode, and then returning to Squirrel produced the following lifecycle
trace: `IMKServer started`, `input controller created`, `input controller
activated`, and `input controller deactivated`. The source was restored to
Squirrel after the test. This confirms metadata discovery, system UI addition,
selection, IMK server creation, and controller lifecycle without private APIs
or preference-file edits.

The Phase 0.5 host appends lifecycle evidence to its sandbox container at
`~/Library/Containers/net.tigerclaw.inputmethod.TigerClaw/Data/Library/Logs/TigerClaw/lifecycle.log`.
This trace is diagnostic-only: failures to write it never affect input handling.

macOS 26 may require a log-out/log-in after a first user-domain IME install
before the picker refreshes. Do not install a second copy with the same bundle
ID while testing.

The host uses a custom `NSApplication` principal class that strongly owns and
sets its delegate. The connection name is
`$(PRODUCT_BUNDLE_IDENTIFIER)_Connection`, and the development entitlement
allows that exact Mach registration name. On this legacy IME bundle, relying
on a Swift `@main` application-delegate class left the process running without
delivering the delegate launch callback; the custom principal class is required
for the `IMKServer` lifecycle to begin.

For development diagnostics, the user-session helper invokes the public TIS
sequence through short-lived TigerClaw executable processes. The default run
registers and enables only; pass `--select` only when intentionally testing
controller activation:

```zsh
xcrun swift macos/scripts/register-input-source.swift \
  "$HOME/Library/Input Methods/TigerClaw.app"

# Optional: select TigerClaw.Hans after the enabled-roster checks pass.
xcrun swift macos/scripts/register-input-source.swift \
  "$HOME/Library/Input Methods/TigerClaw.app" --select

# A normal update deliberately keeps the running IMK host alive so Feishu,
# WeChat, and similar long-lived clients do not retain a dead connection.
# Use this diagnostic-only variant when an immediate host restart is required.
xcrun swift macos/scripts/register-input-source.swift \
  "$HOME/Library/Input Methods/TigerClaw.app" --select --refresh-session
```

## Scope boundary

Do not add marked text, candidate windows, commits, Rust FFI, or global
composition state to this spike. Those come after the Engine/ABI and
multi-session state contracts have been frozen.
