# Swift to .NET NativeAOT Bridge Result

## Result

`PASS` for command-line Swift -> C# NativeAOT C ABI.

`PASS` for the bounded end-to-end IME bridge spike: an isolated `IMKInputController`
routes a physical keyboard event through Swift and the C# NativeAOT engine into
TextEdit.

## Implemented ABI

The dummy C# engine exports:

- `engine_create`
- `engine_destroy`
- `engine_process_key`
- `engine_get_preedit`
- `engine_get_commit`
- `engine_free`

Behavior:

- `engine_process_key("a")` sets preedit to `TigerClaw Test`.
- `engine_process_key("space")` commits `TigerClaw Test` and clears preedit.
- Invalid keys return unhandled.

## Verification

- Publish: `dotnet publish spikes/swift-dotnet-bridge/DummyEngine/DummyEngine.csproj -c Release -r osx-arm64` -> passed.
- Export check: `nm -gU .../DummyEngine.dylib` showed all six expected `_engine_*` symbols.
- Swift link/run: `swiftc ... DummyEngine.dylib ...` followed by `DYLD_LIBRARY_PATH=... swift-dotnet-bridge-test` -> passed.

Observed output:

```text
SWIFT_DOTNET_C_ABI_PASS elapsed_us=1386 avg_call_us=0.6919620569146281 preedit=TigerClaw Test commit=TigerClaw Test
```

The Swift client also exercises invalid-key handling, repeated key calls, and repeated create/destroy lifecycle.

## Artifacts

- NativeAOT library: `spikes/swift-dotnet-bridge/DummyEngine/bin/Release/net10.0/osx-arm64/publish/DummyEngine.dylib`
- Publish directory size: about `5.8M`
- Swift smoke executable: `spikes/swift-dotnet-bridge/SwiftClient/swift-dotnet-bridge-test`

## Decision Impact

NativeAOT C ABI is viable enough to continue as the preferred bridge candidate. It avoids embedding CoreCLR in Swift and gives Swift ownership-explicit string allocation/free semantics.

The next required gate is real IMK integration: `NSEvent` -> Swift `IMKInputController` -> C# NativeAOT -> `setMarkedText` on `a` -> `insertText` on Space in TextEdit.

## Independent IMK Host Registration Follow-up

An isolated Xcode host target now exists at
`spikes/swift-dotnet-bridge/XcodeHost/TigerClawHybridSpike.xcodeproj`. It keeps the
bundle identifier `net.tigerclaw.inputmethod.HybridSpike`, bundles the NativeAOT
library, and builds and verifies with a development signature.

The current macOS login session has retained a stale input-source registration:

- `TISRegisterInputSource` and `TISEnableInputSource` both returned `0`.
- The hybrid mode appears in the installed-source list and reports enabled.
- Its parent source remains absent from the enabled-source roster, so the mode cannot
  be selected through `TISSelectInputSource`.
- Restarting `TextInputMenuAgent` did not refresh that roster.

The System Settings **Add Input Source** flow successfully added the hybrid mode and
made it selectable. This is the reliable replacement for repeated logout/login during
development; keep the same signed bundle and avoid changing its identifier or signature.

## IMK Host Runtime Follow-up

The isolated host has passed these additional gates:

- Xcode build and development-signature verification pass.
- The hybrid input source is enabled and selectable beside the formal TigerClaw source.
- `IMKServer`, `TigerClawInputController`, and the NativeAOT engine all initialize in
  the input method's sandbox container.
- Runtime tracing records real `handleEvent:client:` delivery into the controller and
  NativeAOT handling results for unhandled keys.

Final physical-keyboard E2E validation passed in TextEdit while
`net.tigerclaw.inputmethod.HybridSpike.Hans` was selected:

- `a` set marked text to `TigerClaw Test`.
- Space committed `TigerClaw Test`.

Computer Use key injection is not used as this assertion because it bypasses the macOS
input method event path; the confirmation above came from a real keyboard.
