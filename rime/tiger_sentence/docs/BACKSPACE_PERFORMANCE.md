# Locked / buffered Backspace cache reuse

Base: `c4ab19aa0d3ca4a18aeec4c83308547055f84b04` (merged #8 and #9).
This is independent of the unmerged memory PR #10 (`1d44d7f`). No merge is performed.

## Change and safety boundary

The processor used to call `invalidate_edit_state` and clear both decoder caches
for every deletion with a lock or buffered text. A suffix Backspace then replayed
the locked context and expanded the whole remaining suffix, even though the
locked decoder already supports shortening an existing lattice.

`invalidate_edit_state` now preserves that lattice only for deletion of exactly
one trailing ASCII encoding letter. It verifies the full old raw input, active
lock identity and content/boundaries, duplicate setting, and matching cached
states. Edit evidence and pending learning are still cleared by the existing
processor path. End candidates, EOS adjustments and evidence are still computed
by the normal decoder; the old displayed result is not reused as the new menu.

Conservative rebuilding remains for:
- Middle edits, deleted selectors (`;`, `'`, digits), removed/changed/nested locks,
  missing or mismatched cache generations, and changed scoring configuration.
- Learning-affected generations. Their cumulative inhibition flag may refer to
  a generated, even off-menu edge in the removed tail; simply preserving it
  would change automatic-commit behavior. An empty or unrelated learning history
  does not disable the optimization.

With an empty live suffix and a lock exactly matching the committed context,
the translator emits the single empty-suffix buffered candidate directly. This
avoids replaying a long locked history only to display the current buffer.
Unicode deletion, subsequent typing and final submission remain in the existing
processor/host paths. No model probabilities, weights, Beam widths, candidate
limits, confidence pools, thresholds or data files are changed. No per-key
snapshot cache is added; this retains only the already allocated active lattice.

## Tests and reproduction

`tools/test_backspace.lua` drives the production processor and synchronous
translator using separate environments. Tests count actual `new_states` calls
and expansion ranges via test-only closure instrumentation. Production code
contains no instrumentation or benchmark switches.

It compares visible text/preedit/quality, full candidates/scores/path boundaries
and early evidence with a separately loaded decoder reset before each oracle
query. `TIGER_BACKSPACE_ORACLE=/path/to/base/lua/tiger_sentence.lua` can also run
that oracle from the pre-change source (helpers are unchanged by this PR).

Coverage: continuous long suffix deletion, terminal Delete, refresh, long codes,
mid-string insertion/deletion (existing suite), selectors, unlocking nested locks,
missing/stale generations, actual learned-tail inhibition, staged-learning
cancellation, Unicode text-only deletion/resumption and model-operation failure.
The existing native librime preedit probe additionally types an 82-key live
suffix behind a buffered prefix, deletes 24 keys, resumes input and verifies the
final complete submission. There are no timing thresholds in functional tests.

Local Lua 5.4 results:
- 2,443 Backspace checks without a model; 2,425 with the 17,480-byte synthetic
  TCSKNM02 model and pre-change oracle.
- All existing regression groups and 16 functional negative controls pass.
- Independent old/new decoder serialization: 2,008 synthetic-model snapshots,
  exactly equal (including floating-point scores).
- PR #10 three-way overlay is conflict-free; its whole suite and 18 negative
  controls pass. Compact + synthetic model: 3,343 checks, including active-lattice
  cache trimming between deletions.

```sh
python3 tools/run_regressions.py --lua lua5.4 --negative-control
lua5.4 tools/test_backspace.lua /path/to/isolated/scheme
# Requires a model in that isolated root; fails rather than silently falling back:
lua5.4 tools/test_backspace.lua /path/to/isolated/scheme --require-model
# PR10 overlay only:
lua5.4 tools/test_backspace.lua /path/to/isolated/scheme --require-model --compact
```

## Diagnostic CPU benchmark (not iOS latency)

`tools/test_backspace.lua ROOT --benchmark [--require-model]` uses a small synthetic
ambiguous code table and supplement. The optional model run here uses the same
17,480-byte synthetic model, NOT the user's production ngram. Installed Linux
x86_64 Lua 5.4 shared library, hosted by a minimal local C launcher. For each
case, 30 deletion operations are timed after one warm-up; setup and explicit
collection are outside the timed region. Regular GC remains enabled. The old
and new source use the exact same probe and settings. These are single-process
sample means, not five-run medians or physical keyboard/UI wall-clock latency.

| Synthetic model; CPU ms / Backspace | Base | Fixed |
| --- | ---: | ---: |
| 80-code locked input | 10.248 | 0.829 |
| 128-code locked input | 17.627 | 1.202 |
| Buffered prefix + 80-code total input | 9.634 | 0.867 |
| Buffered prefix + 128-code total input | 17.312 | 1.179 |
| 128-character text-only buffer | 0.220 | 0.055 |

The deterministic result is more important than these environment-dependent
numbers: a valid trailing deletion no longer constructs a new lattice or
re-expands the long suffix. CPU/UI latency can still come from final scoring,
rendering, GC, model cache misses or the conservative cases listed above.

The current local container did not expose the production model bytes after
materialization, so this round does NOT claim production-model timing/parity
or iOS physical-footprint/keyboard acceptance. CI results must be checked at
the final PR head; prior green runs are not substitutes.
