# 虎整句（Rime 独立实验版）

本方案使用纯 Lua 码表格图和本地 n-gram，不依赖 Windows Core，也不是 Windows
TSF 发布包的一部分。它仍处于实验阶段，尤其是自动提前上屏在不同 Rime 前端上的
闪烁、提交顺序和长时间稳定性需要分别验收。

Lua 5.5、Lua 5.4 和 LuaJIT 均有独立回归验证。Linux 前端如果报告
`attempt to assign to const variable 'line'`（或 `'r'`），说明仍在使用旧 Lua 文件：
Lua 5.5 将 `for` 控制变量设为只读，需更新模块中读取数据行和恢复 Tab 锁定边界
的两处循环，使用局部变量保存转换结果。`func type: nil` 是模块加载失败的后续错误。
验证命令：`python3 tools/run_regressions.py --lua /path/to/lua5.5 --negative-control`；
将解释器参数换成 Lua 5.4 或 LuaJIT，可执行同一套回归。

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

本方案没有使用 Rime 的 `table_translator`。`lua/tiger_sentence.lua` 负责输入、
整句解码、n-gram、补充语料和码表索引；`lua/tiger_sentence_learning.lua` 负责 Tab
纠正学习、内存评分索引及 Rime LevelDb 存储适配。升级时两个文件都要复制。
根目录的 `rime.lua` 只负责向 Rime 注册 processor 和 translator。

## 候选纠正自学习

`tiger_sentence/tab_learning: true` 默认开启，同时控制点选与 Tab 学习。直接点选
非首选候选提交，也会与当前首选比较并学习改变的片段；点选首选不反复强化。
Rime 的手动候选确认共用提交通知，因此键盘改选非首选后确认也适用。
Tab/Shift+Tab 改选后，空格提交，
或开启提前上屏时下一字母确认提交，才记录改变的片段；关闭提前上屏时，锁定的
纠正暂存到后续提交。普通首选、仅高亮后取消、原始编码退出不学习。手动编辑编码
会保守地丢弃尚未提交的学习记录。输出转换后的文字与原候选不一致时也不学习。

按共同编码边界提取最多 16 个 Unicode 字符，前文只取当前组合中的最后两字。
每次纠正累计相当于补充语料权重 1000，使用同一公式
`clamp(9 + 2 * ln(weight / 1000), 0, 16)`：首次 9 分、两次约 10.386 分，上限 16。
累计权重按 30 天半衰期衰减，竞争纠正仍会衰减旧偏好；单字不跨前文泛化。学习参与合法
搜索路径的保留，每个位置最多额外保留四条未完成学习路径。受学习影响的结果
不能作为概率性或空码自动上屏证据，用户主动 Tab 确认仍可提交。

偏好按 schema ID 分库存放在 Rime 用户目录的 `tiger_sentence_learning_<散列>.userdb`
中；码表、频序、白名单、高频限制和重码开关也参与模式隔离。关闭设置保留数据库。
备份或清空前先完全退出 Rime，再备份或移走对应数据库目录；不要套用 Windows
学习日志的维护工具。记录只保存在本机，不由此方案自动上传。

需要 [librime-lua 的 `LevelDb` 接口](https://github.com/hchunhui/librime-lua/blob/master/src/types_ext.cc)；接口缺失、数据库被其他进程占用或写入失败时，
输入照常工作，学习不会发布未保存的加分。最多 10,000 条、16 MiB 的有效记录数据。
纯 Lua 没有后台工作线程：普通解码仅查内存索引，数据库写入及索引更新发生在
明确提交时；空闲组合开始时按需刷新时间衰减。大量记录下的手机提交延迟仍需实测。

Rime 的 `commit_notifier`/`commit_text` 只能表示宿主提交，不能证明目标应用已经
插入文字，区别于 Windows TSF 的成功回执。合并 `rime.lua` 时使用
`tiger_sentence.processor_component` 注册 processor，以便释放提交通知连接。
Lua 5.4、LuaJIT 的测试覆盖取消、输出转换、失败写入、开关、方案隔离、持久化和
10,000 条评分索引，以及直接点选、首选不强化、重复通知和 Tab 后点选不重复计数；
这些测试不替代手机或真实应用输入验收。

真实 librime/librime-lua 回归使用生产码表和 mobile 模型，通过点选及 Tab/空格
两次纠正 `zhhbi` 为“虎娘”后应成为首选，并检查引擎重启后仍生效。
旧版 10 分封顶无法跨过“其父”与“虎娘”约 10.083 分的差距，数据库有记录也不会换首选。
现有学习记录自动按新规则重算，无需删除数据库。测试入口：
`tools/test_rime_learning_integration.py`，探针源码 `tools/rime_learning_probe.cpp`。

在有 librime 开发包和 librime-lua 的 Linux 环境，从开发仓库根目录运行：

```sh
g++ -std=c++17 tools/rime_learning_probe.cpp -lrime -ldl -o /tmp/rime-learning-probe
python3 tools/test_rime_learning_integration.py --exe /tmp/rime-learning-probe \
  --plugin /usr/lib64/rime-plugins/librime-lua.so --model /path/to/sentence-ngram-mobile.bin
```

插件路径按发行版调整；测试使用自己的临时用户目录，不修改已安装的输入法。

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
  不立即提交。Tab 高亮后继续输入字母，固定当前候选的文本及编码边界，后续组句不再改写它。
  开启自动提前上屏时，这一刻直接提交已确认部分并保留模型上下文；关闭时留在组合串中，
  回删到边界解除该次锁定。可多次锁定。数字、分号、引号仍用于当前段选重，不触发锁定。
  Up/Down 单独使用不触发继续输入锁定。清空或切换方案会丢弃锁定；回车只提交未上屏原码。
- 一码段只在整段输入只有一码时合法。
- 只有整个独立输入由单一码表边消费时，才隐式显示该编码全部名次。
- `tiger_sentence_allow_duplicate_single` 开关（默认开，即“允许单字重码组句”）
  开启时，多段整句路径中的非首选单字按语言模型分数参与竞争，可以成为可见
  首选；非首选多字词在任何切分路径中仍必须显式选重。开关关闭时，切分路径
  只取各段首选。单独输入一个完整编码的基础排序保持码表首选在前；明确的 Tab
  学习偏好可以改变合法候选的最终次序。
- 分段空格只用于显示，不进入 raw code。

码表默认过滤与 Windows Core 相同：`高频字仅使用最优码组句`
（默认前 1500 字）限制常用字只保留最优码，`整句允许全码组句白名单` 中的
字符保留完整编码。两者在运行时按 schema 的 `tiger_sentence/high_freq_limit`
和白名单 txt 现算，改文件即生效；导出器只负责把白名单初值写进 txt，
并校验 `dist_config.txt` 的 Windows 默认值与 schema 默认值一致。

自动提前上屏默认开启，基础阈值与 Windows Core 一致；Rime 的置信度池策略
自公开 PR #1 合并后更保守，见下文。开关
`tiger_sentence_early_commit`（提前上屏）同时控制概率型提前上屏与空码自动
上屏，两者没有独立开关：

- raw 长度超过四键后，按 `(文本前缀, raw 边界)` 独立累计证据；置信阈值
  `0.995`，强证据与边界封闭阈值均为 `0.99999`。
- 连续两代强证据即可确认，否则需要三代；比较型空档最多跨三代，不累计证据。
- 当前键为不完整尾码时，已计算的 dropped-tail 格点状态并入置信池做前缀
  对比，封闭边界也可计为证据。完整编码路径使用保留的完整 Beam 计算置信度，
  显示仍限前 20 项；祖先发生裁剪时向后传播标记，不以丢失概率质量的证据
  授权置信度提前上屏。这可能延后提交；Windows 仍使用其可见候选池策略。
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

光标中间插入与锁定范围内删除遵循 Rime 的原始编码字节偏移；中间编辑不累计
追加输入证据。Tab 先准备最多 20 个候选再循环高亮。模型运行时读取异常会关闭
模型、清空评分缓存和旧证据，并按无模型策略重试整次解码，不重放物理按键。
详细边界与测试见 [RIME_CORRECTNESS.md](RIME_CORRECTNESS.md)。

独立回归：`python3 tools/run_regressions.py --lua lua --negative-control`；
LuaJIT 将 `--lua` 改为 `luajit`。真实模型仍需单独运行
`lua tools/test_tiger_sentence_incremental.lua . --require-model`。

模型页和上下文查询使用有界缓存；一元概率在加载时预解包为查询表。孤立字惩罚
按需复用路径前缀结果，只处理新增文字及旧末字的邻接变化，不提前参与 Beam
剪枝。分段显示按需生成；同代候选补算提前上屏证据时复用已完成的评分与排序。
路径缓存随格图释放，不建立无限增长的历史候选缓存。

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
边 → 分数”排序。未分割的整段输入如果是某单字的最短可用编码（等长时按码表
来源顺序），该单字无论在同码中排第几都获得仅影响排序的 +5.0；显式选重不加，
也不计入置信度。候选顺序、增量/全量一致性、补充语料和提前上屏
证据应与 C# golden 分开核对。测试已包含与 Windows Core 相同的回归组：
`awmenamcunta`（买 + 椟还珠）、`uriczwxmjou`（不得瞬态提交“可佛”）、
`nuusvbbhoi`（左手匕首）和 `iejryfenahbmsp`（新人上午来面试）；另含明文数据
加载自检（数据状态、`high_freq_limit` 重建、外部码表导入、字频缺失容错）、
空码上屏开关门控、截断候选池拒绝高置信空码上屏、字符级共同前缀和提交时的
原子 composition 重建。`tools/bench_tiger_sentence_lua.lua` 用于解码性能基准。

性能基准直接调用正式模块的 `decode_full`、逐键 `decode` 和提前上屏证据路径，
不会维护另一套简化解码器。它同时报告 mean/p50/p95/max、每轮 Lua 内存净增量
（不是总分配量或 GC 停顿）和实际 decode 次数。`frontend` 检查候选补算证据的
缓存路径，`display` 额外强制生成每代候选的分段显示；均不包含真实 UI 调度。
命令开头还会用 20 组未预热随机 40 码报告整串耗时和单键
P95/P99；可用 `--burst-cases N` 调整数量，或设为 0 跳过：

```bash
lua tools/bench_tiger_sentence_lua.lua . --mode mobile --repeat 50 --require-model
lua tools/bench_tiger_sentence_lua.lua . --mode none --repeat 50
```

`mobile` 要求实际加载 TCSKNM02；`auto` 接受搜索路径中第一个可用模型；`none`
显式关闭模型，用于检查降级路径。`incremental` 是普通逐键解码，`evidence` 是
每一键都构建提前上屏证据的压力测试。
