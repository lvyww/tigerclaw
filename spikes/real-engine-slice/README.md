# TigerClaw real engine slice

Status: Phase 1 experimental .NET Hybrid slice.

This directory is intentionally isolated from the Windows `next/TigerClaw.Core`
runtime. It proves the first modern .NET dependency closure that the macOS plan
needs:

- platform-neutral `InputEvent`, `InputKey`, `InputModifiers`, and `KeyAction`;
- host-supplied physical key identity and logical text carried separately;
- deterministic load of the repository's real Rime-exported TigerClaw lexicon;
- Rime selection-suffix normalization (`a`, `a2`, and `a;` become one ordered candidate sequence);
- basic preedit, digit/`;`/`'` candidate selection, candidate paging, and Space commit;
- readonly basic-input configuration for page size, max code length, auto commit, and selection keys;
- two independent engine instances as a proxy for future session isolation.

It is not full `InputMethodEngine` parity. Mixed input, sentence decoding, user
adjustment, schema switching, persisted config semantics, replay identity, and
NativeAOT ABI compatibility policy are later phases.

## Verification

Run from the repository root:

```sh
/Users/wuzz/.dotnet/dotnet run --project spikes/real-engine-slice/TigerClaw.Engine.Experimental.Tests/TigerClaw.Engine.Experimental.Tests.csproj
```

Expected marker:

```text
REAL_ENGINE_SLICE_PASS
LexiconEntries=36698
Dictionary=rime/tiger_sentence/tiger_sentence.codes.txt
Fixture=tests/engine_cases/basic_real_code_commit.json
```
