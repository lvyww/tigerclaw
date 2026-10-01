# Rime interaction and commit-evidence boundaries

## Changed behaviour

Raw caret offsets belong to `context.input` (bytes), not to a display string.
Only an actual end append may count toward automatic commit. Middle insertion
keeps the suffix and Rime's caret, clears pending append evidence/Tab confirmation,
and invalidates uncommitted locks intersecting the edited range. Locked Backspace
and Delete use the corresponding Rime edit operation. A lock for text already
committed to the application is never erased by editing the live tail.

`Menu:candidate_count()` is the materialized prefix, not the total. Tab prepares
the translator's bounded list (20), then highlights cyclically without selecting.

A runtime model-operation failure invalidates and closes the model, clears all
model-dependent decode/scoring caches, and retries the whole decode once under
the existing no-model policy. The already-processed key is not replayed. A model
generation prevents old confidence/pending-empty-code evidence from surviving
that downgrade. Non-model errors are not silently converted to success. Model
initialization failure closes its handle immediately; explicit model disable
also releases it. This does not implement a general rollback for arbitrary
errors from the Rime host after a text commit.

Display Top-K is independent from confidence: the scored beam is retained for
prefix evidence, same-input evidence upgrades, and strong empty-code acceptance.
A descendant carries any ancestor's truncation flag. Known-incomplete evidence
cannot authorize high-confidence automatic commit. The complete-path uniqueness
query remains in use. These changes may deliberately delay an automatic commit
that previously depended on missing dissent; manual Space selection, display
limit, ranking weights, beam sizes and code-table data are not changed.

This is not a proof that beam-normalized shares are calibrated real-world
probabilities. Further analysis of alternate segmentations/deduplicated path mass
and full-model behaviour remains separate.

## Running tests

```sh
python tools/run_regressions.py --lua lua5.4 --negative-control
# Also tested by CI with Lua 5.3, LuaJIT (no binary-model support assumed),
# and official Lua 5.4.8 built with MSVC for x64 and x86.
```

The runner makes an owned temporary copy of scripts and plain-text resources,
not a live Rime user directory. It propagates syntax, execution, timeout and
negative-control failures. Cleanup retries six times; exhausted OS errors are
reported separately and do not replace the functional result.

The original suite still runs. New contract tests use byte-offset caret edits,
lazy menu preparation and separate processor/translator environments. Safety
tests use independent hand-weighted candidate pools and fault injection into
production closures. `debug` access is restricted to tests; no test hooks or
extra threads are introduced into the production module. Negative controls
must fail at named functional assertions, not merely fail to load or compile.

A separate Linux job links real librime and loads its actual Lua plugin. It runs
the production schema and processor chain against an isolated small table and
minimal shared preset: middle insertion, end-append unique commit, Tab locks,
Backspace/Delete, multi-page cyclic highlights, decimal input and a test-only
model callback failure. It checks actual raw input, caret, commit text and
properties through the C API. This is a headless engine test, not desktop UI,
physical key routing or mobile keyboard acceptance.

```sh
g++ -std=c++17 tools/rime_api_probe.cpp -lrime -ldl -o /tmp/rime-api-probe
python tools/rime_integration_test.py --exe /tmp/rime-api-probe --plugin /path/to/librime-lua.so
```

The full statistical model is not downloaded or published by these jobs.
`tools/test_tiger_sentence_incremental.lua . --require-model` remains a separate
model-equipped acceptance run. Its golden early-commit timings must be reviewed
against complete evidence rather than weakened solely to get a green result.

Deferred: lock-path incremental caching, committed-history checkpoints, broad
module splitting, resource-release publication, stricter benchmark mode flags,
custom code lengths beyond the existing incremental assumptions, and real
Weasel/iOS frontend acceptance. No performance gain is claimed by this PR.

## Same-row key correction experiment (2026-10-01)

The persistent option `tiger_sentence_key_correction` defaults to false and has
no schema `reset`. Only immediate neighbors in `qwertyuiop`, `asdfghjkl` and
`zxcvbnm` are supported. Each unconfirmed suffix needs at least four letters and
two output Han characters, and may contain at most two substitutions. Explicit
rank-selected edges and confirmed prefixes remain fixed. Raw input and caret
positions are never rewritten.

Exact decoding retains its original beam and candidate order. A separate lattice
borrows exact prefix states and keeps at most 64 paths per edit budget through
position 24, then 24 per budget. This is approximate bounded search. The two edit
budgets are retained separately; changing the final penalty cannot change the
search survivors. Search always uses an offset of 8 per edit to preserve floating
point tie-breaking. Final ranking uses the calibrated penalty 8 and promotion
margin 2, with the existing lexical prior. Same-text exact candidates take
precedence. This is a fixed keyboard-error cost, not a calibrated probability.

The original v1 implementation reused append/backspace lattices and rebuilt
middle edits (see the performance revision below for current behavior). Cache
identity includes the locked prefix, required text and ranking parameters;
model/table/learning/option changes clear it. A corrected lock records the
corrected spelling in the existing length-framed boundary property, while the
original raw spelling stays authoritative. Its provenance survives a switch
back to correction-off. Corrections and corrected histories do not enter the
learning journal. The independent mirror's existing `2921b25` learned-fragment
reinforcement was retained and merged into the authoritative learning module.

Automatic early/empty-code commit is blocked when the visible first candidate
is corrected, including an older pending empty-code proposal. Manual Space,
tap, punctuation and Tab confirmation remain available. Exact first candidates
retain normal automatic behavior: an exact prefix can commit before enough
letters expose a correction, and already committed application text cannot be
retracted. Disable early commit too when the whole segment must wait for manual
confirmation. Model failure during correction clears the search scope and
retries the whole decode under the existing no-model policy.

### Calibration and evaluation contract

Model: unchanged three-source TCSKNM03 Q8, 405,663,171 bytes, SHA256
`756f6c92cf43ad6e8e3087ce66b711ac6ad0fc41e6f3fb82b3766e35ecab8681`.
The existing old10k / Articles 33,129 / THUCNews 30,000 inputs are partitioned by
SHA256 of dataset plus original ID (modulo 5). The development partition has
14,582 original rows; the holdout has 58,547. Each eligible row generates one
single-neighbor substitution and a nested second substitution at a different
position using the fixed seed `tiger-key-correction-v1`. Short/nonletter rows
remain in clean testing but are excluded from synthetic errors.

No learning, early commit or Qwen is used in this ranking evaluation. Twelve
LuaJIT workers run the real Lua decoder and reader. Every clean correction-off
candidate pool is compared with the before-edit source, including text, score
and segmentation. The heldout split controls parameter selection only; this is
not a claim of training-corpus independence, real-phone error distribution, or
deduplication across source datasets. Historical older frozen-Lua accuracy
tables must not be substituted for the current decoder's results.

The predeclared gate is at most 0.05% previously-correct clean rows made wrong.
Among passing penalties, maximize the mean one-/two-error Top1 rate, with larger
penalty breaking ties. Margin remains 2. Calibration results:

| Penalty | Clean correct made wrong / 14,525 | One-error Top1 / 14,572 | Two-error Top1 / 14,572 | Gate |
|---:|---:|---:|---:|:---|
| 4 | 49 | 12,593 | 12,248 | fail |
| 6 | 12 | 11,352 | 10,317 | fail |
| **8** | **0** | **9,677** | **7,678** | **selected** |
| 10 | 0 | 7,920 | 5,079 | pass |
| 12 | 0 | 6,244 | 3,049 | pass |
| 16 | 0 | 3,499 | 851 | pass |

Holdout results are pending; no parameter changes are permitted based on them.

### Original v1 validation and performance limits

Lua 5.4 and LuaJIT regressions cover topology/budgets, no-model fallback,
incremental/full parity, selectors, toggles, corrected locks, score-grid parity
and model failure inside correction. Real librime probes cover persistent
defaults/restarts/multiple sessions, composition refresh, manual commits,
automatic-commit guards and no learning from corrected text. Existing preedit,
learning and API regressions also run. These are isolated engine tests; no daily
Rime installation is changed and no phone touchscreen has been tested.

`test_key_correction_random.lua` compares incremental/full results after 35
fixed-seed append, backspace, insert, delete, replacement and toggle operations
at 8/16/32/64/128-key lengths (maximum 129). `test_key_correction_snapshot.lua`
compares the final runtime with the frozen calibration implementation after
frontend-only fixes and the existing mirror learning change; learning is off.

`bench_key_correction.lua` uses compact mode, three repeats of a fixed stress
string, fresh processes per mode, and reports process CPU time, not UI latency.
Workers for accuracy evaluation were also active; this is not an idle-machine
or mobile benchmark. Representative LuaJIT P95 CPU milliseconds:

| Keys | Off append | On append | Off backspace | On backspace | Off arbitrary edit | On arbitrary edit |
|---:|---:|---:|---:|---:|---:|---:|
| 8 | 1.52 | 12.20 | 0.22 | 3.59 | 1.69 | 23.54 |
| 16 | 1.59 | 28.74 | 0.55 | 6.76 | 6.02 | 147.51 |
| 32 | 3.20 | 46.37 | 0.98 | 14.76 | 28.17 | 481.92 |
| 64 | 4.36 | 48.70 | 2.71 | 14.03 | 75.73 | 1,178.73 |
| 128 | 4.27 | 49.77 | 3.62 | 12.81 | 116.86 | 2,670.50 |

Lua 5.4 is slower here: 128-key arbitrary-edit P95 is 11,730.99 ms. Maximum
observed Lua heap at that length is approximately 92.44 MiB with LuaJIT and
73.96 MiB with Lua 5.4; these figures exclude native allocations/model mappings
and are not total process memory. Long middle edits remain unsuitable for a
claim of smooth mobile input. The package remains experimental and default-off.

### Performance revision, 2026-10-01 (provisional test package)

This revision addresses continuous typing on Xiaomi 13 Ultra. The frontend name
and version are still unknown. The switch remains off by default; the model,
penalty 8 and promotion margin 2 are unchanged. No installation is modified.

The fivegram reader caches full scalar step scores and context locations, with
model-local bounds, trim/close invalidation and unchanged float operation order.
Context metadata contains offsets rather than references to evicted page strings.
FIFO eviction removes a quarter of the entries in a batch: single-entry eviction
at a power-of-two capacity caused repeated Lua hash-table rebuilds during testing.
Compact mode retains at most 8,192 scores and 4,096 context locations. A separate
2,048-entry variant cache is invalidated by code-table identity.

The processor captures prior-input automatic-commit evidence before push_input;
translation and subsequent commit checks reuse the current generation instead of
bouncing between old/new raw input. Letter-only middle edits reuse a safe unchanged
prefix, rewinding by the maximum code length. Selector/separator edits conservatively
rebuild. An exhausted lattice is not reused for a different input.

Only correction search is narrowed; exact search is unchanged. Both error budgets
are always attempted, each receiving half the scoring quota. Terminal EOS scores
also consume quota. Beams halve beyond position 24. Cached queries cannot renew
the same generation's quota. The three internal experiment profiles are:

| Profile | Exact seeds | One-error beam | Two-error beam | Total scoring-call quota |
|:--|--:|--:|--:|--:|
| A (provisional package) | 8 | 16 | 8 | 4,096 |
| B | 4 | 8 | 4 | 2,048 |
| C | 2 | 4 | 2 | 1,024 |

These are offline controls, not new user options. The quota bounds correction
LM step calls, not elapsed time or total exact-search/lexical-prior work. Only
completed whole-input correction paths may appear. At most two distinct corrected
candidates survive, within 4 score units of the best exact/corrected candidate.
Incomplete searches retain the language model and explicit confirmation but block
both automatic early and empty-code commits, even when the exact candidate leads.

Final validation: 6,144 exact reader score/state comparisons, 1,440 candidate
comparisons over 72 inputs in the unlimited reference profile, 2,369 bounded-search
checks on each of Lua 5.4 and LuaJIT, and 21 fixed-seed editing comparisons up to
33 keys pass. Existing isolated Lua regressions pass. Actual librime tests pass
for toggles, locks, explicit commits, no learning from corrections, and forced
quota exhaustion with automatic application/preedit commits blocked and manual
Space confirmation retained. These tests do not restart the paused holdout job.

Short development-only probe: 24 existing development originals (8 per source,
raw length at most 16), each with clean/one-error/two-error input. V1 and all three
profiles score 24/24, 19/24 and 14/24 Top1 respectively. A/B/C exhaust quotas on
0/4/9 of 72 queries. V1/A/B/C cold-lattice P95 CPU times are respectively
73.650/14.507/13.891/8.977 ms. This tiny sample neither establishes accuracy gates
nor selects a final production profile; A remains provisional.

Final Linux CPU benchmark, compact mode, three repeats per length:

| Runtime | Keys | Off append P95 ms | A append P95 ms | A edit P95 ms |
|:--|--:|--:|--:|--:|
| Lua 5.4 | 8 | 0.2630 | 4.724 | 7.512 |
| Lua 5.4 | 16 | 0.7230 | 2.967 | 18.285 |
| Lua 5.4 | 32 | 1.2640 | 8.772 | 42.123 |
| LuaJIT | 8 | 0.5150 | 2.921 | 7.493 |
| LuaJIT | 16 | 0.3800 | 1.911 | 18.506 |
| LuaJIT | 32 | 0.5870 | 5.720 | 22.447 |

Actual isolated librime processing of `kispfidyiejryfenahbmspkispfidyiejr` (34 keys),
early commit enabled, gives warm P95 12.028 ms with A versus 5.415 ms off (68 warm
key samples per mode). First cold keys are 50.296/55.927 ms respectively and are
excluded from warm P95. At most one correction search runs per key. This is Linux
API processing, not phone/UI end-to-end latency; cold model startup is separate.

Full development/holdout accuracy and Xiaomi acceptance remain pending. When the
user authorizes resuming evaluation, enforce clean regression <=0.05%, one-error
Top1 loss <=2 percentage points and two-error loss <=5 points versus V1, alongside
phone warm P95 <=50 ms and added latency <=10 ms. Choose the best recall eligible
profile, breaking ties by compute. Do not tune on the holdout. The old suspended
finalizer is obsolete after this source revision and must not be resumed blindly.
Evidence and the verified performance package are in
`next/_run/RimeKeyCorrectionPerf-20261001/` in the TigerClaw repository.
