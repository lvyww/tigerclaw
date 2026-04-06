# TigerClaw UI Bridge Messages (Draft v1)

This document defines the planned UI split protocol between:

- `TigerClawCore.exe`
- `TigerClawOverlay.exe` (status + candidate windows)
- `TigerClawDialog.exe` (settings + add-word windows)

Transport recommendation: named pipe, UTF-8 JSON, one message per line.

## Envelope

```json
{
  "type": "candidate_update",
  "seq": 1,
  "ts": 1700000000,
  "payload": {}
}
```

Fields:

- `type`: message type
- `seq`: monotonic sequence id per channel
- `ts`: unix timestamp (seconds or milliseconds, consistent per channel)
- `payload`: message body

## Core -> Overlay

- `status_update`
  - `payload`: `{ "is_off": bool, "is_chinese": bool, "display_text": string }`
- `candidate_update`
  - `payload`: `{ "visible": bool, "input_code": string, "items": [string] }`
- `caret_update`
  - `payload`: `{ "x": int, "y": int }` (physical pixels)
- `ui_config`
  - `payload`: font/theme/layout settings used by overlay windows

## Overlay -> Core

- `overlay_ready`
  - startup handshake
- `menu_command`
  - `payload`: `{ "command": "open_settings|open_addci|reload_mb|exit" }`

## Core -> Dialog

- `show_settings`
- `show_addci`
  - `payload`: optional initial text/code seed
- `settings_snapshot`
  - all effective settings used to render dialog controls
- `construct_code_result`
  - used by add-word dialog when requesting code generation

## Dialog -> Core

- `save_settings`
  - `payload`: key/value patch
- `addci_submit`
  - `payload`: `{ "code": string, "text": string }`
- `construct_code_request`
  - `payload`: `{ "text": string }`

## Notes

- Overlay windows should not call focus-stealing APIs.
- Candidate window should avoid `Hide()/Show()` for topmost refresh.
- Dialog window can be focusable and normal-activation.
