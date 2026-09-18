# 整句提前上屏端到端评测

该评测直接驱动 `InputMethodEngine`，逐键记录自动提交和候选窗剩余 raw 编码，
用于同时衡量提前上屏的准确度和积极性。旧的 `--sentence-stream-eval` 只模拟
置信度阈值，不经过当前 tracker、掉尾比较、空码顶屏和续写状态机，不能替代本评测。

## 测试集

测试集位于 Git 外：

```text
C:\Archive\tigerclaw_sentence_ml\baseline\tiger-sentence-early-commit-eval-v1.json
```

它包含 3003 条：编码长度 5–8、9–12、13–18、19–30、31+ 五档各
600 条，另含 `新人上午来面试`、`左手匕首`、`有一些人在这里看东西`
三个历史回归案例。语料按固定输入顺序截取，便于不同提交之间比较。

重新生成：

```bash
python3 tools/prepare_sentence_early_commit_eval.py
```

默认规模在当前开发机上实测约需 4.5 分钟。如需快速抽样，可用
`--per-bucket 100`恢复为约 500 条。

## 运行

先构建 Core 测试程序，再运行：

```batch
next\_run\Tests\Debug\net10.0-windows\TigerClaw.Core.Tests.exe ^
  --sentence-early-commit-eval ^
  C:\Archive\tigerclaw_sentence_ml\baseline\tiger-sentence-early-commit-eval-v1.json ^
  release_arm64\Models\sentence-ngram-v2.bin ^
  release_arm64\码表\虎整句\虎整句.txt ^
  C:\Archive\tigerclaw_sentence_ml\baseline\tiger-sentence-early-commit-eval-v1-report.csv
```

评测运行 `early-commit`：概率提前上屏与空码顶屏作为同一流程开启。

Qwen 重排关闭，以固定 n-gram 状态机结果。评测使用同步解码，避免机器负载改变
样本结果；真实异步输入延迟应另做运行时性能评测。

## 指标

准确度：

- `baseline_top1_rate`：不开提前上屏时，最终首选与目标句一致的比例；
- `baseline_correct_safe_early_case_rate`：基线正确且发生提前上屏的样本中，所有
  中间提交始终是目标句前缀的比例；
- `baseline_correct_unsafe_case_rate`：提前上屏破坏基线正确句的比例，核心回归指标；
- `baseline_correct_final_exact_rate`：基线正确句开启提前上屏后仍最终完全正确的比例。

积极性：

- `early_case_rate`：至少发生一次提前上屏的样本比例；
- `mean_retained_codes`、`p50/p90_retained_codes`：从第 1 键到最后一键，
  每一键候选窗所留 raw 编码数；这是积极性的主指标，越低越积极；
- `mean_eligible_retained_codes`、`p50/p90_eligible_retained_codes`：仅统计第 5 键
  以后、概率提前上屏开始具备资格的区间，用于诊断规则本身；
- `mean_post_commit_retained_codes`：刚发生提前上屏后候选窗平均留码数；
- `mean_first_commit_key`：首次提前上屏发生在第几键；越低越积极；
- `raw_commit_coverage_before_final`：按空格最终提交前，已经提前消费的 raw 编码比例。

只看“提交后留码数”会奖励极少触发但偶尔很激进的策略，因此必须和
`early_case_rate`、逐键留码分布一起判断。准确度比较时以基线正确子集为主；基线
本身错误的样本仍保留在 CSV 中，用于观察错误路径，但不应归咎于提前上屏。

## 策略对照实验

`--sentence-early-commit-policy-eval` 在不改发布默认值的前提下，对比证据代数、
留码下限和合并不完整尾码代是否计数。第六个参数可选择单一变体：

```batch
next\_run\Tests\Release\net10.0-windows\TigerClaw.Core.Tests.exe ^
  --sentence-early-commit-policy-eval ^
  C:\Archive\tigerclaw_sentence_ml\baseline\tiger-sentence-early-commit-eval-v1.json ^
  release_arm64\Models\sentence-ngram-v2.bin ^
  release_arm64\码表\虎整句\虎整句.txt ^
  C:\Archive\tigerclaw_sentence_ml\baseline\policy-report.csv ^
  0 count-merged-tail
```

2026-09-01 的 3003 条复验显示：

- 仅把普通证据从三代改为两代，与当前策略指标完全相同；
- 仅取消留三码不增加触发率，对平均留码改善很小；
- 合并不完整尾码代参与证据计数时，触发率为 `88.28%`，平均留码
  从约 `15.62` 降到 `5.02`，基线正确样本零回归；
- 在此基础上取消留码下限会产生 3 条真实回归，因此留三码是高频掉尾
  证据下的必要安全边界。

### 10000 条复验

2026-09-02 使用 `--large` 生成 10000 条去重样本，五个编码长度档为
`1400 / 1501 / 2001 / 2501 / 2597`。七组完整对照耗时 2657.88 秒。

| 策略 | 触发率 | 平均留码 | 提前覆盖 | 基线正确回归 |
|---|---:|---:|---:|---:|
| 当前策略 | 7.26% | 17.516 | 1.48% | 0/9894 |
| 普通证据改两代 | 7.28% | 17.514 | 1.49% | 0/9894 |
| 取消留三码 | 7.26% | 17.399 | 2.09% | 0/9894 |
| 合并尾码代计数 | 91.21% | 5.330 | 72.39% | 0/9894 |
| 合并尾码代计数 + 两代证据 | 91.53% | 5.213 | 73.20% | 0/9894 |
| 合并尾码代计数 + 取消留码 | 91.23% | 4.548 | 76.38% | 19/9894 |
| 三项同时放宽 | 91.68% | 4.416 | 77.44% | 18/9894 |

数据集和报告位于 `C:\Archive\tigerclaw_sentence_ml\baseline` 下的
`tiger-sentence-early-commit-eval-10000-v1.json` 和
`tiger-sentence-early-commit-policy-10000-v1-report.csv`。

根据该复验，Windows Core 采用“合并尾码代计数”，但保留三代普通证据和至少
三个未上屏 raw 编码。该策略是概率决策：允许极少数后续编码导致切分翻转的
理论风险，以换取更高的提前上屏覆盖率。

### hufu-ime-rust 启发的截断证据策略（2026-09-18）

本轮首先对两个方向做端到端 A/B：

- `armed-strong1`：长残码进入 armed 后，强置信证据允许单代成熟；
- `truncated-strong`：beam 截断时仍保留幸存路径证据，但 `ConfidenceTruncated`
  继续为 true，只允许当前代、`share >= 0.99999`、closed-boundary 的证据进入
  现有 tracker，仍需默认两代 strong 证据成熟。

生产模型、当前码表、10001 条大集（原 10000 条 + `jxjcdnp → 斜劈` 回归）结果：

| 策略 | 触发率 | 平均留码 | P90 留码 | 提前覆盖 | 基线正确回归 |
|---|---:|---:|---:|---:|---:|
| 旧 strict truncation veto | 90.34% | 5.891 | 10 | 67.39% | 0 |
| armed-strong1 | 90.50% | 5.735 | 9 | 68.59% | **2** |
| truncated-strong | 90.34% | **5.525** | **9** | **71.21%** | **0** |

`armed-strong1` 因两条 baseline 正确真实回归被否决并从代码移除。两条回归分别为
“有茶有书适合惬意地呆坐半天”被提前锁成“……惬意地吃……”以及
“由威廉二世陪同李鸿章走上阅兵台……”被提前锁成“……李鸿章走上来……”。

`truncated-strong` 的收益只发生在 31+ 编码长句：10001 条中有 763 条行为变化，
全部属于 31+ 档；其中 760 条 baseline 正确，760 条均保持 safe 且 final-exact。
31+ 平均留码 `7.178 → 6.464`，P90 `14 → 10`，提前覆盖
`72.20% → 79.66%`。首次提前提交位置和总体触发率基本不变，说明收益来自长句
后半程不再因为 beam truncation 一票否决而停止继续消费安全前缀。

#### Beam 压力测试

`--sentence-early-commit-policy-eval` 最后可再传一个 beam width，用于在同一 beam
下重新计算 baseline 后做压力比较，例如：

```batch
next\_run\Tests\Release\net10.0-windows\TigerClaw.Core.Tests.exe ^
  --sentence-early-commit-policy-eval ^
  C:\Archive\tigerclaw_sentence_ml\baseline\tiger-sentence-early-commit-eval-10000-v1.json ^
  release_arm64\Models\sentence-ngram-v2.bin ^
  release_arm64\码表\虎整句\虎整句.txt ^
  C:\Archive\tigerclaw_sentence_ml\baseline\truncated-stress.csv ^
  0 truncated-strong 100
```

10001 条大集从产品 beam=2000 一路压低的结果：

| Beam | baseline top1 | 平均留码 | 提前覆盖 | baseline-correct unsafe |
|---:|---:|---:|---:|---:|
| 2000 | 98.56% | 5.525 | 71.21% | 0 |
| 1000 | 98.56% | 5.571 | 70.79% | 0 |
| 500 | 98.56% | 5.638 | 70.29% | 0 |
| 250 | 98.56% | 5.713 | 69.81% | 0 |
| 100 | 98.56% | 5.834 | 68.96% | 0 |
| 50 | 98.56% | 5.961 | 68.11% | 0 |
| 25 | 98.56% | 6.147 | 66.99% | 0 |
| 10 | 98.56% | 6.406 | 65.44% | 0 |
| 5 | 98.56% | 6.542 | 64.67% | 0 |
| 3 | 98.53% | 6.410 | 65.60% | 0 |
| 2 | 98.50% | 5.941 | 68.85% | 0 |
| 1 | 96.61% | 4.347 | 78.59% | **0.11%** |

因此该策略不是理论上无条件安全：当 beam 极端收缩到 1、置信度退化为“唯一幸存
路径即 100%”时会出现真实回归；但在 10001 条上从产品 2000 一直压到 2 都保持
baseline 正确样本零回归。3004 条平衡集得到相同边界：beam=2 仍为 0，beam=1
首次出现 `璎珞 (noonsgw) → 朝` 的错误提前提交。

基于该结果，Windows Core 的发布默认策略改为接受强截断证据，不再对
`ConfidenceTruncated` 一票否决。安全门保持为：

- 仅消费当前 generation 的截断证据，不接受上一代异步结果；
- prefix `share >= 0.99999` 且 boundary closed；
- prefix 必须仍是当前显示首选的前缀；
- 仍需默认两代连续 strong 证据成熟；
- 仍至少保留 3 个 raw，并保持距上次概率提交至少新增 3 个 raw；
- 学习影响、手动导航等原有暂停条件不变。

`jxjcdnp → 斜劈` 在 strict、truncated 及压力测试中都保持 baseline 正确、无错误
提前提交且 final-exact，现有 visible-top prefix gate 足以覆盖 hufu 报告的该类
incomplete-tail 劫持案例。

在策略评测中，`current` 现在专门复现变更前的 strict truncation veto，
`truncated-strong` 对应新的发布默认行为；普通 `--sentence-early-commit-eval`
直接走发布默认策略。
