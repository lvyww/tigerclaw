# 虎爪整句 Fcitx5 Android 移植计划

状态：阶段 4–7 代码已落地，待真机验收与正式签名发布  
编写日期：2026-08-27  
TigerClaw 源仓库：`/mnt/c/users/yc/desktop/bime_codex_src_20260513`  
Fcitx5 Android 目标仓库：`/home/yc/fx5/fcitx5-android`

## 1. 文档用途

本文是把虎爪输入法的整句功能移植到 Fcitx5 Android、并脱离 Rime 运行的实施依据。后续 agent 开始工作前，必须完整阅读：

1. TigerClaw 根目录的 `AGENTS.md`；
2. Fcitx5 Android 的 `/home/yc/fx5/fcitx5-android/HANDOFF.md`；
3. 本计划；
4. 涉及行为的现有 C# 和 Rime 源文件。

本计划不是重写语言模型或改变现有输入规则的授权。除非测试证明现有行为有错误，否则以 Windows C# Core 为规范实现，以 Rime Lua 为移动端算法和文件格式参考。

## 2. 目标与非目标

### 2.1 最终目标

在 Fcitx5 Android 中提供一个独立的“虎整句”输入法插件，满足：

- 不依赖 Rime、fcitx5-rime 或 Rime Lua；
- 使用当前 TigerClaw 码表、TCSKNM02 n-gram 模型和评分规则；
- 候选文本、顺序、分段编码和提前上屏证据与规范实现一致；
- 支持增量解码、补充语料、候选遍历和自动提前上屏；
- 以独立插件 APK 发布，不长期 fork Fcitx5 Android 主程序；
- 解码核心不依赖 Fcitx API，未来可复用于桌面 Fcitx5。

### 2.2 第一版明确不做

- 不接入 Qwen/llama.cpp；
- 不移植 Windows TSF、命名管道、Overlay 或 Dialog；
- 不实现普通虎码的全部 Windows Core 功能，只实现虎整句所需链路；
- 不修改 Fcitx5 Android 候选栏 UI，优先使用标准 `InputPanel`；
- 不把约 215 MiB 的模型提交到任何 Git 仓库；
- 不先扩展 fcitx5-lua。它目前没有完整暴露输入法注册、候选列表和预编辑维护接口，扩展成本不低于原生插件。

## 3. 已确认的技术路线

采用“独立插件 APK + 平台无关 C++ 核心”的结构：

```text
Fcitx5 Android 键盘/候选栏
        │
        ▼
fcitx5-tigerclaw 插件适配层
  - InputMethodEngine
  - 每个 InputContext 的 composition 状态
  - 按键、候选、预编辑、上屏、配置
        │
        ▼
tigerclaw_sentence_core（纯 C++）
  - 码表索引和合法切分
  - Modified Kneser-Ney 模型读取
  - 增量 Beam Search
  - 补充语料匹配
  - 候选评分/分段
  - EarlyCommitEvidence
        │
        ▼
TCSKNM02 sentence-ngram-mobile.bin
```

核心库不得包含 Fcitx、JNI 或 Android UI 类型。插件适配层只负责把 Fcitx 事件转换成核心请求，并把核心结果转换成 `Text`、`CommonCandidateList` 和 `commitString()`。

## 4. 两个仓库的职责

### 4.1 TigerClaw 仓库：规范、导出器和交叉验证

主要参考文件：

| 责任 | 规范实现/参考 |
|---|---|
| 整句格图、Beam、候选、置信度 | `next/TigerClaw.Core/SentenceInputDecoder.cs` |
| KN V2 模型评分和缓存 | `next/TigerClaw.Core/SentenceNgramModel.cs` |
| 生僻字孤立惩罚 | `next/TigerClaw.Core/SentenceIsolationPenalty.cs` |
| 补充语料匹配和奖励 | `next/TigerClaw.Core/SentenceSupplementModel.cs` |
| 字频与常用字规则 | `next/TigerClaw.Core/SentenceCharacterRanks.cs`、`SentenceCommonCharacters.cs` |
| 按键、异步代次、提前上屏状态机 | `next/TigerClaw.Core/InputMethodEngine.cs` |
| 自动化规范测试 | `next/TigerClaw.Core.Tests/Program.cs` |
| 移动模型格式生成 | `tools/convert_sentence_ngram_mobile.py` |
| Rime 码表/数据导出 | `tools/export_tiger_sentence_rime.py` |
| 移动端增量算法参考 | `rime/tiger_sentence/lua/tiger_sentence.lua` |
| TCSKNM02 分页读取参考 | `rime/tiger_sentence/lua/tiger_sentence_kn.lua` |
| 补充语料参考 | `rime/tiger_sentence/lua/tiger_sentence_supplement.lua` |
| Rime 行为说明和部署规则 | `rime/tiger_sentence/README.md` |

若 C# 与 Rime 的行为有分歧，先用 TigerClaw 的测试和 `AGENTS.md` 判断；无法判断时记录差异，不得凭感觉选择。

### 4.2 Fcitx5 Android 仓库：插件和 Android 产物

在 `/home/yc/fx5/fcitx5-android` 中新增 `plugin/tigerclaw`。插件骨架优先仿照 `plugin/sayura`，数据量较大时同时参考 `plugin/rime`。需要修改：

- `settings.gradle.kts`：加入 `include(":plugin:tigerclaw")`；
- `plugin/tigerclaw/build.gradle.kts`；
- Android manifest、`res/xml/plugin.xml`、字符串和图标；
- CMake 工程、Fcitx addon/input-method 描述文件；
- C++ 核心、Fcitx 适配层及测试；
- 必要的数据描述文件生成和打包配置。

不要为第一版修改 `app/src/main/cpp/androidfrontend`。当前前端已经支持候选选择、预编辑回调和文本提交。

## 5. 建议的目标目录

目标仓库中建议形成：

```text
plugin/tigerclaw/
├── build.gradle.kts
├── src/main/AndroidManifest.xml
├── src/main/res/xml/plugin.xml
├── src/main/res/values*/strings.xml
└── src/main/cpp/
    ├── CMakeLists.txt
    └── fcitx5-tigerclaw/
        ├── CMakeLists.txt
        ├── LICENSES/
        ├── data/
        │   ├── tigerclaw-addon.conf.in.in
        │   ├── tigerclaw.conf.in
        │   └── CMakeLists.txt
        ├── src/
        │   ├── tigerclaw.cpp
        │   ├── tigerclaw.h
        │   ├── tigerclawstate.cpp
        │   └── tigerclawstate.h
        ├── core/
        │   ├── include/tigerclaw_sentence/*.h
        │   ├── src/*.cpp
        │   └── tests/*.cpp
        └── resources/
            ├── tiger_sentence.lexicon.*
            ├── sentence_char_ranks.*
            └── tiger_sentence.supplement.txt
```

实际文件名可随 Fcitx 工程惯例调整，但 `core/` 与 Fcitx 适配层必须保持依赖单向：适配层依赖核心，核心不依赖适配层。

## 6. 必须保持的行为不变量

### 6.1 原始编码与显示编码

- composition 的权威状态始终是完整 raw code；
- 候选分段中的空格只用于显示，绝不写回 raw code；
- 修改、退格和原码上屏都基于 raw code；
- 每个候选携带 text/raw 边界，不能靠递归重新解码推断提前提交边界；
- 部分提交后，语言模型仍以“已提交前缀 + 活跃尾部”作为上下文；128 键上限只约束活跃尾部。

### 6.2 合法切分和选重

- 整段只有一个编码键时，允许一码段；
- 其他段至少消耗两个键，可包含选重后缀；
- `;` 选择第 2 项、`'` 选择第 3 项，数字选择显式名次，`0` 表示第 10 项；
- 分号和单引号只有对应设置开启时才作为选择符进入编码；
- 整段不超过 4 个编码键时，裸码允许检索该码全部词条，但首选路径必须排在后续名次之前，后续名次保持码表顺序；
- 更长输入的裸码只能取首选，非首选必须显式选择；
- 多字词是合法格图边；
- 高频前 1500 单字只保留最短首选码；较生僻单字可保留非主码；
- 合法整段路径不能因局部启发式被跳过，例如 `gyfs2` 必须把整个编码作为合法切分尝试。

### 6.3 评分和候选

- Beam 扩展时每输出一个 Unicode 字符奖励 `2.0`；
- 使用完整的 Modified Kneser-Ney V2 概率，不降级到旧 compact 模型；
- EOS 只在生成最终候选时计算，不写回可复用 Beam；
- 生僻字排名大于 3000 时按现有规则应用常数 2 的孤立惩罚，左右任一真实观测二元组可取消对应孤立条件；
- 补充语料奖励为 `clamp(9.0 + 2.0 * ln(weight / 1000), 0, 16)`；
- 同一结束位置的重叠补充条目只取最大奖励，一字条目每次出现都可匹配；
- 补充奖励参与 Beam 剪枝、n-gram 排序和保留的基础分，但不进入置信度质量；
- 同文本不同路径要合并置信度质量；
- 可见候选使用确定性精确 Top-K；同分 tie-break 必须固定，不得依赖 `unordered_map` 遍历顺序；
- 默认显示 20 个候选，内部最终置信度 Beam 保留 200 状态；如果参与置信度的 Beam 被截断，该代不得触发提前上屏。

### 6.4 增量解码和缓存

- 追加输入只扩展跨越旧 raw 末端的新边；
- 退格复用仍有效的前缀格图；
- 一键整段规则发生变化时允许全量重建；
- 旧路径不能被重新打分或重复计入置信度；
- 高歧义目的桶可自适应合并同文本；
- 已处理桶冻结为紧凑列表，未变化的冻结源桶应直接复用；
- Beam 状态保存 raw/text 边界，不逐层拼接分段编码；只为最终可见候选重建分段；
- KN transition、观测二元组、词条选重过滤、UTF-8 拆分和分页位置缓存必须有固定上限；
- 空补充语料必须走整体快速路径，不在字符循环里反复判断或查询。

### 6.5 自动提前上屏

- 默认先在配置中关闭，功能对齐并完成真机验证后再讨论默认值；
- raw 长度 `<= 4` 时不提交、也不积累稳定证据；
- 使用三个严格连续、每次只新增一键的精确代；
- 每代取置信度质量达到 `0.995` 的提案，三代取 Unicode 文本元素最长公共前缀；
- 公共前缀必须在三代对应相同 raw 边界；
- 回退、缺代、空提案和手动遍历候选会失效或暂停证据；
- 每次可以提交一个或多个新字符，但距离上次部分提交至少新增 3 个活跃 raw 键；
- 必须保留全部不稳定后缀，且至少保留当前候选最后一个字符；
- 置信度要纳入“完成路径 + 合法但未完成的尾码前缀”，这些 shadow hypothesis 不得出现在候选栏或改变普通排序；
- 若置信度优胜者是 Qwen 未见过的不完整尾部，则将来即使加入 Qwen 也不能错误约束该证据；
- 提交后推进代次，迟到结果不得改写已经提交的 composition；
- 候选、补全和尾部提交都必须保留已提交文本前缀。

Fcitx 插件可直接执行 `InputContext::commitString(stablePrefix)`，同时保留 state 中的活跃尾部，无需模仿 Rime 的 `clear`/`push_input` 重建方法。

## 7. 每个 InputContext 的状态设计

使用 `InputContextProperty`，每个输入上下文至少保存：

```text
rawCode
committedText
committedRawLength
visibleCandidates[]
selectedCandidateIndex
lastVisibleSegmentation
decoder incremental lattice/cache handle
currentGeneration
lastAcceptedGeneration
three generations of EarlyCommitEvidence
rawKeysSinceLastEarlyCommit
manualNavigationSuspended
decodePending / lifecycle token（异步阶段才需要）
```

模型、只读码表索引和全局只读排名表由 addon 实例共享；composition、选择位置、增量格图和提前上屏证据必须按 `InputContext` 隔离。

`reset`、`deactivate`、焦点切换和输入上下文销毁时必须取消/失效异步结果并清理当前 composition。已经通过 `commitString()` 提交的前缀不能尝试撤销。

## 8. Fcitx 按键与 UI 映射

### 8.1 按键

第一版至少实现：

| 输入 | 行为 |
|---|---|
| 字母 | 追加到 raw code 并解码 |
| 已启用的 `;`、`'`、数字 | 作为显式选重后缀追加 |
| Backspace | 删除一个 raw 键并重新发布状态；空码时不接管 |
| Space | 上屏当前选中的整句候选 |
| Enter | 上屏原始编码 |
| Esc | 清空 composition |
| Up/Down | 原地移动可见候选选中项，不重排 |
| Tab/Shift+Tab | 有候选时遍历候选，优先级高于 Tab 清码 |
| Ctrl+数字 | 留给目标应用，不作为整句快捷键 |
| 候选栏点击 | 调用对应 `CandidateWord::select()`，上屏该候选 |

其他标点的“先提交候选再传入标点”行为以 `InputMethodEngine.cs` 和现有测试为准，不能只测试字母和空格。

### 8.2 UI 更新

每次可见状态变化后：

1. `inputPanel.reset()`；
2. 设置 client preedit；若客户端不支持，再设置普通 preedit；
3. 设置 `CommonCandidateList`、页大小和光标；
4. 调用 `updatePreedit()`；
5. 调用 `updateUserInterface(UserInterfaceComponent::InputPanel)`。

预编辑使用候选对应的分段编码；没有新结果但后台解码尚未完成时，保留上一代候选，显示编码沿用上一分段并只追加或裁剪实时 raw 尾部。第一键 pending 且没有旧候选可保留时，不显示空候选窗。

## 9. 模型、码表和补充语料

### 9.1 固定数据

移动端使用仓库外模型：

```text
/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

当前约 215 MiB，格式为无损重排的 `TCSKNM02`。它保留原始 float32 概率，不能重新量化。开发机找不到模型时应给出明确错误，不能悄悄打包空模型。

建议由 TigerClaw 导出脚本产生确定性的插件数据目录，至少包含：

- 码表索引；
- 字频/常用字信息；
- 默认补充语料；
- 数据格式版本和生成源 commit；
- 模型预期大小、magic 和 SHA-256 清单。

生成物中的小型码表/排名可以进入插件仓库；大模型只能作为构建时外部输入或用户导入文件。

### 9.2 模型读取

优先实现 TCSKNM02 原生读取，不先实现 TCSKNM01。可比较两种无损方式：

1. 只读 `mmap` 整个文件，依赖操作系统按需分页；
2. 稀疏常驻索引 + 有界上下文页 LRU。

两者的候选和分数必须完全相同。用真机的 RSS、缺页、冷启动和 P95 延迟决定最终方案。不要因“映射 215 MiB 虚拟地址”就把它等同于 215 MiB 常驻内存，也不要只看虚拟内存而忽略实际 RSS。

### 9.3 安装布局

原型阶段允许把模型作为插件 asset，通过 Fcitx5 Android 数据描述机制安装到 `usr/share/fcitx5/tigerclaw/`。这样最容易验证，但 APK 压缩副本和解压后的模型可能合计占用 300 MiB 以上。

正式版本优先采用：

- 插件 APK 内只含程序、小型码表和默认补充语料；
- 用户导入或单独下载模型到 Fcitx 用户数据目录；
- C++ 使用 Fcitx `StandardPath` 定位文件，不硬编码 `/sdcard`；
- 插件更新不重复复制模型。

模型导入 UI 属于发布阶段，不阻塞本地原型。

### 9.4 补充语料

格式保持 UTF-8：

```text
# 注释
词条 [权重]
茧师 1000
盗洞 5000
```

空白为一个或多个空格或制表符；权重省略时为 1000；空行和 `#` 注释忽略；重复词条最后一项生效。建议支持系统默认文件与用户文件叠加，用户同名条目覆盖默认项。用户文件放在 Fcitx 用户数据目录的 TigerClaw 子目录中，并提供显式重载或在输入法重新激活时检查变更。不要每次按键读取文件。

## 10. 分阶段实施与验收

每一阶段完成后都要单独提交。后续阶段不得掩盖前一阶段未通过的测试。

### 阶段 0：冻结基线和黄金数据

任务：

- 记录 TigerClaw、Fcitx5 Android 及相关 submodule 的 commit；
- 记录 TCSKNM02 模型的大小和 SHA-256，不提交模型；
- 为 C# Core 增加或复用一个 JSONL 黄金数据导出入口；
- 每条样例逐键导出 raw、候选文本/顺序/分数、分段、边界、置信度提案、截断标志；
- 同时记录补充语料配置、Beam 宽度和模型 hash；
- 生成 append、backspace、候选遍历、选重和部分提交序列，不只导出最终句子。

黄金数据建议只提交规模较小、人工可审核的集合，不包含模型内容。

验收：

- 相同 C# 版本重复导出字节一致；
- `Decode` 与 `DecodeFull` 在所有增量样例上候选、分数和置信度质量一致；
- 现有 `TigerClaw.Core.Tests` 全部通过。

### 阶段 1：插件空壳和输入上下文

任务：

- 新建 `plugin/tigerclaw` 并加入 Gradle settings；
- 注册 addon 和“虎整句”输入法；
- 建立 `InputContextProperty`；
- 暂用固定候选验证字母、退格、清码、候选点击、Space/Enter 上屏；
- 验证安装独立插件 APK 后主程序能发现和启用它。

验收：

- 不修改主 app 即可加载插件；
- 两个输入上下文状态不串线；
- reset/deactivate 后无残留 preedit 或候选；
- 候选栏点击与物理/软键盘按键都能工作。

### 阶段 2：平台无关模型和码表核心

任务：

- 实现 UTF-8/code-point 工具并明确“文本元素”口径；
- 实现码表载入、最优码筛选、全部名次及 prefix 查询；
- 实现 TCSKNM02 文件校验、稀疏索引和 KN 查询；
- 实现精确 transition cache 和 observed-bigram cache；
- 实现补充语料 Aho-Corasick 或等价有界匹配器；
- 构建不依赖 Android/Fcitx 的命令行测试程序。

验收：

- KN 已知/未知转移、backoff 和观测二元组与 C# 结果误差不超过 `1e-6`；
- 坏 magic、截断文件、错误版本和模型缺失均明确失败；
- 补充语料重叠、单字重复、默认权重和覆盖规则通过测试；
- 无补充语料时确认走空匹配器快速路径。

### 阶段 3：完整 n-gram 解码与一致性

任务：

- 实现合法格图、Beam、同文本质量聚合和精确 Top-K；
- 实现长度奖励、EOS、孤立惩罚和补充语料奖励；
- 实现 raw/text 边界和最终分段重建；
- 实现增量追加、退格复用和必要的全量重建；
- 实现 EarlyCommitEvidence 计算，但暂不实际提交文本。

验收：

- 黄金集可见候选文本和顺序 100% 一致；
- 分段和 raw/text 边界 100% 一致；
- 分数在约定浮点误差内一致；
- 置信度提案、边界和截断资格 100% 一致；
- 全量与增量解码结果一致；
- tie-break 在不同运行和优化级别下稳定。

必须加入历史回归：

- `gyfs2` 会尝试合法整段路径，并能产生“堽”；
- `pzjn2` 保留对应的后选路径；
- `auweakmf` 配合“盗洞”补充语料时，不能因为早期局部候选而丢失“那盗洞”路径；
- 为“那依你之见”“今天你们怎么样”“这上面还好”从实际码表生成编码并测试候选/提前上屏行为；
- 短码全选重、一码整段、非法嵌入一码、单引号/分号/数字选择符均有独立用例。

### 阶段 4：接入 Fcitx 候选与提交

任务：

- 用真实核心替换固定候选；
- 完成所有按键映射、候选遍历、点击选择和分段 preedit；
- 处理客户端不支持 preedit 的降级；
- 普通解码先使用同步方式，加入每键耗时统计但默认不刷日志；
- 模型错误时插件保持可重置，不令 Fcitx 主进程崩溃。

验收：

- 真机能连续输入、选择、退格和切换应用；
- 结果与同一黄金样例一致；
- 输入法停用/应用销毁时无悬挂引用；
- 候选窗不会因 pending 或 UI 重置发生无意义闪烁。

### 阶段 5：性能和异步决策

在用户当前的 Snapdragon 8 Gen 2/SM8550 ARM64 设备上测量：

- 冷启动首次解码；
- 热态逐键 append 的 P50/P95/P99；
- 退格、长句、高歧义短码和 128 键尾部；
- 模型 mmap/索引/LRU 的实际 RSS 和 page fault；
- 连续输入 10 分钟后的缓存大小和内存是否继续增长。

建议性能目标（属于验收目标，不是当前实测值）：

- 热态 append P50 不高于 8 ms；
- 热态 append P95 不高于 20 ms；
- 正常样例不出现超过 50 ms 的连续阻塞；
- 固定上限缓存长期运行后不增长；
- 核心额外常驻 RSS 尽量控制在 64 MiB 内，模型文件页单独报告。

如果同步解码不能满足目标，再实现 latest-generation-only 后台解码：

- 工作线程只计算，不直接操作 `InputContext` 或 UI；
- 结果通过 Fcitx 事件循环回到所属上下文；
- 每个请求携带 generation 和生命周期 token；
- 只接收仍为最新 generation 且上下文仍存活的结果；
- pending 时保留旧候选和分段；
- Space、候选点击等确定性动作必须确保最新结果，或采用明确、可测试的同步补全；
- 禁止多个线程同时修改同一个增量 decoder；模型只读部分可以共享。

不得在没有基准数据时先引入线程池。多线程调度本身可能增加按键尾延迟和状态复杂度。

### 阶段 6：实际启用自动提前上屏

任务：

- 在适配层实现三代证据、冷却、手动遍历暂停和 partial commit；
- 自动上屏的判断只消费已完成结果，不在按键路径同步补算完整 Beam；
- 部分提交后立即更新 live tail、候选过滤和 preedit；
- 测试连续快速键入时 commit 与下一按键的顺序；
- 对比开启/关闭功能的候选结果，确认功能只改变提交时机，不改变解码排序。

验收：

- 所有 C# 自动上屏状态机测试有 C++ 对应用例；
- “那盗洞”类样例不会因过早提交“那次”封死合法后续；
- “今天你们怎么样”“这上面还好”能按规范积累公共稳定前缀；
- 快速键入、退格、候选点击、应用切换时无重复提交、漏键或崩溃；
- 用户关闭提前上屏后，不保留旧的稳定证据。

### 阶段 7：配置、模型分发和发布

任务：

- 通过 Fcitx addon 配置暴露提前上屏、选择符开关等必要设置；
- 实现模型存在性、版本和 hash 检查；
- 决定“大 APK”与“模型导入/下载”的正式分发方式；
- 提供补充语料位置、格式、重载方式和示例；
- 完成许可证、第三方声明、图标、版本和发布说明；
- 用稳定签名生成独立 arm64-v8a 插件 APK。

验收：

- 从干净安装开始的完整步骤可复现；
- 插件升级不删除用户模型、配置和补充语料；
- 模型缺失/损坏时提示清楚且不会崩溃；
- 发布包不含签名密码、私钥、绝对开发机路径或调试日志；
- 插件与目标 Fcitx5 Android 版本/ABI 的兼容范围有明确记录。

## 11. 测试矩阵

至少覆盖：

| 分类 | 样例 |
|---|---|
| 解码 | 单字、词条、多段、整段词、同码不同长度、无候选 |
| 选重 | `;`、`'`、`0`、`2`～`9`、开关禁用、Ctrl+数字 |
| 短码 | 一码整段、嵌入一码非法、4 键全名次、5 键规则切换 |
| 编辑 | 连续 append、逐键 backspace、中间规则重建、Esc、Enter |
| UI | client preedit、普通 preedit、候选点击、翻页、横竖屏、切应用 |
| 评分 | 长度奖励、EOS、未见 n-gram、孤立字、观测二元组 |
| 补充语料 | 缺失、空文件、注释、默认权重、极值、重复、重叠、单字重复 |
| 提前上屏 | 三代连续、缺代、回退、边界变化、候选遍历暂停、冷却 |
| 生命周期 | 输入法切换、InputContext 销毁、后台结果迟到、应用强退 |
| 性能 | 冷/热模型、长句、高歧义、连续 10 分钟、低内存恢复 |
| 错误处理 | 模型缺失/截断/版本错、码表坏行、配置损坏 |

至少保留三层测试：

1. 纯 C++ 单元测试，不需要模型或 Android；
2. 使用真实模型的离线一致性/性能测试；
3. Fcitx5 Android 真机集成测试。

## 12. 构建和验证约束

Fcitx5 Android 的 ARM64 WSL 构建必须遵循其 `HANDOFF.md`：使用 JDK 25、Android SDK/NDK 28.2.13676358，并通过 `QEMU_LD_PREFIX` 运行 x86_64 构建工具。不要改用 Box64，也不要在日志或提交中暴露签名秘密。

典型插件任务应为：

```bash
cd /home/yc/fx5/fcitx5-android
./gradlew :plugin:tigerclaw:assembleDebug
./gradlew :plugin:tigerclaw:assembleRelease
```

实际执行前仍应复制 `HANDOFF.md` 中完整的环境变量和 QEMU 配置。若 Gradle task 名称与工程约定不同，以 `./gradlew :plugin:tigerclaw:tasks` 的结果为准。

每次提交前至少记录：

- 构建命令和结果；
- 纯 C++ 测试结果；
- 黄金数据比较结果；
- 是否使用真实 TCSKNM02；
- 真机型号、Android/Fcitx 版本和关键性能数据；
- 尚未覆盖的功能和已知差异。

## 13. 提交和协作规则

- 两个仓库分别提交，禁止一个提交混入另一仓库的生成物；
- 开始阶段前检查 `git status`，不得回退用户或其他 agent 的无关修改；
- 每个阶段使用小而可验证的提交，提交信息注明阶段；
- 大模型、APK、Gradle 缓存、CMake 产物和性能原始大日志不得进 Git；
- 新增数据格式必须写 magic、版本、端序和边界检查；
- 任何为了性能而改变候选结果的优化都不算“无损”，必须单独提出并取得同意；
- 若发现规范实现本身的缺陷，先在 TigerClaw 仓库建立复现测试和修复，再更新黄金数据，不能只在 Android 端打补丁；
- 变更本计划中的行为不变量时，同步更新 TigerClaw `AGENTS.md`。

## 14. 风险清单

| 风险 | 控制措施 |
|---|---|
| C#/Lua/C++ 浮点和同分顺序不同 | 黄金数据、固定 tie-break、分数误差与顺序分别验收 |
| Unicode 边界口径不一致 | 独立测试 BMP、扩展字符、组合字符；边界统一用明确定义的文本元素 |
| 模型导致 APK/磁盘过大 | 原型可内置，正式版改为导入/下载且升级保留 |
| mmap 看似省内存但真机缺页抖动 | 同时记录 RSS、major/minor fault、冷/热延迟 |
| 同步解码阻塞 Fcitx 事件线程 | 先测量，超标后才加入 latest-generation 异步方案 |
| 异步结果访问已销毁 InputContext | generation + 生命周期 token + 事件循环回送 |
| 提前上屏造成漏键/封死后续 | 三代/边界/冷却测试，功能初期默认关闭 |
| 插件 ABI 与主程序不匹配 | 从同一 clone/submodule 构建，发布时记录兼容版本 |
| 用户补充语料被插件升级覆盖 | 系统默认与用户文件分离，用户数据不放安装资源目录 |
| GPL/第三方许可证遗漏 | 复用插件模板的许可证流程，发布前逐项审核链接依赖 |

## 15. 完成定义

只有同时满足以下条件，才能宣布“Fcitx5 Android 整句移植完成”：

- 独立插件 APK 可在目标 Fcitx5 Android 安装、发现和启用；
- 运行时不加载 Rime 或 TigerClaw Lua；
- 全部 C++ 单元测试和黄金一致性测试通过；
- 规范样例候选文本/顺序、分段、补充语料和提前上屏行为一致；
- 真机连续输入无崩溃、重复上屏、漏键或明显候选闪烁；
- 性能与内存达到“阶段 5：性能和异步决策”的目标，或有经确认的实测例外；
- 模型、配置和补充语料的安装/升级策略可复现；
- 文档记录构建环境、兼容版本、已知限制和发布物位置；
- Qwen 明确标记为未包含，而不是静默声称与 Windows 完全等价。

## 16. 后续 agent 的第一批动作

接手后按以下顺序开始，不要直接写 Beam Search：

1. 检查两个仓库的 `git status`，记录已有修改；
2. 记录两个主仓库、Fcitx5 submodule 和模型 hash；
3. 运行现有 TigerClaw Core Tests；
4. 设计并提交 JSONL 黄金快照格式和最小导出器；
5. 在 Fcitx5 Android 仓库建立 `plugin/tigerclaw` 空壳；
6. 用固定候选验证 addon、InputContext、候选点击和 commit 链路；
7. 通过阶段 1 验收后，再开始 TCSKNM02 与解码核心。

这套顺序的目的，是先固定正确性标准并打通平台接口，让后续算法移植可以在普通主机上快速验证，而不是每次依赖完整 APK 和人工观察判断对错。
