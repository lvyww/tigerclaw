# 虎整句（Rime 独立实验版）

本方案使用纯 Lua 码表格图和本地 n-gram，不依赖 Windows Core，也不是 Windows
TSF 发布包的一部分。它仍处于实验阶段，尤其是自动提前上屏在不同 Rime 前端上的
闪烁、提交顺序和长时间稳定性需要分别验收。

## 生成与部署

```bash
python3 tools/export_tiger_sentence_rime.py
```

导出器从 `release_arm64/码表/虎整句/虎整句.txt` 生成方案数据。该目录是本机
日常 ARM64 运行环境，禁止用清理命令删除。导出器写出的码表保持原始数据，
不做高频过滤；高频限制的运行时唯一来源是 schema 配置
`tiger_sentence/high_freq_limit`。导出器只校验 `dist_config.txt` 的高频默认值与
schema 默认值一致（不一致直接报错），白名单可用 `--full-code-whitelist` 覆盖
（它决定生成的白名单 txt 初值）。

部署到 Rime 用户目录：

1. 复制 `tiger_sentence.schema.yaml`、`tiger_sentence.supplement.txt`、三个
   数据 txt（`tiger_sentence.codes.txt`、`tiger_sentence.char_ranks.txt`、
   `tiger_sentence.full_code_whitelist.txt`）、默认标点 `symbols.yaml` 和
   `rime.lua`；
2. 复制 `lua/`；
3. 在已有 `default.custom.yaml` 中加入 `tiger_sentence`；
4. 重新部署，使 Lua 和数据文件重新加载。

不要覆盖用户已有的 `rime.lua` 或 `default.custom.yaml`；合并相应条目。默认在候选
窗口显示编码。用户自行开启 inline preedit 时，Lua 仍通过 `Candidate.preedit`
维护当前候选的分段编码。

## 运行文件与明文数据

本方案没有使用 Rime 的 `table_translator`，运行时只有一个实质 Lua 模块：
`lua/tiger_sentence.lua`（输入、整句解码、n-gram、补充语料和码表索引构建）。
根目录的 `rime.lua` 只负责向 Rime 注册 processor 和 translator。

码表数据全部是明文 txt，由 Lua 在首次使用时加载并建索引（与 Windows Core 的
`SentenceLexiconIndex.Build` 同语义：行序=名次、最优码选择、高频过滤、
全码白名单）。查找顺序：用户目录 → 共享目录。

| 文件 | 格式 | 作用 |
| --- | --- | --- |
| `tiger_sentence.codes.txt` | 每行 `字\t编码`，`#` 注释，兼容 CRLF/BOM | 码表；同码内行序即名次，编码仅小写字母（自动小写化） |
| `tiger_sentence.char_ranks.txt` | 每行一个字，行序=频序 | 常用字过滤与生僻字孤立惩罚；缺失时两者禁用 |
| `tiger_sentence.full_code_whitelist.txt` | 白名单字符，每行一个或连排 | 白名单字保留完整编码参与组句 |

用户可直接编辑或替换这些文件（例如导入其它形码方案的码表），重新部署后生效，
不需要重跑导出器。导入外部码表时注意：编码只能是拉丁字母；单字与多字词均可，
多字词作为整段码表边合法；同码行序决定选重名次。虎码特有的过滤默认开启，
想全部保留非最优码时把 schema 里的 `tiger_sentence/high_freq_limit` 设为 `0`
并清空白名单文件即可。

schema 配置 `tiger_sentence/high_freq_limit`（默认 `1500`）对应 Windows 的
`高频字仅使用最优码组句`：常用字（字频表前 N）只保留最优码；`0` 表示全部
放开；负数按 `0` 处理。

开发仓库中修改 `release_arm64/码表/虎整句/虎整句.txt` 后重新运行导出器即可
重新生成三个 txt。发布包不再提供用户自定义编码覆盖层，避免出现多套码表来源；
直接修改其他 YAML 或旧词典文件不会改变本方案候选。标点是例外：方案导入用户
目录的 `symbols.yaml`。方案包自带一份与 Windows Core 日常输入相符的默认值，
其中标量或 `{ commit: ... }` 映射直接上屏，列表映射才打开符号候选。用户可在
部署后继续修改，修改后需要重新部署。

数据加载状态可从模块的 `data_status()` 查询（路径、条目数、字频与白名单计数、
错误列表），用于排查文件缺失或格式问题。

## 输入行为

- 字母连续输入整句编码；空格提交候选，回车提交原始编码，Esc 清空。
- 有编码时，`;`、`'`、数字分别选择码表第 2、第 3、第 N 项，`0` 为第 10 项。
- 无编码时，`;` 和 `'` 不作为选重后缀，由 `symbols.yaml` 输出中文标点。
- 无编码时数字直接上屏（全角开关输出 ０-９，小键盘同样）；其后紧邻的
  句号自动输出半角小数点 `.`（与虎爪一致，包括全角数字），其它标点不受
  影响。任何非数字按键都会解除该状态。
- Up/Down 或 Tab/Shift+Tab 遍历可见候选；Tab 在首尾间循环，只调用高亮接口，
  不选择或提交候选。
- 一码段只在整段输入只有一码时合法。
- 只有整个独立输入由单一码表边消费时，才隐式显示该编码全部名次。
- `tiger_sentence_allow_duplicate_single` 开关（默认开，即“允许单字重码组句”）
  开启时，多段整句路径中的非首选单字按语言模型分数参与竞争，可以成为可见
  首选；非首选多字词在任何切分路径中仍必须显式选重。开关关闭时，切分路径
  只取各段首选。单独输入一个完整编码时始终保持码表首选在前。
- 分段空格只用于显示，不进入 raw code。

码表默认过滤与 Windows Core 相同：`高频字仅使用最优码组句`
（默认前 1500 字）限制常用字只保留最优码，`整句允许全码组句白名单` 中的
字符保留完整编码。两者在运行时按 schema 的 `tiger_sentence/high_freq_limit`
和白名单 txt 现算，改文件即生效；导出器只负责把白名单初值写进 txt，
并校验 `dist_config.txt` 的 Windows 默认值与 schema 默认值一致。

自动提前上屏默认开启，与 2026-09-04 之后的 Windows Core 规则一致。开关
`tiger_sentence_early_commit`（提前上屏）同时控制概率型提前上屏与空码自动
上屏，两者没有独立开关：

- raw 长度超过四键后，按 `(文本前缀, raw 边界)` 独立累计证据；置信阈值
  `0.995`，强证据与边界封闭阈值均为 `0.99999`。
- 连续两代强证据即可确认，否则需要三代；比较型空档最多跨三代，不累计证据。
- 当前键为不完整尾码时，已计算的 dropped-tail 格点状态并入置信池做前缀
  对比，封闭边界也可计为证据；完整编码路径只使用可见候选，不回扩 Beam。
- 共享词干后出现更强分叉时淘汰旧 tracker；多个 tracker 成熟时提交最长、
  份额更高、边界更早者。退格或手动遍历会清除/暂停证据。
- 概率型提交保留已参与竞争的非首选单字续句（如 `awmenamcunta` 提交“买”
  后仍显示“椟还珠”）；每次提交保留完整不稳定后缀并至少保留三个已完成
  解码的编码。
- 空码自动上屏与 Windows 语义相同：唯一合法候选，或（候选池未截断时）
  置信份额至少 `0.99999` 的可见首选；截断的候选池绝不能触发高置信上屏。
  其续写只隐式采用首选，与新一段的单字重码竞争无关。
  它与概率型提前上屏共用 `tiger_sentence_early_commit` 开关，关掉开关后
  空码自动上屏一并停止。
- schema 配置 `tiger_sentence/min_retained_raw_length`（默认 `0`）同时提高
  概率型与空码提交的最少保留编码数；非法值按 `0` 处理，概率型提交仍永不
  少于三键。

自动上屏（概率型与空码）通过 `commit_text` 加一次原子输入赋值重建
composition：不经过 `clear()`，避免出现空 composition 中间帧导致候选窗
闪一下。旧前端若不支持直接赋值 `context.input`，自动回退到
`clear + push_input`。候选窗坐标和上下翻转由 Rime 前端管理，不属于
Lua 方案能力范围。

## 补充语料

用户目录根部的 `tiger_sentence.supplement.txt` 使用 UTF-8：

```text
# 词条 权重
茧师 1000
新词
```

权重默认为 1000，重复词条以最后一项为准。奖励与 Windows Core 相同：
`clamp(9 + 2 * ln(weight / 1000), 0, 16)`。它参与 Beam 排序但不进入提前上屏
置信度，不会创建码表中不存在的编码。修改后需重新部署。

## 性能与诊断

模型页和上下文查询使用有界缓存；候选的生僻字孤立惩罚另有 8192 项有界缓存，
避免连续按键时反复扫描和查询相同字串。缓存满后循环替换旧项，不会随使用时间
无限增长。

Rime Lua 的 translator 是同步调用，不能像 Windows Core 那样让后台 generation
淘汰过期结果。为避免长串快速输入时每一代候选在按键队列中追赶，格图前 24 码
保持 200 Beam；第 25 码起按格图位置收敛为 48 Beam。按位置而不是当前输入长度
裁剪，因此逐键增量解码与同一编码的全量解码仍完全一致。该裁剪主要约束无稳定
候选的长随机输入；前 24 码的搜索宽度不变。

Lua 按一次 composition 在内存中汇总解码次数、模型缺页、读取字节数、提前上屏
证据构建次数和孤立惩罚缓存命中，不自动写入日志。调试时可从模块的
`performance_status()` 查看当前及上一次汇总。

## 模型

移动端优先使用仓库外的 `sentence-ngram-mobile.bin`（TCSKNM02）。它是 V2 模型的
无损分页重排，常驻稀疏索引约 2.1 MiB，上下文页 LRU 上限 8 MiB，不会一次读入
完整模型。

```bash
python3 tools/convert_sentence_ngram_mobile.py \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

查找顺序：用户目录 `models/`、用户目录根部、共享目录 `models/`，再尝试对应的
`sentence-ngram-v2.bin`；开发机最后回退到 `C:\Archive\tigerclaw_sentence_ml\runtime`。
大模型不得提交 Git。

## 验证

无模型路径：

```bash
lua tools/test_tiger_sentence_incremental.lua .
```

真实模型路径需要 Lua 5.3+：

```bash
lua tools/test_tiger_sentence_incremental.lua . --require-model
```

测试输出必须明确显示实际模型。LuaJIT 2.1 缺少这里使用的标准 `string.unpack` 和
`utf8` API，只适合无模型测试；无模型路径也可以在 Lua 5.4 下用
`sentence.set_model_enabled(false)` 显式模拟。无模型时按“码表名次 → 更少码表
边 → 分数”排序，整码单字（如 `ldac` 的“燕”）不会被等名次多段拼接靠每字
+2.0 奖励压过。候选顺序、增量/全量一致性、补充语料和提前上屏
证据应与 C# golden 分开核对。测试已包含与 Windows Core 相同的回归组：
`awmenamcunta`（买 + 椟还珠）、`uriczwxmjou`（不得瞬态提交“可佛”）、
`nuusvbbhoi`（左手匕首）和 `iejryfenahbmsp`（新人上午来面试）；另含明文数据
加载自检（数据状态、`high_freq_limit` 重建、外部码表导入、字频缺失容错）、
空码上屏开关门控、截断候选池拒绝高置信空码上屏、字符级共同前缀和提交时的
原子 composition 重建。`tools/bench_tiger_sentence_lua.lua` 用于解码性能基准。

性能基准直接调用正式模块的 `decode_full`、逐键 `decode` 和提前上屏证据路径，
不会维护另一套简化解码器。它同时报告 mean/p50/p95/max、每轮 Lua GC 增量和
实际 decode 次数。命令开头还会用 20 组未预热随机 40 码报告整串耗时和单键
P95/P99；可用 `--burst-cases N` 调整数量，或设为 0 跳过：

```bash
lua tools/bench_tiger_sentence_lua.lua . --mode mobile --repeat 50 --require-model
lua tools/bench_tiger_sentence_lua.lua . --mode none --repeat 50
```

`mobile` 要求实际加载 TCSKNM02；`auto` 接受搜索路径中第一个可用模型；`none`
显式关闭模型，用于检查降级路径。`incremental` 是普通逐键解码，`evidence` 是
每一键都构建提前上屏证据的压力测试。
