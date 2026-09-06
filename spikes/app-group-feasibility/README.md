# macOS App Group Feasibility Spike

This is a minimal, uninstalled macOS sandbox experiment for checking whether two
independent bundles can share an App Group container. It models the future
TigerClaw IME and Settings surfaces without installing or registering an input
method.

The spike intentionally keeps signing identities, certificate subjects, Team IDs,
and provisioned group IDs out of tracked files and reports. Scripts print only
counts and redacted capability status.

## What It Builds

- `IMEProbe.app`
- `SettingsProbe.app`

Both apps contain the same tiny Swift executable, but use distinct bundle
identifiers and the same generated App Group entitlement. The Settings probe
writes a value to the group container; the IME probe reads it back.

## Run

```sh
cd spikes/app-group-feasibility
./scripts/check_environment.sh
APP_GROUP_ID='group.example.tigerclaw.feasibility' \
SIGN_IDENTITY='-' \
./scripts/build_and_run.sh
```

Use `SIGN_IDENTITY='-'` for an ad-hoc local signature. That is useful for
reproducing the build shape, but it is not proof that a production App Group is
available. For a real capability check, pass a development signing identity and
an App Group identifier provisioned for both bundle IDs:

```sh
APP_GROUP_ID='group.your.provisioned.group' \
SIGN_IDENTITY='Apple Development: <redacted>' \
./scripts/build_and_run.sh
```

Optional bundle identifiers:

```sh
IME_BUNDLE_ID='com.example.tigerclaw.imeprobe' \
SETTINGS_BUNDLE_ID='com.example.tigerclaw.settingsprobe' \
APP_GROUP_ID='group.example.tigerclaw.feasibility' \
SIGN_IDENTITY='-' \
./scripts/build_and_run.sh
```

## Success Criteria

The run is useful when it proves one of these:

- Build, signing, and sandboxed execution complete.
- `SettingsProbe.app` writes to the App Group container.
- `IMEProbe.app` reads the exact value from the same container.

If signing/provisioning is missing, the scripts still leave repeatable artifacts
under `build/` and print the smallest blocking evidence without exposing local
identity details.

