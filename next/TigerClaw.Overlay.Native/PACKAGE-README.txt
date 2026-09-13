TigerClaw C++ Overlay - experimental replacement

This package changes the Overlay only. It does not contain Core, TSF, Hook,
Dialog, code tables, config.txt, or sentence models. WPF remains the default.

SAFE PREVIEW
Double-click TigerClaw.Overlay.Native.Preview.exe. It shows synthetic candidates
without connecting to Core or changing settings. Middle-click cycles layouts,
the wheel changes font size, and right-click opens the preview menu. Exit from
that menu. Keep the Preview filename: it isolates Core process discovery.

LIVE REPLACEMENT (requires explicit user action)
Use the package matching the existing runtime architecture.
Close the input method normally before changing executable files.
Back up the existing TigerClaw.Overlay.exe outside the runtime launcher path.
Copy only this package's TigerClaw.Overlay.exe into that runtime directory.
Keep existing config, code tables, sounds and private fonts unchanged.
Restart the input method and test typing, menus, audio and focus changes.
To roll back: close the input method and restore the backed-up WPF executable.
Never overwrite a running executable or delete the runtime directory.

STATUS
Both architectures have passed Windows model and isolated transport tests.
Real-window tests cover MMF-driven state changes, caret recovery, placement,
delayed reveals, focus preservation and stale-heartbeat shutdown.
Silent audio initialization/cleanup has passed. Synthetic preview has been
visually inspected. These are NOT live TSF/Hook or audible-playback acceptance.
Private fonts, mixed-DPI monitors, actual menu actions and live rollback still
need acceptance. Do not treat this as the default production replacement yet.

SHA256SUMS.json identifies every payload file. Preserve the WPF backup.
The build and package tools never deploy into release_arm64 automatically.
