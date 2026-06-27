# TigerClaw Next (Three-EXE Skeleton)

This directory contains the new split-process skeleton:

- `TigerClaw.Core` (console host, visible for debugging)
- `TigerClaw.Overlay` (WPF overlay host)
- `TigerClaw.Dialog` (WPF dialog host: settings/add-ci)
- `TigerClaw.Shared` (shared constants/contracts placeholder)
- Solution: `next/TigerClaw.Next.sln`

Current goal:

1. keep existing `bime/` and `BimeTSF2/` untouched for rollback.
2. build new projects in isolation.
3. migrate logic incrementally.

## Quick start (dev)

1. Build:
   `next\build_next.bat`
2. Register CorePath for TSF launch:
   `next\register_dev_corepath.bat`
3. Run Core:
   `next\TigerClaw.Core\bin\Debug\net48\TigerClaw.Core.exe`

Notes:

- Core currently hosts a minimal `\\.\pipe\BimeIPC` server for TSF handshake/testing.
- Core does not start Overlay by default (`--with-overlay` to enable).
- TSF `show_menu` currently triggers Dialog config window launch.
- Overlay/Dialog currently only contain heartbeat timeout skeleton behavior.
