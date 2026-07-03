# Runtime Entry Points

Current handoff and architecture entry: `../AGENTS.md`.

This file is intentionally small. The previous detailed entrypoint notes referred to old implementation paths that are no longer active.

Current high-level flow:

```text
BimeTSF2/SampleIME KeyEventSink.cpp
  -> BimeTSF2/SampleIME PipeClient.cpp
  -> \\.\pipe\BimeIPC
  -> next/TigerClaw.Core PipeServer.cs
  -> next/TigerClaw.Core ProtocolHandler.cs
  -> next/TigerClaw.Core InputMethodEngine.cs
```

Detailed message fields are in `Protocol/messages.md`.
