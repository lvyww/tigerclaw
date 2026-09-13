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
