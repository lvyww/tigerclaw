# BimeTSF2 Bridge-Only Notes

## Scope

BimeTSF2 now runs in bridge-only mode:

- Capture TSF key/focus/caret events.
- Forward to TigerClaw Core through `\\.\pipe\BimeIPC`.
- Apply Core response (`handled`, `commit_text`, `keyboard_open`) back to TSF/UI.

TSF side is **not** the source of IME state, composition state, or candidate logic.

## Source Of Truth

- Chinese/English mode source: **TigerClaw Core**.
- TSF language bar icon: display-only mirror of Core `keyboard_open`.
- Any external/system compartment drift is corrected back to the cached Core state.

## Build Set (active in `BimeTSF2.vcxproj`)

- `BaseWindow.cpp`
- `Compartment.cpp`
- `DllMain.cpp`
- `EditSession.cpp`
- `FunctionProviderSink.cpp`
- `Globals.cpp`
- `KeyEventSink.cpp`
- `LanguageBar.cpp`
- `PipeClient.cpp`
- `Register.cpp`
- `RegKey.cpp`
- `Server.cpp`
- `SampleIMEBaseStructure.cpp`
- `SampleIME.cpp`
- `TfInputProcessorProfile.cpp`
- `ThreadMgrEventSink.cpp`

## Legacy Code

SampleIME legacy composition/candidate/display-attribute paths remain in tree for reference, but are excluded from current bridge-only compile path.
