# Rime performance checks — 2026-09-14

Scope: Lua Rime pack only, after the locked-prefix incremental-cache fix.
No Windows Core/Fcitx5 ranking changes, live deployment or database migration.

## Changes and invariants

- Learning keeps per-code aggregate partitions. Confirmations copy the map and
  affected partitions/groups, preserving old snapshots; new code insertion keeps
  the sorted code list. Durable writes precede publication, including partial
  batch failure. Minute refresh creates a score epoch in constant time; only
  queried code partitions materialize exact/general/prefix scores. No background
  writer or changed score formula. Future timestamps/clock rollback require a
  replay fallback. `learning.build` is intentionally retained as a test oracle.
- Startup omits duplicate scans already guaranteed by code parsing, compares
  dense character ranks directly and sorts distinct code lengths. Hashing reads
  four bytes per call with the exact old arithmetic on each supported Lua.
  No compiled data cache, new model or changed editable TXT contract.
- Confidence reuses aggregate records, retains character counts and only copies
  pooled candidates on a mass collision. Full Beam probability mass, dropped-tail
  evidence and ancestor truncation remain unchanged.
- Repeated lock-property reads reuse parsed immutable entries, with separate
  mutable lists. Same-generation display refreshes reuse the trimmed raw suffix
  but always read current buffered text. Schema-name hashes are environment-cached.
- The existing 8 MiB model-page LRU uses numeric per-section page keys, avoiding
  string construction on each hit. No cache-size increase or GC-mode change.

## Measurements

WSL ARM64, Lua 5.4, mobile model MD5 `77b1d38760fd5efcbdbedf9289c4d6d1`.
These are CPU/isolated host observations, not Weasel/Trime/mobile UI acceptance.
Baseline is the working source immediately before this optimization (including
the preceding preedit fixes), not the older Git HEAD.

Learning benchmark: median of 10 runs, fixture creation outside timing, forced GC
before each run, in-memory durable-write substitute. Each confirmation adds one
record; the large fixture begins with 9,999 records and remains within the cap.

| History | Operation | Before (ms) | After (ms) |
| --- | --- | ---: | ---: |
| 9,999 records / 100 codes | Confirm | 15.972 | 0.016 |
| 9,999 distinct codes | Confirm a new code | 72.920 | 0.740 |
| 9,999 distinct codes | Minute refresh | 71.879 | 0.004 |
| 9,999 distinct codes | Refresh + prefix query | 68.942 | 0.210 |

The first query includes lazy materialization of matching code partitions;
the refresh improvement is not just moving the full replay into that query.
Real LevelDb writes and file-system latency remain synchronous and unmeasured
by this benchmark. Very large numbers of competitors under one code still cost
more than a sparse partition. The top-level snapshot map copy is O(code count).

Startup: 10 fresh processes per version, alternating, both source/data snapshots
on the same `/tmp` filesystem, OS page cache not flushed. Median lexicon setup
65.55 → 40.57 ms; module load 1.71 → 1.65 ms; model setup 5.63 → 4.96 ms.
This is not a physical cold-storage measurement; only lexicon/hash work changed.

Decoder benchmark: 20 repeats, 100 deterministic random 40-key burst samples.
For the 34-key sentence `他把那本书放在桌子上面然后离开了`, cumulative
frontend-cache workload 5.18 → 4.86 ms; evidence heap-growth metric
3537.8 → 2903.6 KiB (about 18% less). The heap metric is end-minus-start live
Lua memory after automatic GC, not a total allocation or pause measurement.
The random burst mean was 11.88 → 12.05 ms per sample and maximum single-key
time 10.91 → 13.70 ms; these runs do not demonstrate improved worst-case latency.
Do not report a general speed multiplier or claim all intermittent stalls fixed.

## Reproduction and regression

```bash
lua tools/bench_rime_learning.lua rime/tiger_sentence/lua 10
lua tools/bench_tiger_sentence_lua.lua . --mode mobile --repeat 20 --burst-cases 100 --require-model
python3 tools/run_regressions.py --lua lua --negative-control
# Repeat the suite with Lua 5.5 and LuaJIT (LuaJIT tests the no-model fallback).
```

The learning regression compares runtime exact/prefix scores against full replay
through corrections, competing choices, time decay, caps, multiple contexts/modes,
old snapshots, clock changes and partial/failed writes. It also rejects a hot-path
replay on a 10,000-record fixture and checks old/new hashes on all byte values.
An additional before/after run compared 8,072 append/backspace generations from
six fixed inputs and 100 deterministic random 40-key inputs with the real model;
candidate order/scores, segmentation and confidence matched the pre-change source.
Real librime tests cover preedit text and menu state, boundary deletion, Unicode,
punctuation, mode/schema changes, taps/Tab, deferred learning and restart persistence:

```bash
python3 tools/test_rime_preedit_integration.py --exe /tmp/rime-preedit-probe --plugin /usr/lib64/rime-plugins/librime-lua.so
python3 tools/test_rime_preedit_integration.py --exe /tmp/rime-preedit-probe --plugin /usr/lib64/rime-plugins/librime-lua.so --production-model /path/to/sentence-ngram-mobile.bin
python3 tools/test_rime_learning_integration.py --exe /tmp/rime-learning-probe --plugin /usr/lib64/rime-plugins/librime-lua.so --model /path/to/sentence-ngram-mobile.bin
```

Compile the probes from the corresponding `tools/*.cpp` with `g++ -std=c++17
-lrime -ldl`. These commands create owned temporary user directories, never use
the installed input method's data. See the preedit runner's `--benchmark-raw` and
`--benchmark-mode` options for per-key real-host measurements.
