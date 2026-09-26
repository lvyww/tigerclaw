# TigerClaw Agent Handoff

This is the single project handoff entry. Read it before older notes. Detailed
protocol, user, porting and model documents are indexed in `docs/README.md`.

Last reorganized: 2026-08-28.

## Full-pinyin main repository (2026-09-22)

Full-pinyin development has moved to https://github.com/lvyww/claw_pinyin .
Use `C:\Users\yc\Desktop\claw_pinyin` (WSL: `/mnt/c/Users/yc/Desktop/claw_pinyin`)
for subsequent full-pinyin changes, tests and releases. This TigerClaw worktree and
its origin remain preserved for the shared/shape project. Read the new repository
AGENTS.md first for pinyin work. The installed desktop r3 runtime is unchanged.

## Current Status

TigerClaw is a Windows input method with a split-process runtime. The C# Core in
`next/TigerClaw.Core/` is the sole maintained implementation.

The experimental Rust Core line was stopped and removed on 2026-08-28. Its useful
source and handoff notes are stored outside this repository in
`TigerClaw-RustCore-Source-Archive-20260828.7z`. Do not restore or resume that line
unless the user explicitly requests it.

```text
Windows TSF
  -> BimeTSF2/SampleIME/TigerClaw.dll
  -> \\.\pipe\BimeIPC
  -> next/TigerClaw.Core.exe
       -> optional \\.\pipe\TigerClaw.Sentence.v1
       -> next/TigerClaw.Sentence.exe
  -> UI state MMF / heartbeat
  -> next/TigerClaw.Overlay.exe
  -> next/TigerClaw.Dialog.exe
```

Active components:

- `next/TigerClaw.Pinyin/` and `next/TigerClaw.Pinyin.Native/`: Windows full-pinyin
  internal build (2026-09-21), integrated into the maintained C# Core through
  explicit `schema.json` engine metadata. Joint-token trigram/Beam 200, no LLM;
  prefix selection, raw editing, user words and TSF receipt-based learning.
  Default dictionaries changed by explicit user request on 2026-09-22 to Wanxiang
  Base v18.0.8 (2,202,473 entries), with source-aligned joint tokens; this overrides
  older notes requesting the original 65k dictionary. Model files remain unchanged.
  Source schemas and packaging default to wanxiang-pinyin.txt/wanxiang-tokens.json;
  --dictionary original retains the frozen small-lexicon packaging path. Desktop
  schemas are updated, with original resources and a pre-Wanxiang backup retained.
  See docs/FULL_PINYIN.md for provenance, cold-load cost and validation limits.
  The normal menu (2026-09-22) shows 1 sentence when retained-pool score mass
  reaches 0.90, otherwise 2, then independent lexicon prefixes by descending
  character length and entry frequency. This confidence is UI-only, not calibrated
  correctness. Preserve exact-spelling/raw-boundary and auxiliary constraints,
  explicit pins/phrases and the F2 whole-sentence menu. Prefix cuts must leave
  a legal syllable suffix; do not cut chang into cha/ng or chan/g. English
  requires an exact match or completion of the entire live input (wind -> Windows),
  never a shorter entry matching only its start (Windowsma must not offer Windows).
  The r3 expansion adds mixed full/initial spelling, syllable aliases, optional
  transposition correction and individual fuzzy rules, fixed phrases/pins/forget,
  English/mixed words, literal URLs/email, tools, syllable editing, Tiger-code
  auxiliary filtering, traditional output and an independent Xiaohe schema.
  Full spelling wins over abbreviation search; bare consonant interjections
  remain exact in sentences with at least two regular full syllables. Auxiliary
  constraints enter search before Beam. Typo/fuzzy/traditional default off.
  Preferences and user words stay schema-local; forgetting appends TCL1 undo
  records. Candidate actions use immutable menu tokens and existing TSF replay
  identities. Do not infer real-app acceptance from offline checks.
  Build/package outputs remain isolated in `next/_run/FullPinyin/`. On
  2026-09-21 the user uninstalled the former ARM64 installation and explicitly
  requested installing r3. The active registered Core and Overlay now run from
  `C:\Users\yc\Desktop\虎爪全拼内测-r3`; current scheme is `虎爪全拼`.
  ARM64X/ARM64/x64 and Win32 TSF registrations and installed DLL hashes were
  verified, as were live Core IPC and Overlay startup. The original
  `release_arm64/` files/settings were preserved; do not overwrite them as part
  of this test installation. Runtime evidence is in
  `next/_run/FullPinyin/install-r3/installed-runtime.json`. Read
  `docs/FULL_PINYIN.md` for frozen parity evidence, build commands and
  outstanding real-app acceptance; installation is not typing acceptance.
  On 2026-09-22 the desktop r3 runtime was upgraded at the user's request to
  original trigram Beam200 Top50 + full Q8 five-gram reranking (2,481.68 MB
  combined models, no LLM). Optional schema `rerank_model` enables this path;
  missing/failed reranking falls back to trigram. Preserve word bonuses,
  spelling costs, locked prefixes and receipt learning. Frozen 8019 rows /
  400950 candidates match the experiment exactly (4890 correct). Backup is
  `backup-before-fivegram-20260922-014718` inside the desktop runtime; evidence
  is `next/_run/FullPinyin/fivegram-20260922/`. Tiger shape-code five-gram
  feasibility was evaluated offline only; see
  `tools/FullPinyinEval/HigherOrder/TIGER_SHAPE.md`. Do not deploy it implicitly.

- Tiger shape-code and Rime mainlines use the three-source TCSKNM03 Q8 fivegram
  (2026-09-25, explicitly requested by the user):
  `Models/sentence-fivegram-mobile.bin`, 405,663,171 bytes, SHA256
  `756f6c92cf43ad6e8e3087ce66b711ac6ad0fc41e6f3fb82b3766e35ecab8681`.
  Corpus4 .50 / Articles .25 / Brightmart non-news .25; history-weighted KL
  pruning matches the previous mainline order-2..5 counts, with 20,799 unigrams.
  The 2026-09-24 356.49 MB model remains the frozen experiment baseline.
  `SentenceFivegramModel` maps this format directly in managed C#; no shape
  KenLM DLL, KLM reader or legacy trigram fallback remains. The same file supplies
  observed-bigram isolation priors. Missing/corrupt models use the existing
  no-model behavior. Query sessions lease the mapping through cancellation,
  incremental reuse and locked prefixes. Historical trigram fixture readers live
  only in Core.Tests, not the Core assembly. Full-pinyin KenLM is unrelated and
  unchanged. Debug/full builds stage only the verified Q8 model; release packing
  excludes stale KLM/trigram/native dependencies. Core-only publishing still does
  not replace models, so this migration needs matching Core and Q8 model.
  ARM64/x64 AOT isolated probes and Q8/decoder/lifecycle tests pass. Mainline
  source and artifacts are updated. On 2026-09-25, the user explicitly requested
  updating daily `release_arm64`: compatible ARM64 Core and the three-source Q8
  were installed and restarted. Live IPC and mapped model identity verified;
  config, code tables and other component hashes unchanged. Old Core/models
  backed up in release_arm64/backup-before-threeway-20260925-221723/.
  See docs/SHAPE_FIVEGRAM.md for results,
  source identity, packaging and real-app acceptance limits.

- Three-source Q8 promoted to TigerClaw/Rime mainline on 2026-09-25 by explicit
  user request. Shared runtime source, staging hash and Rime default-model.json
  use the 405,663,171-byte model above. Old 356.49 MB benchmark baseline is
  retained in runtime/backup-before-threeway-20260925/. Model-only source change;
  compatible Core probes pass on ARM64/x64, 56,344 C# checks / 2,376 snapshots
  and current Rime real-model regressions pass. Companion packages/evidence:
  C:\Archive\threeway-mainline-20260925. Daily ARM64 Core/model subsequently
  deployed by explicit user request; public releases remain unchanged. See docs/SHAPE_FIVEGRAM.md.

- Three-source original-model fusion completed (2026-09-25, user requested).
  Chose non-news by Corpus4-error rescue (168 vs news 136), including 38 shared
  Corpus4+Articles errors vs news 30. Same-set exploratory selection; not an
  independent generalization benchmark. Weights Corpus4 .50 / Articles .25 /
  non-news .25. Raw probability interpolation followed by union-support normalized
  sparse materialization, history-weighted single-deletion KL/prefix pruning,
  exact mainline order-2..5 budgets and Q8. No size ceiling; 20799 union unigrams.
  Old10k/Articles/THUC dynamic=9954/32955/29974,
  full=9952/32951/29971, pruned=9946/32910/29963,
  Q8=9945/32913/29964; Q8 405.66 MB, net +99 mainline.
  Independent toy/raw-record probability, mass/prefix/quantization and 4x3x666
  lifecycle/frozen fixture checks passed. Source records/indexes reused as
  read-only hardlinks. No production deployment. Work:
  /home/yc/tmp/corpus4-articles-third-20260925; verified archive:
  C:\Archive\corpus4-articles-third-20260925. See
  tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_THREEWAY.md.

- Brightmart news-only fivegram training completed (2026-09-25, user requested).
  Only new2016zh/news2016zh_train.json title/content; original source preserved,
  official valid and other domains excluded. Same cleaning/heldout/exact frozen
  target exclusion and MKN --prune 0 0 1 1 1 as the prior non-news experiment.
  2124481236 training Han tokens; 1309488 exact evaluation segments excluded.
  TCS Q8 2650.46 MB; old10k/Articles/THUC=9931/32771/29947, net-74 vs mainline.
  Counts 20213/5979128/47464406/126641011/178622419; heldout20k PPL35.252021, OOV1.
  Recovers 156/191 previous non-news regressions; not its overall net gain.
  Format/quantization,3x666 lifecycle and frozen input/fixture checks passed.
  Original ARPA/Q8/evidence archived C:\Archive\brightmart-news-20260925;
  work /home/yc/tmp/brightmart-news-20260925. Shared runner --news-only.
  No fusion/deployment. See tools/FullPinyinEval/HigherOrder/BRIGHTMART_NEWS.md.
  All jobs complete; historical tests are not independent weight selection.

- Brightmart non-news fivegram training completed (2026-09-25, user requested).
  Excluded new2016zh from the Brightmart input directory; preserved originals.
  Selected baike/webtext train + wiki:1276 files,6781170023 bytes; trained on
  1207652870 Han tokens. Existing Brightmart cleaning, MKN --prune 0 0 1 1 1;
  extra exact frozen-target exclusion497032 occurrences. No fuzzy/substring
  exclusion, and not a strict news-only ablation of the historical mainline.
  TCS Q8 1551.71 MB; old10k/Articles/THUC=9942/32794/29902, net-85 vs mainline.
  Counts 17627/6313914/39943905/84909197/92592712; heldout20k PPL43.084010, OOV0.
  Format/quantization,3x666 lifecycle and frozen-case/fixture checks passed.
  Original ARPA, final Q8 and evidence archived C:\Archive\brightmart-nonnews-20260925;
  work /home/yc/tmp/brightmart-nonnews-20260925. No fusion or deployment.
  See tools/FullPinyinEval/HigherOrder/BRIGHTMART_NONNEWS.md. Jobs complete.

- Full Corpus4 + Articles merge-before-pruning experiment completed (2026-09-25).
  Fixed alpha .10/.25, original ARPA float sources, normalized union vocabulary19070.
  Sparse backoff materialization then history-weighted single-deletion KL budget
  ranking with prefix protection; recompute backoff and convert Q8. Higher-order
  counts exactly7959327/69562625/10273459/8415769, same as mainline.
  alpha-0.10: 418.26 MB; old10k/Articles/THUC=9915/32882/29964, net +38 vs mainline.
  alpha-0.25: 415.31 MB; old10k/Articles/THUC=9919/32904/29964, net +64 vs mainline.
  Four stages evaluated separately: dynamic/full materialized/pruned float/Q8.
  Original ARPA probabilities and union-UNK splitting differ from prior dual-Q8
  experiments; differences cannot solely be attributed to pruning order. Fixed
  historical exploratory weights, not independently validated. No deployment.
  Archive C:\Archive\corpus4-articles-merge-budget-20260924; report
  tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_MERGE_BUDGET.md. All jobs complete.

- Corpus4-pruned + Articles interpolation completed (2026-09-24, user corrected
  primary baseline): 413.52 MB count-matched Corpus4 TCS Q8 + Articles TCS Q8.
  Fixed alpha .05/.10/.25; frozen Lua old10k/Articles/THUC correct counts:
  alpha-0.05=9921/32880/29969, net +139 vs pruned, +47 vs mainline; version-loss rescue 117/226.
  alpha-0.10=9920/32893/29966, net +148 vs pruned, +56 vs mainline; version-loss rescue 133/226.
  alpha-0.25=9917/32906/29962, net +154 vs pruned, +62 vs mainline; version-loss rescue 146/226.
  Endpoints121 full pools/scores byte-identical, five scalar weights x1431 tokens
  and endpoint/all9 x666 lifecycle checks pass; all73129 input/fixture identities match.
  Models total2.917 GB, no merged/compressed model or deployment. Historical
  exploratory weights, not independently validated. Full/pruned version loss also
  includes KenLM/TCS quantization differences; do not attribute all226 to pruning.
  Archive C:\Archive\corpus4-pruned-articles-mixture-20260924; report
  tools/FullPinyinEval/HigherOrder/CORPUS4_PRUNED_ARTICLES_MIXTURE.md. All jobs complete.

- Corpus4-full + Articles probability interpolation experiment completed
  (2026-09-24, user requested): fixed Articles alpha .05/.10/.25, each model
  performs its own backoff then probabilities are mixed per token. Frozen Lua
  old10k/Articles/THUC correct counts: .05=9926/32929/29968,
  .10=9926/32936/29967, .25=9927/32952/29968. Net over full Corpus4 +67/+73/+91;
  over current mainline +100/+106/+124 across 73,129 cases. .25 is best among
  these exploratory points, not independently tuned/validated or an optimum.
  Old10k still trails mainline; .25 finance -3 vs Corpus4. Existing two models
  total10.274 GB; no merged/compressed model or deployment. Offline adapter
  shape5_articles_mix.lua and optional evaluator arguments preserve normal path.
  Five scalar weights x1431 tokens match oracle; alpha0/1 x121 full pools/scores
  byte-identical; endpoint and all9 evaluations'666 lifecycle checks pass;
  input/fixture identities match. Archive `C:\Archive\corpus4-articles-mixture-20260924`;
  report `tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_MIXTURE.md`.
  All jobs complete; do not rerun historical sweeps to pick production weights.

- Articles/Corpus4 fusion feasibility steps 1–2 completed (2026-09-24): Articles
  now has matched frozen Lua scores 9795/10000, 32924/33129, 29666/30000, using
  same original 2.503 GB TCS Q8. Against full wsmerge, Articles-only correct
  14/168/21 (203 total), Corpus4-only 147/106/321 (574); both wrong170, target
  still in either candidate pool138. Against wsmerge413, Articles-only302,
  Corpus4-only548. Complement is concentrated in Articles source groups; all
  six THUCNews categories net favor Corpus4. Worth low-weight interpolation
  testing, not proof of fusion gains. No interpolation or weight tuning yet.
  Three 666-case regressions, 73,129 input identities and fixture hashes pass.
  Pause/resume honored; all jobs now completed. Archive
  `C:\Archive\articles-corpus4-complement-20260924`, report
  `tools/FullPinyinEval/HigherOrder/ARTICLES_CORPUS4_COMPLEMENT.md`.

- Mainline 345.99 MB size-pruning route retired and mohu v5 old10k completed
  (2026-09-24, user requested): deleted selected archive model plus trial-4/6/7/8
  ARPA/Q16/Q8 payloads, 17 files / 20.75 GB (Windows 0.346 GB, Linux 20.404 GB).
  Shared counts/corpus/full rebuilt models/tools and all evidence retained;
  566 retained identities and mainline/430 MB hashes verified. Do not rerun old
  size-pruning pipeline or delete its shared work directory. Cleanup audit:
  `C:\Archive\mainline-sizeprune-cleanup-20260924`.
  mohu v5 (573,052,280 bytes, SHA256 c2c148ea…dfee34) old10k: 9918/10000,
  Top5/20 9968; -41 versus mainline old10k and -45 across 73,129 cases. Twelve
  workers, same verified frozen fixture/model as previous Articles/THUCNews,
  666 lifecycle cases and input identity checks pass. No training/deployment.
  Archive `C:\Archive\mohu-v5-old10k-20260924`; global comparison updated.

- Model comparison now uses old 10k only (2026-09-24, user requested): replace
  historical20k column with `source=old` / old_1..old_10000; exclude added THUCNews
  10k from this column to avoid repeated weighting with THUCNews. Existing
  predictions filtered, no decoder rerun; all input identities match. Frozen
  mainline/size-pruned/count-matched/full correct: 9959/9953/9959/9963;
  wsmerge full/count-matched: 9928/9918. New 73,129-row combined deltas versus
  mainline: -50/+13/+69/+33/-92 respectively (excluding baseline).
  C# mainline/Articles old10k: 9959/9786; mohu subsequently verified at 9918.
  Global MODEL_COMPARISON.md updated; experiment reports retain original20k
  historical results. Audit: `C:\Archive\model-comparison-old10k-20260924`.

- Corpus4 488.68 MB count-pruned model retired (2026-09-24, user requested):
  deleted Windows/Linux model copies and dedicated ARPA, 3 files / 4.403 GB
  (Windows 0.489 GB, Linux filesystem 3.914 GB). Keep shared raw counts and all
  historical evaluations/tools/logs in wsmerge-count500-20260924; do not delete
  that directory or auto-rerun the retired pipeline. 2,733 retained file identities
  and new 413 MB model hashes verified. Old manifests describe historical files.
  New count-matched model and full wsmerge retained. Audit:
  `C:\Archive\corpus4-count500-cleanup-20260924`; MODEL_COMPARISON.md separates
  retained models from retired historical scores.

- Corpus4 wsmerge mainline-count matching completed (2026-09-24): actual-frequency
  thresholds `[0,1,7,256,278]`, independently nearest per-order counts and legal
  nondecreasing thresholds, no byte cap or accuracy tuning. Counts are
  18,673 / 8,379,906 / 67,621,519 / 10,276,050 / 8,412,543. Unlike prior wsmerge
  pruning, singleton bigrams are removed; seven whitespace-repair probes remain.
  TCS Q8 413,515,511 bytes, SHA256
  `2cd06f487fc38d47fb03bbbc535373b3cc331f420db256053845bc96a6d0a67f`.
  Frozen correct counts 19885/20000, 32758/33129, 29955/30000; +18/+16/+3
  versus prior wsmerge489 (+37 total), -44/-69/+18 versus mainline (-95 total).
  Reader, checkpoint bigram-pruning parity, three 666-case lifecycle checks and
  83,129-row comparison checks pass. Prior wsmerge baselines are KenLM Q8, so
  quantization path also differs. No deployment/interpolation. Archive
  `C:\Archive\char5-corpus4_0-wsmerge-20260923\count-matched-mainline`;
  report `tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_COUNT_MATCHED.md`.

- Mainline record-count matching experiment completed (2026-09-24): user removed
  byte budget and requested matching current per-order totals. Actual-frequency
  thresholds `[0,0,1,29,29]` preserve first three orders exactly; fourth/fifth
  counts 11,083,122 / 8,085,216 differ +7.88% / -3.93% from mainline. Independent
  nearest thresholds 31/28 violate KenLM's monotonic closure constraint; chosen
  legal pair minimizes squared relative count errors, before scoring.
  TCS Q8 is 430,168,074 bytes, SHA256
  `685d492e290a825fab3f199aa25485e282710abcf0b3a7415850140be23a401b`.
  Frozen correct counts 19927/20000, 32834/33129, 29943/30000: -2/+7/+6 versus
  mainline, +11 total; +66 versus prior 346 MB count pruning. This changes multiple
  orders/backoffs, not an isolated third-order causal test. No deployment or
  interpolation. Verified archive `C:\Archive\mainline-count-matched-20260924`;
  report `tools/FullPinyinEval/HigherOrder/MAINLINE_COUNT_MATCHED.md`.

- Mainline actual-frequency pruning completed (2026-09-24): `[0,0,7,14,28]`
  chosen by actual TCS Q8 bytes under current 356,492,204-byte budget produces
  345,992,115 bytes, SHA256
  `91f35f32674927ba4c16d075c00b2d25531531cecccf1efa9480dfbd6f99b3c1`.
  Frozen correct counts 19918/20000, 32795/33129, 29925/30000; -11/-32/-12
  vs current mainline, -55 total. Do not deploy this result; mainline unchanged.
  Compared with context128, it cuts third-order counts 69.56M→23.50M and raises
  fourth-order counts 10.27M→22.35M. No general claim that count pruning is worse.
  Original Brightmart preprocessing rebuilt with 12 workers: dedup index exactly
  matches. Original full KLM has 8 anomalous bytes in two packed records (word IDs
  exceed vocabulary); preserved for evidence. Stable rebuilt KLM is
  `/home/yc/tmp/mainline-count-prune-20260924/full-q8.klm`, SHA256
  `00252320a5cfac09906fe972c5e855d9ac56a3cc86ff1949200aeb37b91d376a`.
  Two builds match, all 83,129 predictions/ranks match original; future training
  should use reconstructed counts/full.arpa, not copy the anomalous bytes.
  Count checkpoint and corpus remain in that work directory. Tests/hash checks
  passed; archive `C:\Archive\mainline-count-pruned-20260924` and report
  `tools/FullPinyinEval/HigherOrder/MAINLINE_COUNT_PRUNING.md`. No interpolation.

- Mainline pre-context-pruning fivegram retested (2026-09-24): original
  Brightmart KenLM Q8, 2,032,382,402 bytes, SHA256
  `f13b5b6b61ef1440d3363510730b8fe122d727570cefbcc8a372bd42cadb1f70`,
  preserved at `/home/yc/tmp/tiger-shape-direct5/char5-q8.klm`. Original training
  singleton pruning remains; only later context128 pruning is absent.
  Frozen Lua correct counts: 19930/20000, 32878/33129 Articles, 29951/30000
  THUCNews (+1/+51/+14 vs current TCS Q8, +66 total). Historical predictions
  and target ranks exactly reproduce prior full-model results. Three 666-case
  lifecycle checks pass. KenLM/TCS quantization paths differ, so do not attribute
  all changes solely to pruning. No deployment; see
  `tools/FullPinyinEval/HigherOrder/MAINLINE_FULL5_ACCURACY.md` and archive
  `C:\Archive\mainline-full5-eval-20260924`.

- Retired corpus4 payload cleanup (2026-09-24, explicitly authorized): removed
  the pre-wsmerge corpus4 training/model payloads, derived pruning/fusion models,
  and wsmerge character-ranked pruning payloads/intermediates (41 files,
  210.72 GB total: Windows 137.54 GB, Linux 73.18 GB). Keep historical reports
  and shared frozen cases under the old archive; manifests there describe past
  experiments, not currently present model payloads. New wsmerge sources,
  count-pruned-500mb, and raw count checkpoint remain intact. Do not rerun retired
  routes implicitly. Audit: `C:\Archive\corpus4-retired-cleanup-20260924`.

- Actual-frequency wsmerge pruning completed (2026-09-24): 12 counting
  processes plus exact integer merges took 93.3 minutes; full corpus counts and
  original n-gram totals verified. Size-only thresholds [0,0,23,46,92] produce
  KenLM Q8 488,684,661 bytes, SHA256
  `6cacce36e4b8fc7914b14d34fded5bb2755b003cbbc5e1ac155a850d1c4fc71f`.
  Frozen Lua correct counts: 19867/20000, 32742/33129 Articles, 29952/30000
  THUCNews. Net +199 over the character-ranked 493 MB model, -169 versus full
  wsmerge; all bigrams and seven whitespace-fix probes retained. Windows hash
  verified. Archive: `C:\Archive\char5-corpus4_0-wsmerge-20260923\count-pruned-500mb`.
  See `tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_COUNT500.md`.
  Raw counts remain in `/home/yc/tmp/wsmerge-count500-20260924`; no need to
  recount. No TCSKNM03 conversion, interpolation or deployment; model-only budget
  excludes the frozen isolation-prior fixture.

- wsmerge 500 MB compression (2026-09-24): selected KenLM Q8 context thresholds
  [700,128,128] by size before evaluation; 493,072,710 bytes, SHA256
  `8a5316da7e8ef517a3e9354e5d22164999c5d84d4635ef7f491c107e7b3b3fe3`.
  Frozen Lua results: 19789/20000, 32686/33129 Articles, 29887/30000 THUCNews;
  losses versus full wsmerge are 113/176/79 correct rows. All bigrams and the
  seven whitespace-fix probes remain present. No interpolation or deployment.
  Archive: `C:\Archive\char5-corpus4_0-wsmerge-20260923\compressed-500mb`;
  see `tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_500MB.md`. This is the
  fivegram-only budget, excluding the frozen isolation-prior fixture.

- corpus4 wsmerge offline accuracy (2026-09-24): the retrained whitespace-merged
  KenLM Q8 was tested with the frozen Lua shape decoder (not current C#).
  Historical 20k: 19902 correct (unchanged from original corpus4); Articles:
  32862/33129 (-3); THUCNews: 29966/30000 (+15). Total net +12/83129.
  Same-environment mainline Q8 scores are 19929/32827/29937 respectively.
  No conversion, further compression, interpolation or deployment in this test.
  See `tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_ACCURACY.md` for paired
  changes, provenance, frozen/current decoder differences and overlap limits.

- Articles character-fivegram experiment (2026-09-24): trained independently from
  `C:\Archive\Copus\articles`, 324,209,793 training characters, no n-gram pruning.
  ARPA/Q16/Q8 are archived in `C:\Archive\articles-char5-20260924`; Q8 is
  2,503,378,280 bytes. Heldout and Python/Lua/LuaJIT/C# reader checks passed;
  repaired archive copies passed full SHA256 and Windows-native hash checks.
  Matched C# standalone accuracy is recorded in accuracy-csharp: Articles heldout,
  historical 20k and THUCNews 30k, with both models rerun using identical settings.
  Interpolation is deferred; default and installed models are unchanged.
  See `tools/FullPinyinEval/HigherOrder/ARTICLES_CHAR5.md` before the next experiment.

- Full-pinyin performance implementation (2026-09-22): compact source-order-preserving
  code/syllable indexes, streaming pooled tokens, physical-file-keyed shared resource
  bundles, model-local integer/batched context scoring and bounded query caches.
  Preserve exact Beam/candidate order, OOV source-token identity and float addition
  order; do not shrink Wanxiang, Beam 200 or five-gram Top50 to meet latency targets.
  Core uses one latest-request worker, transactional cancellation and off-lock menu
  preparation; confirmation joins the existing task without repeating search.
  Await initial schema-local learning replay before taking the generation snapshot.
  The new native batch ABI is optional: old jointkenlm.dll falls back to scalar
  queries, but full speed needs the updated DLL alongside Core. Shared resources
  have reference-counted ownership and query leases; user preferences/journals
  remain schema-local. See docs/PINYIN_PERFORMANCE_20260922.md and
  tools/FullPinyinEval/Performance/README.md for evidence and acceptance limits.
  Desktop r3 Core + jointkenlm.dll were updated at 14:08; backup is
  backup-before-pinyin-performance-20260922-140813. Configuration, journals,
  model files, TSF and daily Overlay were preserved. Frozen 8241 signatures
  match; 24 real-key RichEdit commits match the frozen first candidate.
  Hot compute P95 22.0..23.8 ms and load 6.4..6.5 s meet the compute targets,
  but observed first-frame P95 with current animation is 51.5..83.7 ms; the
  <=50 ms end-to-end target remains unmet. Do not claim all latency gates passed.

- Full-pinyin single-character learning uses independent mode/index
  `full-pinyin-character-v1` (2026-09-22). The schema TCL1 journal remains shared
  for receipt/undo compatibility; legacy `full-pinyin-v1` single-character events
  are classified into the character bucket on replay. Only standalone, whole-code,
  non-incomplete character selections learn; sentence prefix/tail characters do not.
  Character rewards/retention require an exact whole raw-code match and a complete
  single-character output, never a sentence prefix. Phrase learning is unchanged.
  Forget handles both old and new event identities. See `docs/FULL_PINYIN.md`.

- `next/TigerClaw.Core.Native/`: user-requested parallel C++ Core, **paused at the
  user's request on 2026-09-09 due to usage cost**. Do not automatically resume;
  wait for an explicit user request. The README's opening pause/resume section
  records the latest state, evidence and unfinished work. Compact tables,
  lexicon/config runtime, mixed/sentence decoding and shared event routing are
  implemented and tested in isolation. Main IPC/UI publishing and complete
  frontend hosting/acceptance are not done; it cannot replace the running Core.
  C# remains production default and behavioral authority. Build/output/commands
  are isolated; no production IPC or release deployment. Full completion gates
  and current evidence are in that directory's README; do not equate this initial
  data layer with completing the parallel Core.
- `BimeTSF2/SampleIME/`: bridge-only TSF DLL. It captures key, focus, caret and
  activation events, forwards them to Core, and applies Core responses.
- `next/TigerClaw.Core/`: configuration, lexicons, input state, candidates,
  sentence decoding, IPC and process lifecycle. It is a .NET 10 Native AOT
  executable; there is no maintained .NET Framework Core configuration.
- `next/TigerClaw.Overlay.Native/`: mainline C++ Win32/Direct2D/DirectWrite
  status/candidate UI and typing sounds, promoted at the user's request on
  2026-09-09. Release x64/ARM64 and debug builds default to this implementation.
  `next/build_overlay.bat` is the shared build entry; it does not deploy.
  `--demo` remains isolated from production IPC. See its README for validation.
  Candidate animation now follows monitor display-mode refresh rate (2026-09-24):
  QPC elapsed-time sampling, high-resolution one-shot waitable timer with ordinary
  fallback, one generation-tagged queued wake, two-second monitor cache and
  display/DPI invalidation. Preserve existing duration/easing, first-show/hold/
  residence behavior and final-publication-only placement history. No IPC/settings
  changes. ARM64/x64 isolated tests pass; hardware timing tested at 60 Hz only,
  other rates/cross-monitor changes simulated. See Native README and
  `next/_run/OverlayRefresh/` for measurements and Overlay-only deployment backup.
- `next/TigerClaw.Overlay/`: retained WPF fallback and display-parity reference.
  Set `TIGERCLAW_OVERLAY_BACKEND=wpf` before building to explicitly use it.
- `next/TigerClaw.Dialog/`: settings, add-word and selection-key UI.
- `next/TigerClaw.Shared/`: shared constants, build identity, MMF and guards.
- `next/TigerClaw.Sentence.Native/`: optional native C++ Qwen reranking sidecar,
  including the named-pipe host and statically linked llama.cpp scorer. Failure
  always falls back to n-gram order.
- `next/TigerClaw.Hook.Native/`: experimental native-hook frontend. Do not make
  it the default workflow unless explicitly requested.

`reference/` is upstream/reference-only. It must not be wired into active build
or publish scripts.

## Runtime Contracts

Main IPC:

- Pipe: `\\.\pipe\BimeIPC`
- Encoding/framing: UTF-8 JSON, one object per line
- Contract: `Protocol/messages.md`
- Handler: `next/TigerClaw.Core/ProtocolHandler.cs`

Sentence reranking:

- Pipe: `\\.\pipe\TigerClaw.Sentence.v1`
- Contract: `Protocol/sentence_messages.md`
- Client/server: `SentenceRerankClient.cs` /
  `TigerClaw.Sentence.Native/SentenceHost.cpp`

UI state:

- `Local\TigerClaw.UiState.v1`: Overlay state
- `Local\TigerClaw.Heartbeat.v1`: Core heartbeat
- `Local\TigerClaw.OverlayHeartbeat.v1`: Overlay heartbeat
- `Local\TigerClaw.ShowMenu.v1`: status-window menu trigger

Native Overlay's low-latency path adds `Local\TigerClaw.UiState.v1.Snapshot.v2`
with a `.Lock` mutex and `.Changed` event. Core retains v1 for WPF, then publishes
a mutex-protected complete snapshot and signals after unlock. Native reads it
without a second polling interval; old Core retains the legacy fallback. Never
block Core on a paused reader. Contract: `Protocol/ui_state.md`.

## Behavior That Must Stay Aligned

- Main and pinyin candidate tables use immutable `CompactLexicon` binary images
  with pooled UTF-16 text and insertion-order-preserving candidate ranges.
  Pinyin is runtime-owned, never schema-snapshot-owned: recent-schema switching
  shares it; full reload replaces it. User edits publish a new main-table image
  and preserve existing adjustment persistence. Ordinary/pinyin pagination reads
  only the requested range. TXT/YAML remain authoritative; no disk cache or idle
  unloading is introduced. See `docs/COMPACT_LEXICON.md` and compact lexicon tests.
- Raw code is authoritative. Mixed input keeps the complete raw composition,
  decodes it again after every edit, performs case-insensitive lookup, and
  preserves casing for display and literal commits. Code masking is display-only.
  After sentence auto commit, literal-code exits (Enter, CN/EN switching and
  CapsLock) emit only the uncommitted raw suffix, never the retained decoder context.
- Schema switching through shortcuts or `set_config` migrates only uncommitted
  raw code, clears old candidate preferences and sentence prefix constraints,
  and rebuilds for the target schema. Preserve raw casing across sentence mode.
  With mixed input disabled, short codes use ordinary composition; an imported
  code longer than maximum code length uses a temporary mixed composition.
- Sentence segmentation spaces are display-only. The raw code never contains
  them. Up/Down and Tab/Shift+Tab traverse visible sentence candidates; sentence
  mode leaves Ctrl+number to the target application.
- Windows, Rime and Fcitx5 sentence input: Tab/Shift+Tab highlights without submitting. The next
  code letter locks that candidate's text and consumed raw boundary, retaining
  language-model context and preventing later resegmentation across the boundary.
  With early commit enabled, this manual confirmation immediately submits only
  the uncommitted selected text; confidence and retained-code floors do not delay
  it. Otherwise Backspace unlocks when it reaches the noncommitted boundary.
  Rank selectors still edit the current segment rather than confirm a Tab lock.
  Up/Down alone does not arm locking. Clear/schema migration discards locks;
  literal exits still emit only live raw code. Rime stores the locked text/raw
  boundaries in a length-framed context property shared by the separate processor
  and translator environments; confidence trackers remain processor-local.
- A one-key sentence segment is legal only when the whole input is one key.
  Other segments consume at least two keys. `;`, `'` and digits select explicit
  lexicon ranks. An implicit non-first rank is legal only when the whole input is
  consumed by one lexicon edge; segmented paths use first ranks unless selection
  is explicit. Empty-code automatic-commit continuations hide whole-input
  non-first edges, but retain decoder-approved segmented duplicate-single paths
  when `允许单字重码组句` is on (for example `xrxbj` must retain `反刍` after
  committing `反`); non-first multi-character words still need a selector.
  Probabilistic early commit must preserve the already-ranked full-sentence paths,
  including eligible non-first single-character segments.
  Multi-character lexicon entries are legal edges. `允许单字重码组句` (default
  on) additionally allows non-first single characters without a selector on
  segmented paths and ranks those paths by language-model score so they can
  become the visible first candidate. A whole-input single lexicon edge still
  keeps first-rank characters ahead of later ranks. It does not bypass
  `高频字仅使用最优码组句` or the full-code whitelist, and explicit digit/`;`/`'`
  rank selection still works when it is on. Multi-character words still need an
  explicit selector on segmented paths.
- Sentence mode is controlled only by `自动启用整句模式` (default on). It
  activates when the current schema name contains `整句`.
- Sentence decoding is latest-generation-only and asynchronous. A stale Beam or
  Qwen result must never replace newer composition state. Pending UI keeps the
  previous candidate list and stitched live raw suffix.
  Manual sentence navigation freezes Qwen ordering for that generation; editing
  raw code re-enables reranking. Apply a Beam generation only once, including
  when a synchronous key-path completion races the asynchronous worker.
- Shape sentence loading is TCSKNM03-only (Q8 version 2 mainline, same-format
  Q16 version 1 readable). Search Models/ then runtime root for
  `sentence-fivegram-mobile.bin`; never search KLM or legacy trigram paths.
  Rime likewise searches only this fivegram filename. Preserve observed records
  independently of quantized zero codes and preserve empty-context backoff.
  Models are mapped read-only in Core and paged in Lua. Core-only upgrades do
  not replace model files. Beam
  expansion adds `2.0` per emitted Unicode character. Supplemental entries use
  `clamp(9 + 2 * ln(weight / 1000), 0, 16)` and affect sentence ranking only.
  A whole-input single-character candidate gets a ranking-only `5.0` reward
  when the unsplit input is that character's shortest available code (source
  order breaks equal-length ties), regardless of its rank under that code. The
  reward does not enter confidence mass or apply to an explicitly selected rank.
- Compact final ranking adds `2.0 × code length` for primary-code single-character
  edges, removes isolation penalties only for primary/explicit single-character
  edges of at least four codes, and gives the original Top-5 a bounded 50k-word
  Bloom-filter vote. None of these enter Beam or confidence mass; without an
  n-gram model they are disabled. Qwen consumes the resulting base Top-5 and
  keeps its existing fusion formula. See `docs/COMPACT_RANKING_PRIORS.md`.
- Qwen3 0.6B Q8 reranks exactly the first five n-gram candidates. If the
  pre-Qwen base winner has 2..6 text elements, score each candidate by
  `(1-alpha)*BaseScore + alpha*QwenScore`: its own length 2 uses alpha=0.15,
  3..6 uses 0.30. Candidate lengths outside that range use the original
  lambda converted to alpha=lambda/(1+lambda) (one character lambda=0.30,
  longer/unknown lambda=0.84). If the base winner itself is outside 2..6,
  preserve the original whole-set additive policy, without extrapolation.
  This supersedes the intermediate base-length shared-weight calibration.
  The 2026-09-08 grouped experiments are documented in
  `tools/SentenceLengthEval/README.md`. A subsequent frozen 50,000-case,
  record/text-disjoint confirmation achieved 99.142% versus the original
  99.006% and intermediate 99.066%; its net +38 over the intermediate policy
  came from mixed-length sets. Three-character targets still regress versus
  the intermediate policy (99.03% vs 99.16%), but beat the original 98.83%.
  These are context-free snippet benchmarks, not real typing acceptance.
  Its GGUF is mapped directly and is not encrypted.
- Sentence residency follows both schema and settings: preload in the background
  only when sentence input and neural reranking are enabled; otherwise cancel
  pending work and release the owned Sentence process. Never unload on idle.
  Rapid off/on transitions must finish release before reloading; late scores
  from the previous lifecycle must not reach the active composition.
- Known limitation: isolated Qwen scoring can worsen very short candidate sets,
  because it scores surface text without Tiger code context. Fix the scorer or
  skip short-text reranking if this becomes material; do not patch candidate
  identity/order plumbing without a failing trace.
- Windows early commit is experimental and defaults off. It requires raw length
  greater than four, exact consecutive one-key generations, confidence mass at
  least `0.995`, and the same raw boundary. Confidence is tracked independently
  for every `(text prefix, raw boundary)` from the visible candidates, plus
  dropped incomplete-tail lattice states when present. A raw
  boundary is eligible when the confidence mass of visible candidates that also
  have a segmentation boundary there reaches `0.99999`; their text need not
  agree. This weighted check prevents negligible crossing paths from vetoing an
  otherwise stable prefix. Two consecutive generations suffice when both prefix
  shares reach `0.99999`; otherwise three evidence generations are required.
  When several prefixes mature, commit the longest,
  then the higher-share and earlier-boundary prefix. Backspace, contradictory
  complete evidence and manual navigation invalidate or suspend evidence. When
  the current key is an incomplete code tail, merge the already computed
  dropped-tail lattice states into the confidence pool so competing prefixes
  such as `上午` and `上窦` are compared; supported closed-boundary prefixes in
  that merged generation also increment evidence. This deliberately accepts a
  small probability of a later segmentation flip in exchange for substantially
  earlier commits. A complete generation whose best
  visible candidate confidence is below `0.995` is also a comparison-only gap
  unless a visible prefix still reaches `0.995` after the merge. Keep a tracker
  only while current merged prefixes still support it; a stronger fork after a
  shared stem drops it. These gaps add no evidence, do not break a supported
  sequence and may span at most three generations. Keep the complete unstable
  suffix and at least
  three uncommitted raw code characters from the completed decode generation.
  `保留最少编码数量` defaults to `0` (no extra limit). A value greater than zero
  raises the leftover-raw-code floor for both probabilistic early commit and
  empty-code automatic commit to that count; probabilistic commit still never
  goes below three.
  `test整句` `uriczwxmjou` must not commit transient
  `可佛`; Tiger `nuusvbbhoi` must finish as `左手匕首` rather than `左手要好自`;
  `iejryfenahbmsp` may commit `新人` but must finish as `新人上午来面试`, never
  `新人上窦...`.
- Empty-code automatic commit is a fixed part of `整句自动提前上屏` (default
  off); there is no separate switch. Before the new key, accept either exactly one group-eligible candidate
  or a group-eligible candidate that is also the visible first candidate and has
  untruncated confidence share at least `0.99999`. Appending an ordinary letter
  must then leave no complete lexicon path; first check whether the selected last
  lexicon segment is still a proper code prefix. Non-first multi-character words
  shown for manual selection do not create implicit group ambiguity without a
  rank selector. With duplicate-single grouping enabled, eligible non-first
  single characters (including whole-input edges) and decoder-approved segmented
  paths participate in both uniqueness and strong-confidence comparisons.
  The exact precommit query must apply the same eligibility, independently of
  Beam pruning: `ot` = `是/题`, `qm` = `目` must not falsely commit `是` at `otq`.
  Defer
  while the segment can grow; if the actual extension goes dead, commit the saved
  candidate's uncommitted suffix, preserve the committed sentence context, and
  retain every appended letter as the next composition. Collecting confidence
  evidence while this setting is enabled may inspect the existing visible list,
  but its key-path check must stay a lightweight lexicon-path test rather than a
  synchronous n-gram/Beam decode.
  A single visible group-eligible candidate is not proof of uniqueness after
  Beam pruning. Immediately before committing through the unique-candidate
  branch, check the base raw code for another group-eligible output using an
  exact lexicon-path query independent of Beam. Different segmentations of the
  same text do not count as different outputs. The strong-confidence branch
  keeps its existing acceptance rule. Windows, Rime and Fcitx5 share this check.
- Windows early-commit confidence/mass aggregation must run over the already
  narrowed visible/top-candidate list, not the full beam pool. Incomplete-code
  tails may merge the already computed `states[consumedLength]` lattice into the
  evidence pool; supported closed-boundary prefixes may count as a new evidence
  generation, but they must not re-widen the common complete-code path. Re-widening
  the complete path was a real performance regression once (beam pool up to 100x
  the visible list on every keystroke). Fcitx5 Android `tigerclaw_sentence_core`
  received these 2026-09-01 independent-prefix, dropped-tail comparison and
  closed-boundary rules. The Rime Lua port received the same tracker rules,
  the duplicate-single-character switch (default on) and the empty-code
  continuation split on 2026-09-04. Fcitx5 Android received the duplicate-single
  switch, continuation split, full-code whitelist and minimum-retained-code
  setting on 2026-09-05. Its native decoder keeps Beam 2000 through raw position
  24 and uses 48 thereafter; host benchmarks and real-device acceptance remain
  separate because the runtime and UI scheduling differ from Rime.
- TSF key requests carry stable `client_session` + `event_id`; timeout retries
  reuse them and Core returns the cached first response without executing a
  physical key twice.
  Pipe requests reject reentry; TSF timers defer pipe work while a request is
  active. Read/write share one request deadline, with cancellation completion
  drained before releasing I/O storage (cleanup may exceed that deadline).
  Failed-key replay keeps event IDs and FIFO order, yields after 8 events or a
  60 ms batch budget, and resumes on a 30 ms timer under the existing queue TTL.
  Standalone Windows pipe fault tests: `tools/test_tsf_pipe.bat` (isolated pipe).
- Core startup is limited to a non-service user in a nonzero session on
  `WinSta0\Default`. TSF checks before scheduling and launching Core; Core
  independently checks before registration UI, singleton ownership or IPC so
  older installed TSF DLLs cannot launch a SYSTEM Core from the logon desktop.
  Startup checks: `tools/test_tsf_startup.bat` and Core tests
  `--startup-context-tests`. Do not bypass the guard to support a service host.
- TSF bypasses protected input before modifier tracking, cached responses or
  key IPC: secure activation, non-user desktop/account, standard password
  Edit/RichEdit controls and TSF keyboard-disabled contexts. Bypass clears
  local pending responses/events/replay queues and timers; enqueue, replay and
  response application recheck protection. Ordinary uncertain requests retain
  their retry/dedup policy. Tests: `tools/test_tsf_protected_input.ps1` and
  `tools/test_tsf_startup.bat`. Custom password controls without OS metadata
  cannot be identified reliably; no input text is inspected for detection.
- Native Hook also snapshots each physical key with stable replay identities.
  Uncertain requests are held and retried FIFO (128 events, 5 s TTL; batches of
  at most 8 events / 60 ms, resumed by the 200 ms state pump). Expiry, overflow
  or focus changes discard pending events and order composition cancellation
  before subsequent keys; never pass through a key whose result is unknown.
  Focus publication remains pending until sent and reconnects resynchronize it.
  Left/right modifiers are tracked independently. Diagnostics are opt-in via
  `TIGERCLAW_HOOK_DIAGNOSTICS=1` and omit input/commit text and window titles.
  Isolated frontend tests: `tools/test_hook_native.bat` (no global hook).
- Manual add-word and recent-schema switching keep their existing enable flags
  and use configurable exact modifier chords. Defaults remain `Ctrl+=` and
  `Ctrl+M`; TSF and Native Hook both route these actions through Core.
  Settings show only the two shortcut rows. Disabled bindings display `清空`;
  the `修改` dialog clears or restores defaults immediately into the unsaved
  settings page. Legacy enable flags remain internal compatibility data.
- Overlay owns candidate display. Ordinary word commits experimentally leave an empty native candidate frame
  at its published rectangle when `上屏后候选窗驻留时间(毫秒)` is positive
  (0..60000, default 0/off). Core publishes `CandidateResidenceDurationMs`
  and the absolute `CandidateBackgroundUntil` deadline. A new ordinary composition
  can animate from it; focus/cancel/English/off/hiding/invalid geometry revoke it.
  Fresh-caret gating and candidate reveal delay in the first resumed ordinary
  session preserve only the blank frame within the original deadline.
  A fresh ordinary commit during that wait or an unfinished animation starts
  another configured-duration residence at the actual displayed rectangle.
  It never preserves text or extends expiry on caret updates. Real-window probe:
  `overlay_pending_frame_tests.exe --blank-residence`.
  It retains an already published nonempty
  candidate/code-only frame while
  sentence decoding is pending in the same `CandidateFrameSession`. This also
  covers another key after a completed empty decode, without a hide/show flash.
  Clear/focus/session changes and explicit hiding still invalidate retention;
  no hidden frame is resurrected and no placeholder is created while pending.
  It pins the first caret anchor for a composition
  and flips above the caret when needed. TSF legacy candidate UI is not active.
  Native menus open without waiting for Core schema queries and use a dedicated
  temporary host independent of candidate/status visibility. Foreground permission
  is best-effort, not a display prerequisite; menu-lifetime outside-click/Escape
  monitoring provides fallback dismissal. Schema submenu command IDs are fixed
  to the list shown at expansion, never remapped by a later Core reply.
  Temporary pinyin reverse lookup always shows available splits, full codes and
  comments without annotation delay, irrespective of normal annotation settings.

When changing sentence behavior, update tests and the standalone Rime/C++ ports
only where they intentionally share that invariant. The implementation and tests
remain the final authority for lower-level cache and Beam details.

Windows sentence Tab learning from PR #4 is integrated locally with Smart mode
remaining removed. `整句Tab自学习` defaults on; only corrected text acknowledged
as successfully committed by the updated TSF is learned. Journals stay in each
schema source directory. Learning scores use immutable indexes; learned search
results cannot supply automatic-commit confidence. Existing candidate-length Qwen
weights remain in effect, with the learning reward added afterwards. Native Hook now advertises learning receipts (2026-09-26): successful text
submission with unchanged foreground window/process acknowledges on the original
key pipe. Failed/suppressed submissions do not learn; failed acknowledgements
never replay text. This confirms OS submission, not application insertion. The Rime and Fcitx5 ports
now share correction scoring, legal search retention and automatic-commit isolation;
they learn after host submission, not a Windows TSF/application insertion receipt.
Rime uses schema-scoped LevelDb and `tiger_sentence/tab_learning` (default on);
Fcitx5 uses an asynchronous TCL1 journal and `TabLearning` (default on). Both
settings also cover direct non-first candidate taps: compare against the current
first path, do not reinforce first-choice taps, and consume each submission once.
Rime and Fcitx5 correction weights now align with supplemental corpus weights:
each confirmation adds 1000, using `clamp(9 + 2 * ln(weight / 1000), 0, 16)`.
The 30-day half-life applies to weight. Existing journals are replayed under
this rule. Windows now uses the same 9/10.39/11.20 confirmation rewards and
16-point cap (2026-09-19), including replay of existing journals; confidence
maturity inverts this curve, and stable top1 reinforcement stops at 11 points.
Real-model `zhhbi` needs two corrections to promote
`虎娘` over `其父`; real librime tests cover taps, Tab/space and engine restart.
Rime's shared manual-confirm notification also covers keyboard selection. See each
port README for persistence and acceptance limits. See `TAB_LEARNING.md` and
`Protocol/messages.md`; tests include 10,000-record index pressure and score
equivalence. Actual application commit acceptance remains separate from tests.

## Key Code Paths

Startup and IPC:

- `next/TigerClaw.Core/Program.cs`
- `next/TigerClaw.Core/ProcessLauncher.cs`
- `next/TigerClaw.Core/ProtocolHandler.cs`
- `BimeTSF2/SampleIME/PipeClient.cpp`

Input behavior and data:

- `next/TigerClaw.Core/InputMethodEngine.cs`
- `next/TigerClaw.Core/CoreRuntimeState.cs`
- `next/TigerClaw.Core/SentenceInputDecoder.cs`
- `next/TigerClaw.Core/SentenceFivegramModel.cs`
- `next/TigerClaw.Core/SentenceSupplementModel.cs`

TSF/UI:

- `BimeTSF2/SampleIME/KeyEventSink.cpp`
- `next/TigerClaw.Overlay.Native/main.cpp`
- `next/TigerClaw.Overlay.Native/Renderer.cpp`
- `next/TigerClaw.Overlay.Native/Transport.cpp`
- `next/TigerClaw.Overlay/MainWindow.xaml.cs`
- `next/TigerClaw.Overlay/MainWindow.Candidate.cs`
- `next/TigerClaw.Overlay/OverlayStateSource.cs`
- `next/TigerClaw.Dialog/ConfigWindow.xaml.cs`
- `next/TigerClaw.Dialog/CorePipeClient.cs`

Native hook:

- `next/TigerClaw.Hook.Native/App/main.cpp`
- `next/TigerClaw.Hook.Native/App/HookRuntime.cpp`
- `next/TigerClaw.Hook.Native/Hook/KeyboardHook.cpp`

## Build And Test

Initialize the pinned llama.cpp dependency once:

```bash
git submodule update --init --recursive
```

Debug build and smoke test:

```batch
next\build_next.bat
next\register_dev_corepath.bat
next\_run\Debug\x64\TigerClaw.Core.exe --with-overlay
```

After testing:

```batch
next\unregister_dev_corepath.bat
```

Core tests are in `next/TigerClaw.Core.Tests/` and have no external test
framework dependency.

Release commands:

```batch
publish.bat
publish_arm64.bat
```

`publish_no_qwen.bat` runs the x64/Win32 release pipeline with `--no-qwen`:
keeps the n-gram model, Sentence host and llama.cpp license, skips the external
Qwen model/license requirement and copy, and produces `虎爪输入法-<版本>-no-qwen.7z`.
Packaging excludes all GGUF files, including leftovers in an existing release;
the source release model files are preserved. The normal package is unchanged.
Users can obtain the model through the Dialog's neural-rerank setting guide.
WSL packaging regression: `python3 tools/test_publish_no_qwen.py` (Windows 7-Zip
required, isolated fixtures only).

The main release is `release/`. Windows on ARM development output is
`release_arm64/`; its default uses an ARM64X wrapper with ARM64 and x64 TSF
sidecars. `--diagnostic` enables embedded TSF logging.

`publish_core_arm64.bat` publishes only `next/TigerClaw.Core/` as a
self-contained ARM64 Native AOT executable and replaces `TigerClaw.Core.exe` in
an existing `release_arm64/`, skipping Overlay, Dialog, Sentence, Hook.Native,
Shared.dll and both TSF DLLs for faster Core-only iteration. It does not touch
`EmbeddedBuildInfo.h` or rebuild the TSF DLL. Current TSF and Native Hook do
not bind connections to a Core executable hash or require querying its path.
Trial expiry checks and metadata have been removed; protocol handshakes remain
enforced. Legacy publish configuration keys for hash verification and expiry
are ignored. Previously installed frontends retain their old hash/expiry checks
until replaced with a new frontend;
replacing Core alone cannot change an old DLL's behavior.

`publish.bat` rebuilds TSF into `next/_run/Release/tsf/{Win32,x64}/` with
separate `obj/publish-x64-tsf/` intermediates, never selecting legacy output
paths. Before packaging it checks DLL PE architecture and source/copy identity.
Isolated packaging tests: `tools/test_publish_tsf.ps1`. These checks do not
replace real 32-bit WPS acceptance (loaded DLL identity, input, candidates,
commit and reconnect); that acceptance remains pending until tested in WPS.

`publish_arm64.bat` schedules dependency-aware build tasks with a default limit
of 2 (`--jobs N`, 1..16; `--serial` selects 1). Embedded metadata is independent
of Core and precedes every native frontend including the ARM64X wrapper. Overlay and
  Dialog remain serialized to support the WPF fallback's shared build/output files. Each
task has stdout/stderr logs and a timing CSV under
`next/_run/ReleaseArm64/logs/`. Failed builds drain active workers and never
enter deployment. `--build-only` validates artifacts but skips process shutdown
and release-directory writes; it still builds and updates build metadata.
Model files copy directly from their canonical sources at deployment, not via
the intermediate build outputs. Scheduler isolation tests:
`powershell -NoProfile -File tools/test_publish_arm64.ps1`.

Important local rule: this checkout's `release_arm64/` is the user's daily
runtime. Standing user authorization (2026-09-10): after each Overlay adjustment
is built and verified, deploy the ARM64 Overlay executable into `release_arm64/`
automatically, retaining a recoverable backup and restarting only that deployed
Overlay if needed. This authorization does not cover Core or other components,
configuration/code tables, or cleaning the release directory.

Outside that Overlay-only authorization, `release_arm64/` is the user's daily
runtime, not disposable build output. Never delete, clean, replace or partially
rebuild it unless the user explicitly asks. In particular, do not run a broad
`git clean -X` in this repository. Its config, code tables and user adjustments
may exist only in that ignored directory.

Generated models live outside Git under
`C:\Archive\tigerclaw_sentence_ml\runtime`; publish scripts copy them into the
release tree. Keep `.bat` files CRLF.

## Related Ports And Experiments

- Rime deployment-resource fix (2026-09-25): ship the 50k-word Bloom filter at
  `rime/tiger_sentence/models/tiger_sentence.lexical.bin`, never the user-root
  `.bin` path that librime cleanup_trash moves into trash. Lua prefers models/
  and retains root lookup only for old-pack compatibility. Do not read trash.
  `tools/package_tiger_sentence_rime.py` rejects top-level binary resources and
  verifies model identity, source bytes, archive CRC and extracted manifest.
  `tools/test_rime_resource_deployment.py` + `rime_resource_deploy_probe.cpp`
  exercise real librime maintenance/cleanup and fresh-process Lua loading twice.
  Lua 5.4/5.5/LuaJIT regressions pass. Corrected Rime package:
  C:\Archive\threeway-mainline-20260925\虎整句-Rime-20260925-三模型Q8主线-部署修复版.7z.
  This supersedes the earlier same-day Rime package's root lexical-resource layout;
  TigerClaw packages and the 405.66 MB Q8 model bytes are unchanged.

- `rime/tiger_sentence/`: standalone experimental Rime pack.
  Synced public runtime through PR #18, `bd83900`, on 2026-09-19.
  The distributed schema defaults to `tiger_sentence/memory_profile: compact`;
  `balanced` remains an explicit custom-patch alternative. Safe locked tail
  Backspace reuses the active lattice; text-only buffered deletion skips scoring.
  Optional `tiger_sentence_early_commit_to_preedit` (default off) buffers early
  confirmations in preedit until host submission. Backspace removes live code,
  then buffered Unicode characters without restoring codes. Deferred learning
  is cancelled on deletion/cancel; schema changes cancel the buffer. The native
  ASCII composer wrapper uses `tiger_sentence_ascii.schema.yaml` as a private
  configuration so raw/inline exits cannot emit the internal marker. Update
  rime.lua, both Lua modules and both schemas together. Real-host regression:
  `tools/test_rime_preedit_integration.py` and `tools/rime_preedit_probe.cpp`.
  The three sentence switches have no `reset`: user choices persist in
  `tiger_sentence.options.yaml`, with first-use `tiger_sentence/option_defaults`.
  New sessions restore preferences; existing sessions synchronize before input.
  Config I/O occurs only on initialization/toggles. Keep this user-owned file
  across upgrades; do not distribute it. Tests: `tools/test_rime_options_integration.py`.
  Regenerate its plain-text data files with `python3 tools/export_tiger_sentence_rime.py`.
  Keep Lua 5.5 compatibility: never assign to a `for` control variable; use a
  separate local for converted values. Run `tools/run_regressions.py` with
  Lua 5.5, Lua 5.4 and LuaJIT when changing the runtime module.
  Learning uses immutable per-code aggregate partitions and lazy score epochs;
  normal confirmation and minute refresh do not replay the whole journal.
  `learning.build` remains the independent full-replay test oracle. Clock
  rollback/future timestamps retain a replay fallback. Preserve database hash
  arithmetic, durable-write-before-publish and the 64-code prefix-search bound.
  Performance evidence and commands: `tools/RIME_PERFORMANCE.md`.
  The pack includes `symbols.yaml` as its directly-committing punctuation
  default; the schema imports that preset instead of Rime's `default` preset.
  The schema disables built-in `digit_separators` so punctuation after digits
  follows that table without a pending ASCII separator candidate. Lua retains
  the immediate decimal point after a digit.
  Lua predecodes mobile-model unigrams, lazily scores isolation from path
  prefixes without changing Beam pruning, materializes segmentation on display
  access, and reuses final candidates when adding same-generation evidence.
  The code table, character ranks and full-code whitelist are runtime-loaded
  txt files (`tiger_sentence.codes.txt` and siblings) so users can edit or
  import other shape-code tables without re-running the exporter; the
  high-frequency optimal-code limit is the schema config
  `tiger_sentence/high_freq_limit` (default 1500, 0 disables) and the exporter
  only cross-checks that default against `dist_config.txt`. Auto commits
  rebuild the composition with one atomic input assignment (no intermediate
  `clear`) to avoid candidate-window flicker. Its synchronous Lua decoder keeps
  a 200-wide Beam through raw position 24 and uses 48 thereafter, preventing
  long low-confidence input from queuing candidate generations. It is not part
  of Windows TSF.
- Open-source mirror `tiger-sentense-rime`
  received PR #1 on 2026-09-12 (merge `a19c38e`), synced back locally: caret-aware
  edits, lazy-menu Tab preparation and whole-decode model-failure fallback.
  Rime confidence now uses retained full Beam rather than display Top-20 and
  propagates ancestor truncation, potentially delaying automatic commits;
  Windows keeps its existing narrowed-pool policy. Details and tests:
  `rime/tiger_sentence/RIME_CORRECTNESS.md`, `tools/run_regressions.py`.
  The mirror
  (https://github.com/lvyww/tiger-sentense-rime, GPL-3.0, public): the
  standalone release of the Rime pack above. Publishing rules:
  - `rime/tiger_sentence/` in this repo is the source of truth. Published
    runtime files (schema, `lua/tiger_sentence.lua`, the three data txt
    files, supplement, `symbols.yaml`, `rime.lua`, `default.custom.yaml`)
    must stay byte-identical to it; only `tools/` (test + bench) gets path
    adaptation (`/rime/tiger_sentence/lua` -> `/lua`, data dir -> repo
    root). The exporter is never published; it is TigerClaw-internal.
  - The mirror README is a standalone rewrite: installation, model download
    from Releases, data-file customization. Do not leak internal paths
    (`release_arm64/`, `dist_config.txt`, dev model paths) into it; the Lua
    module keeps its inert dev fallback paths to stay byte-identical.
  - The Q8 fivegram never enters git (above the 100 MB limit); distribute it
    as a release attachment. Canonical local source is
    `C:\Archive\tigerclaw_sentence_ml\runtime\sentence-fivegram-mobile.bin`,
    405,663,171 bytes, SHA256
    `756f6c92cf43ad6e8e3087ce66b711ac6ad0fc41e6f3fb82b3766e35ecab8681`.
    Both mainlines use the same file. No old KLM or trigram reader fallback.
    Local changes do not publish public Release attachments automatically.
  - Release procedure per version: sync files into the mirror layout ->
    run the full test suite from the mirror layout (Lua 5.4 with model and
    luajit no-model) -> tag `vX.Y.Z` and push (SSH works) -> build the
    end-user zip `tiger-sentense-rime-vX.Y.Z.zip` from the mirror layout
    plus `LICENSE` and the model, excluding `tools/` (verify CRC, Lua
    byte-identity, model md5) -> upload the Release. This environment has
    no `gh` CLI and no stored HTTPS token, so Release assets are uploaded
    manually by the user on the web UI. v1.0.0 was published this way on
    2026-09-04.
- `/home/yc/fx5/fcitx5-android`, plugin `tigerclaw`: standalone native Fcitx5
  Android port. Core stages and wiring are implemented; real-device performance,
  packaging and signed release acceptance remain. See
  `docs/FCITX5_ANDROID_PORTING_PLAN.md`.
- `tools/SentenceNgramTrainer/`: Windows .NET 10 external-counting trainer.
- Offline neural/model experiments are summarized in
  `tools/README_sentence_neural.md`; generated corpora and models stay outside Git.

## Common Workflows

IPC change:

1. Update `Protocol/messages.md` or `Protocol/sentence_messages.md`.
2. Update `ProtocolHandler.cs` or the Sentence client/server.
3. Update every affected TSF/Dialog/Hook caller.

Key behavior change:

1. Start in `InputMethodEngine.cs`.
2. Check response/UI shaping in `ProtocolHandler.cs`.
3. Change `KeyEventSink.cpp` only for physical capture or TSF commit behavior.
4. Add a regression in `TigerClaw.Core.Tests`.

Settings change:

1. Dialog UI in `ConfigWindow.xaml(.cs)`.
2. persistence/defaults in `CoreRuntimeState.cs` and `dist_config.txt`.
3. Preserve grouped layout, search, dirty-state feedback and empty values.
   Category-internal display order is defined by Dialog `ConfigSettingOrder.cs`;
   fixed and dynamic rows share this order. Unknown keys sort last within their
   existing category, using ordinal case-insensitive key order.

Release change:

1. Update `publish.bat` or `publish_arm64.bat`. `publish_core_arm64.bat`
   covers Core-only iteration; keep it in sync if `TigerClaw.Core.csproj`'s
   build inputs change.
2. Preserve CRLF.
3. Verify required executables, both TSF architectures, models and licenses.
4. Package `dist_config.txt`, never a developer's live `config.txt`.

## Documentation Policy

- Keep this file as the concise current handoff.
- `docs/README.md` is the document index.
- Put stable message fields in `Protocol/`, user behavior in the user manuals,
  and port-specific details beside the port.
- Delete superseded stage notes instead of adding another entry document.
- Historical documents belong in `docs/archive/` only when they retain real
  diagnostic value. Generated/release payloads are not documentation sources.

## Style And Safety

- The worktree may be dirty. Never revert unrelated user changes.
- Prefer `rg`/`rg --files`; use `apply_patch` for manual edits.
- C#: 4 spaces, surrounding Allman style, `_privateField`, `camelCase` locals.
- C++: log unambiguous code points when debugging IME/window matching; disable
  high-frequency diagnostics afterwards. Avoid raw Chinese source literals for
  runtime matching; construct code points and add a readable end comment.
- Do not wire `reference/` into active builds.
- Do not delete ignored runtime data merely because Git labels it generated.
