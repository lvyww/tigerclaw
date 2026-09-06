# TigerClaw.Core Portability Scan

This is a scan-based matrix, not a compile-probe. It is enough to guide the next spike, but not enough to claim final reuse percentages.

## LOC

`find next/TigerClaw.Core -maxdepth 1 -name '*.cs' -print0 | xargs -0 wc -l`

Total top-level C# LOC in `next/TigerClaw.Core`: `13,928`.

## Windows / Runtime Dependency Hits

Fixed-string scan covered:

`Microsoft.Win32`, `Registry`, `System.Windows`, `System.Windows.Forms`, `NamedPipe`, `MemoryMappedFile`, `DllImport`, `kernel32`, `user32`, `ProcessStartInfo`, `HWND`, `\\.\pipe`, `Local\\`, `Environment.GetFolderPath`, `OperatingSystem`, `RuntimeInformation`, `Process.Start`.

Key hits:

- `CoreRuntimeState.cs`: `Microsoft.Win32`, startup registry, Windows app-data path.
- `InputMethodEngine.cs`: `System.Windows.Forms` message box only.
- `Program.cs`: `System.Windows.Forms` startup warning UI.
- `PipeServer.cs`: Windows named-pipe host IPC.
- `SentenceRerankClient.cs`: named-pipe sidecar IPC.
- `ProtocolHandler.cs`: `ProcessStartInfo` / `Process.Start` to launch Windows UI processes.
- `ProcessLauncher.cs`: `ProcessStartInfo` / `Process.Start`.
- `HeartbeatBroadcaster.cs`, `OverlayLaunchSupervisor.cs`: memory-mapped heartbeat.
- `SentenceNgramModel.cs`: file-backed `MemoryMappedFile`.

## Matrix

| File | LOC | Category | Reason |
|---|---:|---|---|
| `CoreRuntimeState.cs` | 4718 | C - Platform Abstraction | Mostly config, lexicon, schema, and engine state, but contains Windows app-data and registry startup logic. |
| `InputMethodEngine.cs` | 4340 | B - Minor Change | Core key/candidate behavior is high-value reusable logic; visible Windows dependency is a Forms message box. |
| `SentenceInputDecoder.cs` | 1359 | A - Direct Reuse | Decoder logic showed no scanned Windows runtime dependency. |
| `ProtocolHandler.cs` | 977 | C - Platform Abstraction | Protocol shaping is reusable, but launching overlay/dialog is Windows runtime behavior. |
| `SentenceNgramModel.cs` | 515 | B - Minor Change | File-backed MMF may work cross-platform, but needs macOS validation and fallback policy. |
| `SentenceRerankClient.cs` | 268 | D - Windows Runtime | Named-pipe sidecar IPC should be replaced by Unix socket/XPC/sidecar bridge on macOS. |
| `SimpleJson.cs` | 252 | A - Direct Reuse | No scanned Windows dependency. |
| `PipeServer.cs` | 233 | D - Windows Runtime | Windows named-pipe server for TSF/Core IPC. |
| `Program.cs` | 209 | D - Windows Runtime | Windows process entry and Forms warnings, not the macOS engine entry. |
| `MixedInputDecoder.cs` | 187 | A - Direct Reuse | No scanned Windows dependency. |
| `SentenceSupplementModel.cs` | 177 | A - Direct Reuse | No scanned Windows dependency. |
| `ProcessLauncher.cs` | 165 | D - Windows Runtime | Windows process launching/publish layout. |
| `OverlayLaunchSupervisor.cs` | 144 | D - Windows Runtime | Overlay heartbeat through MMF. |
| `SentenceIsolationPenalty.cs` | 97 | A - Direct Reuse | No scanned Windows dependency. |
| `KeyRequestReplayCache.cs` | 95 | A - Direct Reuse | No scanned Windows dependency. |
| `SentenceCharacterRanks.cs` | 62 | A - Direct Reuse | No scanned Windows dependency. |
| `SentenceCommonCharacters.cs` | 55 | A - Direct Reuse | No scanned Windows dependency. |
| `HeartbeatBroadcaster.cs` | 49 | D - Windows Runtime | Core heartbeat through MMF. |
| `OverlayMenuSignal.cs` | 23 | D - Windows Runtime | Windows overlay menu signal. |
| `AssemblyInfo.cs` | 3 | A - Direct Reuse | Metadata only. |

## Approximate LOC Buckets

These are scan-derived, not compile-proven:

- A - Direct Reuse: `2,287 LOC` (`16.4%`)
- B - Minor Change: `4,855 LOC` (`34.9%`)
- C - Platform Abstraction: `5,695 LOC` (`40.9%`)
- D - Windows Runtime: `1,091 LOC` (`7.8%`)

Interpretation: the largest behavior body is not a Rust-scale rewrite problem, but the current C# Core mixes platform runtime concerns into `CoreRuntimeState` and protocol/process layers. A proper engine extraction spike should start by carving a modern .NET engine project around `InputMethodEngine`, decoder/model files, schema/config read paths, and a small host-neutral result model.

## Next Compile Probe

Create an isolated project that links or copies only the A/B files first:

- `InputMethodEngine.cs`
- `MixedInputDecoder.cs`
- `SentenceInputDecoder.cs`
- `SentenceIsolationPenalty.cs`
- `SentenceSupplementModel.cs`
- `SentenceCharacterRanks.cs`
- `SentenceCommonCharacters.cs`
- `SimpleJson.cs`
- minimal config/lexicon subset from `CoreRuntimeState.cs`

Expected initial result: `MINOR_PATCH_REQUIRED` or `PLATFORM_ABSTRACTION_REQUIRED`, depending on how much of `CoreRuntimeState` is included.
