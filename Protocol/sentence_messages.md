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
- Lifetime: Core preloads Sentence in the background when the current schema
  activates sentence input and neural reranking is enabled. Disabling either
  condition cancels pending requests and releases the process started by Core
  (shutdown first, forced exit if necessary). Re-enabling reloads after cleanup.
  There is no idle release. Core passes `--parent-pid`; Sentence also exits when
  that parent exits. Shutdown validates the pipe server PID against the owned
  process; an independently started process is never terminated by name.

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
still match the active composition and sentence/neural settings remain enabled.
For a pre-Qwen winner of 2..6 text elements, Core uses
`(1-alpha)*ngram_score + alpha*qwen_total_log_probability`, with each candidate's
own length selecting alpha (2: 0.15; 3..6: 0.30; other lengths: the original
lambda normalized as `lambda/(1+lambda)`). Outside that winner-length range,
the original additive policy remains (lambda 0.30 for a one-character base
winner, otherwise 0.84). Only the first five candidates
are reordered; later n-gram candidates retain their original order. Missing
executable/model, connection errors, timeouts, crashes, and stale responses leave
the n-gram order unchanged.

The optional shutdown request is `{"type":"shutdown","seq":3}`.
