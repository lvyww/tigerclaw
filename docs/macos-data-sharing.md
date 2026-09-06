# macOS Data Sharing Decision

Status: Phase 0 decision record for the .NET Hybrid route.

The current macOS InputMethodKit host is sandboxed. Its entitlement file enables
`com.apple.security.app-sandbox` and a temporary Mach registration exception,
but it does not enable an App Group. The spike and formal host projects also use
manual signing with an empty `DEVELOPMENT_TEAM`, so App Group feasibility is
not proven in this checkout.

## Required shared data

The IME host and Settings app eventually need access to:

- active configuration;
- schema/code table files;
- user dictionary and adjustment data;
- model metadata and diagnostics.

The engine must not hard-code a shared path until signing and entitlement
constraints are validated.

## Options

| Option | Status | Notes |
| --- | --- | --- |
| App Group container | preferred, locally proven; production pending | A separate uninstalled sandbox probe built two bundles, wrote shared state from Settings and read it from IME. Production still requires the same provisioned entitlement on both TigerClaw bundles. |
| Controlled local IPC | fallback | Settings sends validated changes to the running engine/host, which owns writes. Avoids shared filesystem assumptions but needs lifecycle/retry handling. |
| Supported shared file location | risky | A sandboxed IME cannot assume arbitrary access to `~/Library/Application Support/TigerClaw/`. Use only after entitlement and runtime access tests. |
| Import/export | limited fallback | Safe for manual schema/config transfer, but not suitable for live settings or user-dictionary updates. |

## Local feasibility evidence

`spikes/app-group-feasibility/` builds and ad-hoc signs two independent,
sandboxed macOS app bundles. Its Settings-role probe writes a value through
`FileManager.containerURL(forSecurityApplicationGroupIdentifier:)`; its
IME-role probe reads the exact value back. The test never installs or registers
an input source.

The local host has no provisioning profile containing App Group entitlement.
Therefore this is an architectural feasibility result, not release-signing
evidence. The repository deliberately does not record a Team ID, certificate
subject or production group identifier.

## Decision for Phase 1

Use an abstract data-root provider in the engine and keep the concrete macOS
storage location outside the hot key path. Phase 1 may load fixtures or explicit
test paths, but the production path remains undecided.

The production validation gate is now narrower: configure one production App
Group entitlement for the InputMethodKit host and Avalonia Settings app through
the Apple Developer account, sign both bundles with that configuration, then
repeat the probe's write/read test. Only then may the implementation store
config, schemas or user data in an App Group container by default.

## Current Phase 8 development boundary

Until that production gate is passed, the signed IME host owns a private
sandbox data root under its application-support container. It contains the
development user dictionary as a UTF-8 tab-separated `text<TAB>code` file and
is passed explicitly to NativeAOT when a runtime is created. The boundary is
deliberately local to the host: it proves user-entry precedence and reload-on-
activation without implying that a future Avalonia Settings app may access the
same files. The current Avalonia Settings development app instead invokes the
installed host's validated command interface to read and update configuration,
selected schema and user dictionary entries. It never opens the host's sandbox
data files directly, and the host remains the only writer of those files. This
is a controlled-local-command bridge, not production IPC: App Group migration
will replace the root provider and control channel, not the engine file format
or ABI field.
