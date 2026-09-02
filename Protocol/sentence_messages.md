# TigerClaw.Sentence IPC

`TigerClaw.Sentence.exe` is an optional native C++ Qwen reranking sidecar. Core
remains the owner of composition state, lexicon lookup, lattice generation,
n-gram scoring, candidate selection, and commit behavior. The sidecar statically
links its llama.cpp scorer and has no managed-runtime or companion-DLL dependency.

## Transport

- Named pipe: `\\.\pipe\TigerClaw.Sentence.v1`
- Encoding: UTF-8 without BOM
- Framing: one JSON object per line
- Direction: Core is the client; Sentence is the server
- Lifetime: Core starts Sentence lazily and passes `--parent-pid`; Sentence exits
  when that parent exits

## Requests

Health check:

```json
{"type":"hello","seq":1}
```

Rerank the first 1 to 5 n-gram candidates:

```json
{
  "type":"rerank",
  "seq":2,
  "generation":18,
  "raw_code":"otj2",
  "candidates":["是什么","是人么"]
}
```

Sentence returns neural log-probabilities in the same order:

```json
{
  "type":"response",
  "seq":2,
  "generation":18,
  "raw_code":"otj2",
  "success":true,
  "provider":"llama.cpp-cpu-q8",
  "scores":[-8.25,-13.7]
}
```

Core accepts a response only when `generation`, `raw_code`, and score count all
still match the active composition. It combines scores as
`ngram_score + 0.84 * qwen_total_log_probability`. Only the first five candidates
are reordered; later n-gram candidates retain their original order. Missing
executable/model, connection errors, timeouts, crashes, and stale responses leave
the n-gram order unchanged.

The optional shutdown request is `{"type":"shutdown","seq":3}`.
