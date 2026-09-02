# TigerClaw Next

Current handoff and document index: `../AGENTS.md`, `../docs/README.md`.

This directory contains the active split-process runtime:

- `TigerClaw.Core`
- `TigerClaw.Overlay`
- `TigerClaw.Dialog`
- `TigerClaw.Shared`
- `TigerClaw.Sentence.Native` (C++ Sentence host and llama.cpp scorer)
- `TigerClaw.Hook.Native` experimental native hook frontend

Build:

```batch
next\build_next.bat
```

Debug output:

```text
next\_run\Debug\net48\
next\_run\Debug\native\
```

Build outputs are disposable, but this checkout's ignored `../release_arm64/`
is the user's daily runtime and must not be removed by broad clean commands.
