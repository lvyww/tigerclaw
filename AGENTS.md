# AGENTS.md

## Project Overview

Current active runtime is **next split-process**:

- **Active runtime**: `next/` (split process)
  - `TigerClaw.Core`
  - `TigerClaw.Overlay`
  - `TigerClaw.Dialog`
  - `TigerClaw.Shared`
- **Experimental native frontend**: `next/TigerClaw.Hook.Native/`
  - C++ hook frontend skeleton
  - currently wired into solution, `build_next.bat`, and `publish.bat`
  - does not replace TSF default flow unless explicitly launched
- **Active TSF packaging/build project**: `BimeTSF2/SampleIME/`
- **Active publish entry**: `publish.bat`
- **IPC docs**: `Protocol/messages.md`, `Protocol/entrypoints.md`, `Protocol/ui_messages.md`

Current notable behavior:

- Overlay typing sound is implemented in `next/TigerClaw.Overlay/TypingSoundPlayer.cs`
  - normal keys use a `MediaPlayer` pool
  - space and function/backspace keys use dedicated slots
  - failed slots are auto-recreated
- Code masking (`编码伪装`) is currently applied in `next/TigerClaw.Core/ProtocolHandler.cs` on outward display data
  - internal Core input buffer remains raw
  - TSF/Overlay receive masked display code
- Candidate window currently does **not** show input code; code display is mainly via composition
- `TigerClaw.Hook.Native` is currently experimental and should not change the default TSF workflow
  - `next\build_next.bat` builds it into `next\_run\Debug\native\TigerClaw.Hook.Native.exe`
  - `publish.bat` includes it in `release\TigerClaw.exe`
- Settings window implementation is in `next/TigerClaw.Dialog/ConfigWindow.xaml` and `ConfigWindow.xaml.cs`
  - grouped form layout
  - search/filter
  - `按键音量0~100` uses a slider
  - empty-value config items must remain visible in parsing/UI

Deprecated implementations were removed from root:

- `bime/` removed
- `BimeTSF/` removed

Reference-only upstream source trees are kept under:

- `reference/bime-master/`
- `reference/SampleIME/`
- `reference/weasel/`

---

## Build Commands

### Mainline Build

```batch
next\build_next.bat
```

Builds debug artifacts to:

```text
next\_run\Debug\net48\
```

### Publish (Release)

```batch
publish.bat
```

Builds release artifacts and copies to:

```text
release\
```

Current release payload:

- `TigerClaw.Core.exe`
- `TigerClaw.Overlay.exe`
- `TigerClaw.Dialog.exe`
- `TigerClaw.exe`
- `TigerClaw.Shared.dll`
- `x64/TigerClaw.dll`
- `Win32/TigerClaw.dll`

### Batch Script Line Endings

- All `.bat` files must use CRLF line endings (`\r\n`).
- For text-file rewrites that are sensitive to encoding, Chinese filenames, or CRLF preservation, prefer a short Python script over PowerShell string replacement.
- This preference is especially important for `.bat` files and other non-UTF-8 text files.

---

## Manual Test Flow

No automated tests yet. Use manual smoke test:

```batch
next\build_next.bat
next\register_dev_corepath.bat
next\_run\Debug\net48\TigerClaw.Core.exe --with-overlay
```

Then test in target apps and unregister if needed:

```batch
next\unregister_dev_corepath.bat
```

---

## C# Code Style

### Imports

System namespaces -> third-party -> project namespaces. Keep alphabetical order.

### Naming

| Element | Convention | Example |
|---------|------------|---------|
| Namespace | Pascal/lowercase by existing project style | `TigerClaw.Core` |
| Class/Method/Property | PascalCase | `ProtocolHandler`, `Handle` |
| Private field | `_underscorePrefix` | `_state`, `_uiStatePublisher` |
| Local variable | camelCase | `message`, `response` |
| Constant | PascalCase or UPPER | `PipeName`, `VK_RETURN` |

### Formatting

- Indent: 4 spaces
- Braces: Allman style where existing files use it
- Keep edits consistent with surrounding file style

### Native Hook / C++ Notes

- When debugging window-title or IME compatibility issues, prefer logging unambiguous values such as hex code points in addition to human-readable text. Do not rely on terminal rendering of CJK strings alone.
- For high-frequency diagnostics such as caret polling, keep logs disabled by default during targeted debugging. Remove or silence noisy logs once they stop serving the current investigation.
- Do not rely on raw C++ source-file Chinese string literals for runtime matching in native code when cross-machine stability matters. Prefer code-point construction for non-ASCII match tokens.
- When constructing non-ASCII strings from code points for matching, add an end-of-line comment showing the intended readable text, for example `// Pain打器` or `// 跟打`.

---

## IPC Notes

Transport:

- Named pipe: `\\.\pipe\BimeIPC`
- UTF-8 JSON per line (`\n`)

Common message types in active code:

- `key`
- `caret`
- `focus`
- `hello`
- `query_state`
- `ctrl_space`
- `show_menu`
- `response`

Use `Protocol/messages.md` as the contract source.

---

## Project Structure

```text
bime_codex/
|-- BimeTSF2/                      # Active TSF build/package source
|-- next/                          # Active source
|   |-- TigerClaw.Core/
|   |-- TigerClaw.Overlay/
|   |-- TigerClaw.Dialog/
|   |-- TigerClaw.Hook.Native/
|   `-- TigerClaw.Shared/
|-- Protocol/                      # Active protocol docs
|-- reference/                     # Reference-only upstream source
|   |-- bime-master/
|   |-- SampleIME/
|   `-- weasel/
|-- release/                       # Publish output
|-- publish.bat                    # Only root bat entry
`-- AGENTS.md
```

---

## Common Tasks

### Add/Change IPC Message

1. Update `Protocol/messages.md`
2. Update active handler path:
   - `next/TigerClaw.Core/ProtocolHandler.cs`
   - related UI/reader code in `next/TigerClaw.Overlay/` or `next/TigerClaw.Dialog/`

### Modify Key Handling

1. `next/TigerClaw.Core/InputMethodEngine.cs`
2. `next/TigerClaw.Core/ProtocolHandler.cs`
3. verify overlay display in `next/TigerClaw.Overlay/`

### Modify Display-Only Code Rendering

1. If the change affects what TSF/Overlay should display but not how Core computes input, prefer changing:
   - `next/TigerClaw.Core/ProtocolHandler.cs`
2. Do not change protocol shape unless there is a clear need.
3. Keep raw Core input state and outward display state conceptually separate.

### Modify Settings Window

1. Main files:
   - `next/TigerClaw.Dialog/ConfigWindow.xaml`
   - `next/TigerClaw.Dialog/ConfigWindow.xaml.cs`
2. Preserve:
   - grouped form layout
   - search/filter
   - dirty-state feedback
   - support for empty-string config values

### Release Packaging

1. Run `publish.bat`
2. Check output files under `release\`
3. Confirm TSF payload exists under `release\x64\` and `release\Win32\`
