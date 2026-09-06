# Third-party Reference Register

This register separates architectural study from dependencies shipped by
TigerClaw. A reference is not a source-code import authorization.

| Project / material | Role | License status | TigerClaw use |
| --- | --- | --- | --- |
| TigerClaw C# Core | Product behavior authority | repository code | executable behavior reference |
| \`reference/weasel/\` | Windows frontend boundary reference | GPL-3.0; local license file present | architecture/reference only; no source copied into macOS frontend |
| Rime/librime | session-oriented engine API reference | verify before any code import or link | architecture/API reference only |
| Rime Squirrel | InputMethodKit lifecycle and panel reference | GPL-3.0; verify at pinned upstream revision before any code import | architecture/reference only; no source copied |
| McBopomofo / OpenVanilla | macOS IME implementation reference | verify before any code import | engineering reference only |
| \`third_party/llama.cpp\` | optional Qwen runtime dependency | submodule is not initialized in this checkout | no macOS integration approved yet |

## Rules

1. Before adding, copying or linking third-party code, record the exact
   revision, license and distribution implications here.
2. GPL references remain design references unless the project explicitly
   chooses a GPL-compatible distribution strategy.
3. The eventual macOS implementation must cite the source file/reference used
   for a non-obvious platform decision, without reproducing its code.

