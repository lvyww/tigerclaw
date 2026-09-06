# macOS .NET Hybrid Baseline

Frozen on 2026-08-27 for the macOS port.

## Product-reference revision

| Item | Value |
| --- | --- |
| C# behavior reference | \`59062a6f0142b50d54ec13b0bd30cfdcad0e2c86\` |
| Commit subject | \`perf: reduce sentence decoder allocations\` |
| Working tree at capture | dirty; see "Working-tree caveat" below |
| Current product authority | \`next/TigerClaw.Core/\` |
| macOS architecture | Swift/InputMethodKit host + C# NativeAOT engine |
| Rust status | separate replacement candidate; not required for macOS 1.0 |

Fixtures in \`tests/engine_cases/\` must record their source C# test and baseline
revision. A later behavior change requires a deliberate baseline update; it must
not silently rewrite expected output.

## Tracked resource manifest

SHA-256 values below are for the exact files available in this checkout.

| Resource | SHA-256 |
| --- | --- |
| \`config.txt\` | \`c68a5a3d98a1cd01427ea89855dac6b22f434f131029d9b69e6736d79c0aba13\` |
| \`next/TigerClaw.Core/Data/sentence_common_chars_1500.txt\` | \`e3ccaadda8e856213aac2f5418d4f8cd9f5a206a5593bc3756dc96b187c5b74d\` |
| \`next/TigerClaw.Core/Data/sentence_char_ranks.txt\` | \`a8be232135cb33ac85f36475d89705f381419b424c3f83422a0e1f0d72aeb101\` |
| \`rime/tiger_sentence/tiger_sentence.codes.txt\` | \`1d3e9b0ce0e4a603be3f220c71acecad846f020e87a52723ecb3814f6b53ac0e\` |
| \`rime/tiger_sentence/tiger_sentence.schema.yaml\` | \`4ea4283670b9bb3ce8547bba9406b3cf0562d2c18abe74b3b580d1cec277e832\` |
| \`rime/tiger_sentence/tiger_sentence.supplement.txt\` | \`d21a1c3a23f839abbfca9be1622c81afcf8a2555a3db892c7d306118a7d0ea78\` |

The production \`sentence-ngram-v2.bin\` and Qwen GGUF are intentionally outside
Git and were absent from this checkout. Their hashes are **unknown**; before
model-backed sentence fixtures are added, record their source path, version and
SHA-256 in this table.

The \`third_party/llama.cpp\` submodule is not initialized in this checkout. Its
recorded gitlink is \`9b05354ec6fb58b4e665e9a39ebc40285c015638\`.

## Working-tree caveat

This baseline intentionally records the checked-out commit plus the current
dirty workspace, because the macOS implementation work started before these
files were committed. At capture time the non-Rust macOS/.NET Hybrid additions
were untracked under:

- \`docs/\`
- \`macos/\`
- \`spikes/\`
- \`tests/engine_cases/\`

The Rust candidate tree also contained unrelated modified/untracked files under
\`rust/TigerClaw.Core.Rust/\`. Those files are excluded from this macOS .NET
Hybrid baseline and must not be treated as macOS parity evidence.

## Toolchain observation

This is an environment observation, not a successful build result.

| Requirement | Observed status |
| --- | --- |
| Swift / Clang | available on arm64 macOS through Xcode |
| Full Xcode / \`xcodebuild\` | available: Xcode 26.6, build 17F113 |
| .NET SDK | available at \`/Users/wuzz/.dotnet/dotnet\`: SDK 10.0.400, runtime 10.0.11, RID \`osx-arm64\` |
| .NET macOS workload | installed |
| Rust | not part of the macOS .NET Hybrid acceptance path |

The Phase 0 baseline metadata and fixture scaffolding are prepared. This does
not mean the eventual fixture set is complete or that any golden fixture runner
has broad coverage. The first Phase 1 runner now executes
`tests/engine_cases/basic_real_code_commit.json` only.

## Proven spike evidence

The isolated Hybrid spike in \`spikes/swift-dotnet-bridge/\` established the
macOS direction:

- command-line Swift can call a C# NativeAOT C ABI library;
- an InputMethodKit host bundle can route a physical key through Swift into the
  C# NativeAOT engine;
- TextEdit received marked text for \`a\` and a commit on Space in the manual
  integration test.

This proves the bridge shape, not full TigerClaw engine parity.

## Baseline evidence

- \`next/TigerClaw.Core.Tests/Program.cs\` is the executable C# behavior suite.
- It covers mixed-input surface/raw separation, sentence candidate behavior,
  selection symbols, incremental decoding, async-result handling and replay
  semantics.
- The initial portable cases use either inline deterministic data or a
  hash-pinned repository lexicon. Sentence cases remain non-executable until
  their runtime model artifacts are available and their hashes are recorded.
