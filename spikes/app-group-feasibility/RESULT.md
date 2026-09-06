# App Group Feasibility Result

Date: 2026-08-27

## Current Host Capability

- Xcode `swiftc`, `codesign`, `security`, and `plutil` are available.
- One valid code-signing identity is visible locally. Identity details are
  intentionally redacted and not recorded here.
- No local provisioning profiles were found.
- No local provisioning profile advertising App Group entitlements was found.

## Verification Run

Command:

```sh
./scripts/check_environment.sh
./scripts/build_and_run.sh
```

Observed result:

- Built `IMEProbe.app` and `SettingsProbe.app`.
- Signed both apps with ad-hoc sandbox/App Group entitlements.
- Ran `SettingsProbe.app` without installing it.
- Ran `IMEProbe.app` without installing it.
- `SettingsProbe.app` wrote shared state to the App Group container.
- `IMEProbe.app` read the exact shared state back.
- The signed `IMEProbe.app` entitlement structure contains sandbox enabled
  and one App Group entry.

The default local ad-hoc run proves that this host can build, sign, and execute
the uninstalled sandbox/App Group shape used by the spike. It does not prove
production readiness for TigerClaw because no provisioned production App Group
capability was present locally.

## Minimum Production Blocker

To convert this from a feasibility spike into production evidence, both future
bundle identifiers need the same provisioned App Group entitlement from the
Apple Developer account. This repo should not store the Team ID, certificate
subject, or real provisioned group identifier.
