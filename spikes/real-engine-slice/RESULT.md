# Phase 1 result

Date: 2026-08-27

The first isolated .NET Hybrid Engine slice builds and runs on .NET 10 without
touching the Windows Core runtime.

## Dependency closure

| Dependency | Included in slice | Notes |
| --- | --- | --- |
| Platform key event contract | yes | `InputEvent`, `InputKey`, `InputModifiers`, `KeyAction` |
| Real lexicon file | yes | loads `rime/tiger_sentence/tiger_sentence.codes.txt` |
| Basic input state | yes | per-engine code buffer and selection index |
| Candidate generation | yes | exact-code lookup, repository lexicon order |
| Space commit | yes | commits selected candidate and clears composition |
| Session isolation shape | partial | separate engine instances do not share buffers |
| NativeAOT ABI | no | Phase 2 |
| Full C# `InputMethodEngine` parity | no | Phase 4+ |

## Latest verification

Build:

```sh
/Users/wuzz/.dotnet/dotnet build spikes/real-engine-slice/TigerClaw.Engine.Experimental.Tests/TigerClaw.Engine.Experimental.Tests.csproj
```

Result: 0 warnings, 0 errors.

Run:

```sh
/Users/wuzz/.dotnet/dotnet run --project spikes/real-engine-slice/TigerClaw.Engine.Experimental.Tests/TigerClaw.Engine.Experimental.Tests.csproj
```

Result:

```text
REAL_ENGINE_SLICE_PASS
LexiconEntries=36698
Dictionary=rime/tiger_sentence/tiger_sentence.codes.txt
Fixture=tests/engine_cases/basic_real_code_commit.json
```
