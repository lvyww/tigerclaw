# C++ Core — mainline Windows runtime

## 主线切换（2026-10-08）

用户指定 C++ Core 为主线。x64/ARM64 整包发布、`next/build_next.bat` 调试入口
和 `publish_core_arm64.bat` 均已默认使用本实现。C# 保留为历史及差分测试参考。
发布仍使用标准 `TigerClaw.Core.exe` 与原协议；全拼独立仓库不受此次切换影响。
此次为源码主线切换，未替换日用目录。
共用构建入口 `tools/publish_cpp_core.ps1 -BuildOnly -Architecture x64|ARM64`
支持 `-Configuration Release|Debug` 与 `-OutputDirectory`，整包脚本继续独立暂存并校验
五阶模型。每次 Core 构建均运行 CTest、PE 架构与生产身份检查后才复制产物。

## 移除系统 ICU 依赖（2026-10-08，用户要求）

参考本机 `C:\Users\yc\Desktop\ime\native\LexiconOrder.cpp` 和 `DynamicText.cpp`
（虎娘/Tigirl）的 Windows NLS 路线。Core 不再包含 `icu.h` 或链接 `icu.lib`，也不通过
动态加载保留 ICU 后备路径。星期名称改用 GetLocaleInfoEx；空 locale 保持 invariant
英文全称，省略 locale 使用当前用户区域，星期索引按 Sunday=0 正确换算。

文件排序改为 LCMapStringEx(LCMAP_SORTKEY | NORM_LINGUISTIC_CASING) 生成一次排序键，
再按字节稳定排序，保留方案同名文件优先及相等项 TXT/YAML 原枚举顺序。最初参考实现
直接调用 CompareStringEx，但在本机发现 `ä甲ーcσäác.txt` 与 `a甲甲cＡＡＡb.txt` 正反
比较都返回“大于”，违反排序要求。因此采用 NLS 排序键，C# 测试端用 NLS GetSortKey /
SortKey.Compare 独立验证。不能把这一结果描述为任意文件名下与 .NET CompareString
或旧 ICU 完全同序。

文化语义现明确为 **Windows NLS**：240 个真实目录用例对照 C# NLS 排序键，210 个
星期名称对照 C# NLS 数据。测试先核实实际 UseNls；不会修改 C# 主线生产配置。
`tools/test_core_native_order.py`、`test_core_native_weekdays.py` 默认 `--globalization nls`；
保留 `--globalization icu` 供历史差异检查。协议工具需显式加 `--globalization nls`，
通过临时 runtimeconfig 设置测试子进程，避免 WSL 环境传递的不确定性。

本机日用 8 个方案只读核对：当前“虎整句B”等 6 个方案文件顺序不变；“虎码单字”与
“虎码字词”的 Latin 文件名相对中文符号文件顺序改变，详细前后名单保留在证据中。
多文件方案的同码合并次序可能受此影响。旧 ICU 基线的 273 条协议轨迹有 7 处 UI 注释
差异；改用明确的 NLS 基线后两架构均零差异。此文化后端变化是移除 ICU 的行为边界。

证据：`../_run/CoreNative/no-icu-20261008/validation.json`。Windows CTest 现为 17 项，
其中 `core_native_no_icu` 检查实际生产 exe 导入表；根发布脚本在此检查失败时停止，
防止重新带入 ICU。保留中间排序调查和失败日志。新 ARM64/x64 构建无需系统 ICU，
但移除这项 Windows 10 1903 限制不代表已完成 Win7/8.1 或旧 Win10 的整包兼容验收。
既有现代工具链/API、TSF、Overlay/Dialog 的要求仍需分别核对。本轮未替换日用目录。

## UI 重复发布优化与性能探针（2026-10-08）

隔离 ARM64 真实三源 Q8/虎整句表测量发现：空闲和停留候选的稳定状态，原宿主每 3 秒
仍发布约 96 次相同 UI。RuntimeUiPublisher 现在对完整 JSON 去重；有变化才更新 v1/v2
及 Changed 事件。20ms 异步结果轮询、JSON 构建和独立 5 秒心跳保持原样，不修改 Beam、
排序、学习或提前上屏阈值。收益是减少共享内存写入和前端重复解析/刷新，不能据此声称
解码更快或整机功耗降低。

v2 读锁忙时仍更新 v1，并保留最新 payload 等待下一次轮询补发；补发沿用原始序号和
时间戳，使 native Overlay 的 v1/v2 身份检查保持一致。新增测试覆盖重复去重、锁争用、
连续状态覆盖、同一 payload 重试与无效消息。原锁争用回归改为使用真实变化的状态，
不再要求相同状态每次递增序号。Windows CTest 现为 16 项。

性能探针（已有专用隔离 root，神经重排/学习/提前上屏关闭）：

```text
dotnet TigerClaw.Core.Tests.dll --native-performance-probe <native-exe> <isolated-root> <output.json>
```

记录启动、空闲和候选停留时的 Core CPU/内存/UI 发布次数，以及 10 次预热后的 60 组
固定 tuja/tlleo/tujatuja 管道按键/空格往返。空格同步等待当前解码，但这些重复短句不代表
冷模型、长句、神经重排或物理应用输入。3 秒 CPU 采样受 Windows 时间粒度限制，不能
把 0ms 解读为无 CPU 开销。按键时延包括托管客户端、调度和 IPC，不是纯算法耗时。

前后各 3 次交替运行，保留每次结果和全部提交输出，见
`../_run/CoreNative/performance-20261008/validation.json`。前端恢复、真实学习回执、tuja
前缀显示及协议差分回归另行通过；性能采样期间不同时运行构建或其他回归。
本轮仅构建，没有替换日用目录。仍可进一步测量长句热点，以及用事件驱动替代空闲时
重复 JSON 构建，但需先覆盖异步结果、光标延时和动态候选的失效边界。

## 已上屏前缀的候选显示修复（2026-10-08）

用户报告 `tuja` 提前上屏“我”后，候选仍显示“我们”，空格却只提交“们”。
旧 ARM64 标准产物配真实三源 Q8/虎整句表已复现：剩余编码 `ja`，候选仍为完整文本。
原因是 RuntimeInput 的候选页直接使用解码候选，遗漏 C# GetPublishedSentenceCandidates
对已提交前缀的裁剪。现在发布当前或等待中候选时均只显示未上屏后缀，跳过不匹配/空
后缀；解码候选、选择和学习仍保留完整句子。页数按投影结果计算。

新增 pending/current 投影回归及真实管道 `tuja` 回归：提前输出“我”、候选显示“们”、
空格只输出“们”。证据位于 `../_run/CoreNative/prefix-ui-20261008/`，含修复前失败日志。
修复产物通过根目录 `publish_arm64_cpp_core.bat` 部署；本轮只构建验证，不替换日用目录。

## ARM64 平替入口与发布（2026-10-08）

新增生产目标 `core_native_runtime`，生成 **`TigerClaw.Core.exe`**，使用 Windows GUI
子系统和共享 BuildInfo 的版本资源。原 `TigerClaw.Core.Native.Experimental.exe` 保留
作为隔离测试目标。以下内容取代旧阶段“只有测试入口”的限制。

在仓库根目录运行：

```bat
publish_arm64_cpp_core.bat -BuildOnly
publish_arm64_cpp_core.bat
```

第一条只构建 ARM64、运行 17 项 CTest 并检查产物身份；第二条在同样检查通过后，
平替 `release_arm64\TigerClaw.Core.exe`。需要已安装的 Visual Studio C++ ARM64 工具链
和可运行 ARM64 程序的 Windows。已有 release_arm64 资源必须完整；脚本不重新打包模型。
`-Jobs 2` 为默认编译并发；`-Destination "其他完整运行目录"` 可指定目标；`-NoRestart`
替换后保持停止。正常发布保留先前的运行/停止状态和 `--without-overlay` 选择。

发布先创建 `backup-before-cpp-core-时间-唯一后缀`，核对旧 Core 的 SHA256，再停止
**目标完整路径**的进程、原子替换单个 exe。管道退出命令发送前核对 hello 的 core_path；
不按进程名批量杀进程。配置、码表、学习日志、模型、TSF、Hook、Overlay、Dialog 和
共享 BuildInfo 均不由脚本改写。重启失败自动恢复备份；`publish.json` 记录状态和新旧
哈希。备份可用于手工恢复。发布 C++ Core 不等于迁移其他组件。

兼容入口及行为：

- 默认启动、`--autorun`、`--silent`、`--with-overlay`、`--without-overlay` 与 C# 一致，
  忽略未知启动参数；诊断入口单独处理。无标准输入或 stdin EOF 不会退出生产宿主。
- 生产管道 `BimeIPC`，UI `Local\TigerClaw.UiState.v1`（含既有 v2 派生通道）、Core/
  Overlay 心跳、菜单事件、Sentence 管道和 Core 单实例互斥锁均沿用主线名称。
- 在交互式 WinSta0/Default 桌面才允许启动；资源从自身 exe 目录加载。无 TSF 注册时
  使用已有 `TigerClaw.exe` Hook；二者都缺失则提示安装。自动启动注册随配置同步。
  Overlay、Dialog 和 Sentence 使用原发布文件名，生产子进程不继承测试端点变量。
- 同目录同会话 Core 可重启，已有 Overlay 可接管。为避免影响另一安装，进程接管/停止
  额外核对完整路径和会话；不同安装占用主单实例锁时退出。这一进程范围有意比 C# 的
  按名称清理更窄。生产管道占用检测先于 UI MMF 写入。
- hello 使用主线构建字段；字段名大小写、数值/布尔转换、按键别名、形码拼音管理响应、
  候选背景清理与重载失败时清除组合行为已对齐。底层失败重载仍保留旧的完整资源快照，
  不发布半加载状态。

证据：`../_run/CoreNative/drop-in-20261008/validation.json`。ARM64/x64 各 15 项 CTest、
273 条协议/UI/命令轨迹差分为零；Linux 10 项测试通过。标准 ARM64 产物通过断开 stdin
后的管道/UI/重放/退出、真实 Q8 学习回执/重连/取消、实际 Dialog/native Overlay 启动与
异常恢复。批处理在含中文/空格的临时目录完成备份替换，其他文件哈希不变；启动失败
回滚用隔离故障注入验证（模拟进程 API，不占用生产管道）。初次 PowerShell 空参数失败
及修复后的日志均保留。

本轮未执行日用 `release_arm64` 替换或重启。真实 TSF/Hook 应用输入、跨完整性窗口及
长时间资源/延迟尚待实际使用验收；这些定向回归不是穷尽行为证明，也不包含另仓全拼
引擎迁移。`--capabilities` 保留 `production_ready=false` 表示上述验收尚未完成，
`production_entrypoint=true` 表示标准平替入口已实现。Rime 暂停任务保持暂停。

## 前端宿主、候选帧和真实整句回执（2026-10-08，补齐后续差异）

- 新增 `--serve-isolated-ui <root> <TigerClaw.Core.Native.Test.*>`。明确目录中需放入
  **本轮重新构建**的 `TigerClaw.Dialog.exe`、`TigerClaw.Shared.dll` 和
  `TigerClaw.Overlay.exe` 及它们的运行资源。默认 C++ Overlay 和 WPF 回退版均可用。
  宿主启动 Overlay，处理设置/加词/菜单命令；重复设置命令复用窗口，切换到加词时
  只替换自己持有的 Dialog。Windows Job 和保留进程句柄保证宿主退出/崩溃不会遗留
  自己的前端，也不会按进程名接管或终止日用进程。
  Overlay 心跳监护同步 3 秒检查、6 秒存活窗及 10～120 秒递增重试；异常退出后只
  重启自己的 Overlay，回归会终止测试子进程验证此路径。
- 子进程专用 `TIGERCLAW_TEST_PIPE` 同时指定管道、UI、心跳和菜单事件；格式错误
  拒绝启动，不回退到日用管道。Dialog/WPF 的测试实例跳过生产注册检查和按名称杀
  进程的单实例逻辑；C++ Overlay 使用独立实例互斥锁和测试窗口名。没有环境变量时
  保持原有生产入口。不要混入不支持该变量的旧前端文件。
- 有界管道增加与 C# `BuildPipeSecurity` 对齐的当前用户、System、Authenticated
  Users、Builtin Users 和两个 AppPackage SID 的读写/建实例 ACL，仍拒绝远程客户端。
  回归实际读取 DACL 核对权限；这不是低完整性/AppContainer 实际输入验收。
- 候选帧差分由随机标识“是否存在”改为比较完整轨迹中的标识相等关系；补齐退格、
  取消、语言/方案切换、配置重载、Hook 禁用、大小写/反查模式与新整句/混输的失效边界。
  加词窗口启动失败仍保留已执行按键的缓存响应，避免重试再次处理按键；独立窗口命令
  在无界面模式明确返回失败。有界面宿主也接入官方链接/导出文件的 Shell 打开回调。
- 新增 `--native-sentence-host-probe <native-exe> <new-root> <model> <shape-table>`：
  真实三源 Q8 + 虎整句表，异步宿主经实际管道输入、Tab 选重、空格提交、断线重试、
  确认回执和隔离日志落盘；错误客户端、失败确认、重复确认、焦点取消均不能多学。
  验证取消后的异步结果不会复活旧组合，并采样 200 次重连的句柄/私有内存增量。
  `--native-frontend-probe <native-exe> <new-root> <next-dir> [native-overlay-exe]`
  验证真实窗口启动/切换、Overlay 独立心跳、MMF、提交重放及子进程退出；省略末参数
  使用 WPF 回退版。`--native-host-probe` 保留无界面路径。

最终结果和原始轨迹：`../_run/CoreNative/frontend-parity-20261008/validation.json`。
ARM64/x64 各 263 条协议/UI/命令差分零差异；Core 各 14 项 CTest、Linux 10 项、
Native Overlay 各 4 项通过。两架构实际 Q8 回执/200 次重连通过，句柄净增长均为 0，
私有内存增量分别为 36/44 KiB（短时定向采样，不是长期压力指标）。默认 native Overlay
及 WPF 的实际进程启动、切换、异常退出恢复和宿主退出有独立证据。早期前端探针使用
Process.MainModule 查询并行跨架构进程时发生 Win32 读取错误，已改用
QueryFullProcessImageNameW 并保留失败日志；这不是输入法部署或物理打字验收。
本阶段仍只启动测试命名空间；没有替换日用 Core、注册 TSF 或部署 Hook。
**尚不能宣称完整生产替代：**生产宿主接管、实际 TSF/Hook 应用编辑与跨完整性上下文、
长时间资源/延迟验收仍未完成。263 条小表差分和真实模型的定向回执测试也不等于穷尽
所有异步输入序列。历史失败轨迹保留，暂停中的 Rime holdout 未恢复。

## 独立宿主、Dialog 协议和 UI 通道（2026-10-08，继续追平）

本阶段把已移植的输入内核接入独立 Windows 宿主。仍是实验实现，尚不能宣称完整替代
C# Core；以下完成项与剩余验收分开记录。

- `RuntimeProtocol` 接入配置读写、独立配置/词库版本、选重键读写、造词编码、历史查询、
  加词、导出、方案目录查询、语言切换、UI 命令回调和形码下的拼音管理响应。配置默认表
  从 C# 生成，补齐此前遗漏的 18 项（共 68 项），包括实际控制学习的 `整句Tab自学习`。
  `tools/generate_core_native_config.py --check` 可检查生成表是否过期。
- UI 投影覆盖光标、候选/注释/拆分、编码伪装、语言、前端开关、音效和动效设置；新
  组合等待新光标最多 30ms。支持 v1 MMF 和带互斥锁/Changed 事件的 v2 快照；v2 读者
  占锁时仍更新 v1，不阻塞 Core。独立 5 秒心跳不依赖按键或 UI 刷新。
- `RuntimePipeServer` 使用 Windows 重叠 I/O、换行 UTF-8 帧、有界请求/连接数和可取消
  读写。响应先于 UI 发布；通知不产生响应。分段 UTF-8、连续帧、重连、并发去重、
  客户端空闲时关闭、快照读锁竞争和导出再导入有原生回归。
- `--serve-isolated <root> <TigerClaw.Core.Native.Test.*>` 提供无界面宿主：配置/模型
  只从明确目录读取；管道、UI MMF 和心跳使用测试命名空间。支持 `exit_core` 或标准
  输入 `quit` 关闭；宿主负责定时器、可选的独占 Qwen 子进程和异步解码 UI 刷新。
  可选资源为 `<root>/sentence/TigerClaw.Sentence.exe` 与其 `Models/sentence-qwen-q8.gguf`，
  也接受根目录 exe / Models 路径。没有资源时不寻找或接管日用 Sentence 进程。
- 宿主模型缺失/损坏时使用普通输入；新增/修复 TCSKNM03 后重载可以恢复整句并保留编码。
  底层 `RuntimeInput` 默认仍保留严格拒绝策略，宿主显式启用降级。已有有效映射继续持有。

证据：`../_run/CoreNative/host-parity-20261008/validation.json`。ARM64/x64 各 183 个
协议响应、UI 快照和命令回调零差异；缺失与损坏模型路径均覆盖。版本字段、68 项配置
全文均参与比较；实验程序身份/目录路径除外，随机候选帧标识仅比较是否存在。
Windows 各 14 项、Linux 10 项 CTest；两架构均有实际 C# 客户端→C++ 管道→v2 UI→
提交/重放→协议退出验证。实际三源 Q8/虎整句码表的 58 组差分、3,510 次学习查询、
345 个解码状态和双向日志回归通过；实际 Qwen 预加载/重复评分/正常退出 smoke 通过。

隔离问题及修复：C# `CoreRuntimeState(differentialRoot)` 原先仍会执行开机启动项同步，
早期探针将 `TigerClawCore` 写成 `dotnet.exe --with-overlay`。已增加明确测试根目录的
系统副作用隔离；按正在运行的 `release_arm64/TigerClaw.Core.exe` 和日用配置“开机
自动启动=是”恢复启动命令（并非事前注册表备份恢复）。恢复依据保存在
`startup-recovery.json`。最终探针覆盖开关启动选项及重载，逐次保存启动项前后身份并
断言不变。旧失败输出和一次重复构建导致的对象文件占用错误保留；最后构建干净通过。

**仍未完成的完整追平门槛：**真实 Dialog/Overlay 进程启动协调（当前无界面宿主只
输出窗口命令）、生产 TSF/Hook 的权限/重连/真实应用输入联调、完整异步候选帧连续性
及性能/长期资源验收。现有协议/UI 差分主要是小型普通形码和无模型降级；不能据此推断
真实前端或整句回执协议已经完成端到端验收。独立宿主未连接日用命名空间，未部署、
未提交/push；暂停中的 Rime holdout 保持原状。

## 宿主协议与重载追平（2026-10-08，后续）

新增 `RuntimeProtocol` 串行请求适配器和隔离文件探针 `--runtime-probe`，接通现有
输入宿主。它处理 `key`、`query_state`、`focus`、`composition_canceled`、
`learning_commit`、`get_config`、`get_schema_list`、`reload_config`、`reload_mb`。
按客户端/事件去重，重试保留原提交与学习回执，仅重写请求序号；按键释放提示同步 C#。
16 个并发重复请求只执行一次。未移植命令返回失败；`hello` 明确标识实验适配器。

配置重载同时准备码表、拼音表和选重键，宿主接受后再发布。失败保留原快照和输入；
这是刻意保留的事务保证，和 C# 在配置读取前清空组合的失败行为不同。
`reload_mb` 使用已生效配置更新码表/选重键并保留普通模式输入；`reload_config`
读取磁盘配置、清空组合、恢复默认语言，并在下一次新按键响应通知前端取消旧组合。
该取消标记随按键响应重放，不会重复处理输入。

验证：ARM64/x64 的文件协议差分各 **86 个请求、零差异**，涵盖普通形码输入、
选重、退格、语言切换、焦点/取消、重试、查询及两类重载。Windows 两架构各
14 项 CTest、Linux 10 项通过。构建和原始响应证据保存在
`../_run/CoreNative/protocol-20261008/validation.json`；早期多带响应字段与取消
时机差异的失败输出也保留。复现：

```bash
python3 tools/test_core_native_protocol.py \
  --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe \
  --output next/_run/CoreNative/protocol-local
```

差分不含版本计数元数据：当前适配器仍以快照 generation 同时报告配置/词库版本，
尚未同步 C# 两种独立版本计数。86 请求使用小型普通形码表，不代表整句协议或
真实前端验收。学习协议已接现有回执 API，其整句落盘链路沿用上一阶段直接 API 测试。
生产主管道、完整 hello/Hook 配置、UI 发布/光标通知、Dialog 全量命令和真实应用
输入仍待完成。本轮未部署或提交，C# 继续作为生产默认。

## 追平 C# 主线的算法与学习功能（2026-10-08）

用户已明确“同步到主线”指 **C++ 功能追平当前 C#**，不是 Git 分支合并。
本轮同步了形码整句的模型、排序、自学习和选重后续输路径。C# 仍是生产行为基准。

- `SentenceFivegramModel` 直接映射 TCSKNM03 Q8/Q16，严格校验 UTF-8、索引和边界，
  查询会话持有映射生命周期，缓存有界。Beam、EOS 和提前上屏证据使用完整四字历史。
  显式提供的 TCSKNM01 历史测试文件仍可读取；五阶加载失败不会寻找旧模型回退。
- 同步主码奖励、主码/显式选重的孤字保护、嵌入式 TCSLEX01 词汇先验、直出候选来源
  与原词表顺序、直出/组句配对偏好。Qwen 混合评分把学习奖励放在混合之后，并重新
  应用融合顺序。模型置信度独立于这些排序奖励。
- 自适应学习每次确认只记录一个事件、等级 1..3；排名等级和实际确认次数分别累计。
  组句修正仅学习双方共有合法边界之间的变化片段；保留已有学习/补充词的强化规则。
  学习前缀采用索引查询，保留 C# 的 64 编码行扫描上限；Beam 最多额外保留 4 个提示路径。
- 学习日志使用方案目录下的 `自学习-虎爪.txt`、主线 `整句Tab自学习` 开关和模式标识。
  I/O 进入有界工作队列，解码读取不可变快照。撤销、按词遗忘、清空、重复事件、损坏日志
  和跨进程排他锁有回归覆盖。C# 与 C++ 可双向读取本轮隔离日志。
- `RuntimeInput::IssueLearningReceipt` / `AcknowledgeLearningReceipt` 提供宿主回执接口：
  只有返回输出完全一致的候选确认才签发；客户端必须匹配，30 秒过期，一次性消费。
  失败、重复、过期回执不会学习，成功配置/方案切换取消旧回执。生产协议尚未接线。
- Tab/方向键选中后继续输入字母，锁定所选前缀；启用提前上屏时提交该前缀，否则留在
  组合中。新搜索重新播种五阶历史，退格按边界解除锁定；锁定对象进入异步结果身份。
  键入选择后缀仍编辑当前分段。取消令牌传入 Beam，取消结果不发布。
- 提前上屏使用所有保留的完整候选和适用的未完成尾部，不以菜单 TopK 构造置信度。
  阈值同步为 .99；成熟个性化贡献有界，强证据使用基础模型份额。学习影响过的截断池
  不得自动提交；提交还需匹配当前显示首选并通过竞争分段的保留长度检查。

验证与复现：

- 当前三源 Q8 的 2,006 条独立评分查询与 C# **逐值完全一致**。
- `tools/test_core_native_mainline.py` 的 ARM64/x64 差分：各 58 组 / 3,510 次学习查询 /
  345 个解码状态，零差异。包含冻结实际虎整句码表、增删输入、锁前缀、窄 Beam、
  孤字保护、词汇/补充奖励、学习置信度、直出/组句偏好反转和双向日志。两种架构均零差异。
- Windows ARM64/x64 Release 各 13 项 CTest、Linux Debug 10 项通过；新增测试包含完整
  `RuntimeInput` 选重→回执→日志链路。C# 主线学习套件 29,930 项通过，构建零警告/错误。
- 本轮证据：`../_run/CoreNative/mainline-sync-20261008/`。`validation.json` 记录最终文件
  身份和结果。早期失败日志保留；其中 Windows 换行断言、旧版提前上屏/可达性断言已
  按当前 C# 修正。x64 一轮生成的 JSON 输入出现非 JSON 字节，原文件保留，复现脚本
  新增 flush/fsync 和写后字节校验，再以独立目录重跑。

```bash
python3 tools/test_core_native_mainline.py \
  --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe \
  --model next/_run/ThreeWayMainline/stage-ARM64/Models/sentence-fivegram-mobile.bin \
  --shape-table next/_run/FirstCorrectionSeed-20261003/real-repeat/码表/虎整句/虎整句.txt \
  --output next/_run/CoreNative/mainline-sync-local
```

这仍不是完整 Core 功能验收：生产主管道/完整 UI 投影与通知、Dialog 全量命令、
TSF/Hook 真实输入联调、无模型运行策略和长期资源/延迟验收尚未完成；本轮也没有
重新运行所有暂停前的差分脚本或真实 Qwen 进程 smoke。不得替换日用 Core。
未部署、未提交/push；独立的 Rime 纠错 holdout 继续保持暂停。

学习辅助代码的来源：GPL-3.0 项目 `lvyww/tigirl`（本机 `C:/Users/yc/Desktop/ime`），
提交 `cc9a0d8cccc4584a86c9bcd373575568d1852404`。以其 `native/SentenceLearning.h`、
`SentenceLearningStore.h`、`EditableText.h` 为起点，适配 TigerClaw 命名空间、Unicode、
方案文件名、Windows 文件共享锁、工作线程和当前 C# 前缀索引/回执语义。
原文件 SHA256 见证据目录 `learning-source-hashes.json`，同仓 GPL-3.0 许可适用。

## 恢复开发的前一阶段（2026-10-08）

用户已明确要求继续推进 C++ Core，解除本项目 2026-09-09 的开发暂停。
本轮先完成宿主状态投影和方案切换失败恢复；C# 仍是生产默认和行为基准。
独立的 Rime 邻键纠错 holdout 暂停不受此授权影响。

- `RuntimeInput::CaptureSnapshot()` 在一次同步、最多一次异步结果 Pump 后，
  返回拥有数据的 `RuntimeInputSnapshot`：方案/运行时代际、输入模式、原码、
  活动编码、显示码、候选页、页大小、选中项、整句代际及解码/错误状态。
  普通混输保留已解析前缀，整句使用 `DisplayCode()`；返回对象不会随下一次
  按键、异步评分或方案切换而改变。调用方仍须串行访问宿主。
- 宿主新增 `SwitchSchema()` / `SwitchRecentSchema()`，最近方案快捷键也走
  同一路径。目标码表准备完成后、运行时发布之前，先让输入宿主加载模型并迁移。
  模型缺失或迁移准备失败时抛出错误，原方案、配置、最近方案历史、代际及原码保留；
  修复资源后可直接重试。此接线不自动写配置文件。
- `RuntimeLexicons` 的低层直接发布接口仍为数据层调用者保留；绕过宿主调用
  `SwitchSchema()` 或直接 `Reload()` 不具有上述宿主事务保障。
  完整配置重载的宿主事务和主协议错误响应仍待接入。
- 仍只有离线实验宿主，无生产主管道、完整 UI 协议、TSF/Hook 联调或日用部署。
  候选注释/拆分/反查和异步通知尚未完成。该阶段尚未移植五阶及自学习；
  同日后续同步已完成上述算法路径，见顶部当前记录。
- Windows x64 / ARM64 Release 各 11 项 CTest 全部通过；Linux Debug 8 项通过。
  新增宿主回归覆盖普通/混输/拼音/大写/英文/整句快照、快照数据保留、异步解码、
  手动选重、取消、模式迁移、显式/快捷键切换失败及修复资源后的重试。
  首轮 x64 新测试漏发 Ctrl 按下事件，已补齐物理事件序列后通过完整测试。
  Linux 不执行 Windows 宿主集成测试。本轮未重跑 36 个历史 C# 差分脚本、
  真实 Qwen smoke 或真实前端验收，不能据此声称已经追平当前 C#。
  验证清单和 CTest 日志副本：`../_run/CoreNative/resume-20261008/`。
  构建目录：`../_run/CoreNative/{x64,ARM64}/` 和
  `/tmp/tiger-core-native-resume-20261008`。

源码已于 2026-09-13 的 `6625fc1` 纳入 Git。下方暂停时“未跟踪”的说明仅为历史记录。

## 暂停时历史快照（2026-09-09；已于 2026-10-08 恢复）

**当时状态：按用户要求暂时挂起，原因是额度消耗超出预期。**
当时保留源码和测试，停止推进、构建及部署；现在已按明确指令恢复隔离开发。
这不是完成、废弃或回退。C# Core 仍是生产默认和行为基准，C++ Core 不能替换日用版本。

本节是暂停时的历史快照；当前进度以上方恢复记录为准。下方早期 gate 说明及追加记录包含历史状态，不能用其中
“尚未实现”的旧描述否定后续已落地的代码，也不能把局部测试通过视为完整 Core 验收。

### 已落地的主要内容

- 独立 CMake 工程、实验性离线 CLI、ARM64/x64 构建和 Linux 可移植层测试。
- CompactLexicon 二进制读写、码表/配置/拼音加载、顺序与 Unicode 处理、用户调整
  持久化、不可变运行时快照和重放缓存；大量独立 C# 差分脚本位于 `tools/test_core_native_*.py`。
- 普通、混输、拼音、大写、英文、标点、选重、快捷键与输出历史处理。
- Windows V2 n-gram 映射、整句 lattice/Beam、最优码/重码规则、补偿评分、提前上屏
  证据与未上屏后缀、异步代际隔离、手动选重冻结及 Qwen 混合字数评分规则。
- Sentence 隔离管道客户端、owned-process 生命周期、开关/方案关联加载释放，
  以及独立真实模型 smoke 程序；不会自动连接生产 Sentence 管道。
- 最近完成：`RuntimeSentenceState.ProcessKey` 通过 `InputDispatchExtensions` 复用
  `ChineseInputSession` 的物理事件执行顺序，接入引号补偿限制、语言切换、组合键
  透传取消、整句编辑、标点与字面编码退出。
- `RuntimeInput` 已支持显式传入 n-gram 模型路径后接入整句，真实普通/整句方案
  双向切换、原码迁移、选重重置、候选读取、焦点/取消和导入。
  未提供模型路径时仍明确拒绝整句方案，不静默退化为普通模式。

### 最新验证及边界

- 最后一批修改后，Windows ARM64 和 x64 各 11 项 CTest 全部通过。
  覆盖真实临时码表目录、最近方案快捷键回调的双向切换、大小写原码、目标码表提交、
  焦点保留、取消、模型缺失时保留旧组合并切回恢复。
- 上一批共用路由修改后，Linux 8 项 CTest 全部通过；Linux 不覆盖 Windows
  `RuntimeInput` 集成。不要将这个结果表述成跨平台完整输入法验收。
- ARM64/x64 真实 Qwen smoke 曾通过：使用 `_run` 中的独立 Sentence 程序和
  `TigerClaw.Core.Native.Test.*` 管道，同一架构重复评分稳定，跨架构分数并非逐位相同。
  这不是准确率、输入延迟或日用验收。
- 模型加载失败时 host 保留旧组合，但 `RuntimeLexicons` 已发布的目标方案不会由
  host 自动回滚；测试通过切回原方案恢复。后续必须决定主协议如何报告/恢复这种失败。
- `RuntimeInput::Session()` 仍只代表共用普通/特殊模式 session；整句原码应通过
  `Raw()` 读取，选中项通过 `SelectedCandidateIndex()`，候选通过 `Page()`。

### 恢复后待做（未完成项）

1. 暂停前刚开始检查统一状态快照：目前编码、候选与选中项还是分开读取，`Page()`
   会 Pump 异步结果。需要提供一次同步/一次 Pump 后的拥有数据的快照，供后续 IPC/UI
   使用，避免跨调用取得不同代状态。**已于 2026-10-08 实现，见顶部恢复记录。**
2. 完善 UI 投影：普通混输前缀、整句显示码、候选注释/拆分/反查、待解码状态、
   页大小与选中项，以及异步候选变化的通知。现有 `CandidatePage` 不是完整 UI 协议。
3. 继续真实方案/设置/特殊模式切换、提前上屏后迁移、生命周期失败与恢复的端到端
   C# 差分；已有低层测试不替代完整宿主行为对照。
4. 实现主 `Protocol/messages.md` 命令面、命名管道服务、事件幂等重放接线、
   focus/caret/activation、MMF/心跳、进程监督与 Dialog 配置/加词等命令。
5. 隔离 TSF/Hook 前端联调、真实输入延迟/内存基准、故障注入、发布回滚和用户验收。
   现有 CLI 仍只有离线诊断，不能因为库层有按键入口就称为可运行输入法。

### 恢复操作与保护范围

- 先读根 `AGENTS.md`、本节及相应实现，再检查 worktree；不要从头重做已验证模块。
- 关键文件：`RuntimeInput.h`、`RuntimeSentenceState.h`、`ChineseInputSession.h`、
  `RuntimeSentenceInput.h`、`SentenceCompositionSession.h`、`RuntimeLexicons.h`、
  `tests/RuntimeLexiconsTests.cpp`。C# 参考 `InputMethodEngine.cs` 和 `ProtocolHandler.cs`。
- 构建：`cmake --build next/_run/CoreNative/ARM64 --config Release --parallel 2`；
  测试：`ctest --test-dir next/_run/CoreNative/ARM64 -C Release --output-on-failure`。
  x64 替换目录中的 ARM64；从 WSL 调用 Windows CMake/CTest 可执行文件。
- Linux 已有构建目录 `/tmp/tiger-core-native-build`，可能被临时目录清理；若仍存在可
  `cmake --build /tmp/tiger-core-native-build --parallel 2` 后运行对应 CTest。
- 工作区有大量未提交的 C# 及其它修改，`next/TigerClaw.Core.Native/` 当前也是未跟踪目录。
  **本次暂停只保存文档，没有提交、push 或制作独立备份。** 不得 broad stage/revert/clean。
- `release_arm64/` 是用户日用目录，本轮开发未覆盖它。恢复开发也不代表获准部署；
  不动其配置、码表、用户调整和模型，不连接生产 IPC，不重启日用进程。

The target is a full parallel implementation of TigerClaw Core, preserving the
current C# input behavior, configuration, IPC and sentence algorithms. C# remains
the production default and behavioral authority. This is not the archived Rust
Core and does not depend on that archive. No production publish script is changed.

Executable: `TigerClaw.Core.Native.Experimental.exe`. It currently supports only
offline diagnostics; no BimeIPC server, production shared memory, global hooks,
process launching or runtime-directory writes. Running it without an explicit
offline command prints usage and exits. It is **not an input-capable Core yet**.

## Full implementation gates

- [x] Independent CMake build, Windows ARM64/x64 and Linux data-layer tests.
- [x] Validated, owned TCLX v1 reader and compiler; original code/candidate order and exact
  UTF-16 preservation, including supplementary and unpaired surrogate units.
- [x] Cross-language fixtures generated by the actual C# `CompactLexicon`,
  including its update path, rather than a duplicated binary exporter.
- [ ] Complete Windows Unicode comparison parity. ASCII
  codes use an ordinal ignore-case fast path; non-ASCII codes currently use
  CompareStringOrdinal on Windows and explicitly fail on Linux. This limitation
  must be resolved/validated before general user-schema support is claimed.
- [ ] TXT/YAML/configuration loading, schema-name priority/CurrentCulture file
  order, user adjustments, independent shared pinyin table and metadata.
  Token escape decoding, inline comments, display/commit packing and streaming
  row parsing, byte decoding and single-file loading are ported and differentially
  tested. Frequency-ordered coded-row assembly is implemented. Culture acquisition,
  uncoded inference and main-table schema loading are implemented. Complete
  configuration, pinyin runtime ownership and sentence supplements are still
  pending. The independent pinyin table loader is implemented.
  Display/prefix metadata is implemented. Windows directory
  ordering is implemented. Adjustment actions are
  ported, including custom/adjustment-file readers; persistence and full schema
  runtime wiring are not yet implemented.
- [ ] Ordinary input, punctuation, selection/page keys, mixed input, temporary
  pinyin, shortcuts and literal-code exits, all compared per key to C#.
- [ ] Idempotent physical-key replay and focus/modifier lifecycle.
  The synchronized FIFO response cache is implemented and compared to C#;
  actual key dispatch, focus changes and transport are not yet implemented.
- [ ] Sentence n-gram V2 model reader and decoder, rank eligibility, optimal
  codes, early-commit evidence, retained raw suffix and all golden cases.
- [ ] Latest-generation asynchronous Beam and Qwen results, manual navigation
  freeze, mixed-length Qwen policy, cancellation and schema/settings residency.
- [ ] Complete Protocol/messages.md command/notification surface, Windows pipe
  permissions/lifetime, UI snapshots/heartbeat and process supervision.
- [ ] Differential traces on ordinary/pinyin/sentence mode, schema switching,
  edits, timeouts, reconnects, late results and failure injection. Unknown or
  unsupported functionality must not silently claim successful execution.
- [ ] ARM64/x64 native builds, input latency/memory comparison on fixed datasets,
  isolated frontend integration, backup/rollback packaging and user acceptance.

Completing only the executable shell or an ordinary-input subset does not satisfy
the project objective. Keep this gate list current as functionality is implemented.
Do not merge the Core into TSF/Hook/Overlay; preserve the split-process contracts.
The Android C++ sentence library can be used as a reference, but its mobile
TCSKNM02 reader is not the Windows TCSKNM01 reader and cannot replace it unmodified.

## Build and test

```powershell
cmake -S next/TigerClaw.Core.Native -B next/_run/CoreNative/ARM64 -A ARM64
cmake --build next/_run/CoreNative/ARM64 --config Release
ctest --test-dir next/_run/CoreNative/ARM64 -C Release --output-on-failure
```

Linux data tests:

```sh
cmake -S next/TigerClaw.Core.Native -B /tmp/tiger-core-native-build -DCMAKE_BUILD_TYPE=Debug
cmake --build /tmp/tiger-core-native-build --parallel 2
ctest --test-dir /tmp/tiger-core-native-build --output-on-failure
```

Generate cross-language fixtures in a **new** directory:

```powershell
dotnet build next/TigerClaw.Core.Tests/TigerClaw.Core.Tests.csproj -c Release -p:PublishAot=false
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll --native-core-lexicon-fixtures next/_run/CoreNative/fixtures-v1
python tools/test_core_native_lexicon.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --fixtures next/_run/CoreNative/fixtures-v1
```

The same fixture directory is readable by the Linux binary. Python/native paths
must belong to the same OS. No user input is recorded by this harness.
`--lexicon-json <image>` emits UTF-16 unit arrays for comparison, not production
IPC responses. `--capabilities` explicitly reports input-engine/production support
as false. The current reader has a 256 MiB input-image limit and copies individual
queried strings; hot-path lookup optimization is pending, not a performance claim.

2026-09-09: Linux Debug and MSVC ARM64 Release boundary tests pass. Both platforms
pass 40 C# fixture images, 10,004 codes, exact candidate order and UTF-16 content.
No full input-engine or cross-platform Unicode-code parity is implied by this test.

## Replay and text parsing foundations

`--replay-stdio` is an offline cache probe, not the input protocol. It preserves
exact UTF-16 identity keys, minimum capacity 16, FIFO eviction (reads and updates
do not refresh age), and C#'s sequence-field replacement. `Execute` serializes
lookup/execution/store; callbacks must not reenter that cache. A throwing callback
does not publish a cached response. Native tests exercise concurrent requests.

```powershell
python tools/test_core_native_replay.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_text.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_rows.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_files.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_build.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_assembly.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_adjust.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_edit_files.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_order.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_construct.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_infer.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_schema.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_pinyin.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
python tools/test_core_native_config.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

Run Python, .NET and the native executable on Windows for these differential
probes. Build the C# test assembly first as above. Cross-WSL redirected .NET
execution has shown transport stalls and is not counted as passing evidence.

2026-09-09 evidence:

- Windows ARM64 and x64: 78,190 replay operations agree with actual C# cache,
  covering every UTF-16 unit's whitespace classification; trace SHA256
  `5d84188406f079845feab33ddc07ef7077147adafdd7dafb4c82619f70bbdd8e`.
- Windows ARM64 and x64: 141,084 token cases agree with actual C# private helpers
  (bound by reflection in the test assembly), including all UTF-16 code units,
  unpaired surrogates, NUL, escape/comment interactions and display/commit packing;
  trace SHA256 `a77d0e56bba0e010a32ab16b5c7c4e57e564fead2b9dc1fd866f397ff9b7c100`.
- Linux Debug, Windows ARM64/x64 Release: all three CTest targets pass.

`LexiconText` deliberately retains `WrapDec`'s legacy placeholder replacement
semantics, even for literal `Bime20231222BIME` text. This is parity, not a new
escape grammar. The `--lexicon-text-stdio` probe accepts/emits UTF-16 unit arrays.
Neither probe loads live configuration, listens on production IPC or types text.

## Streaming lexicon rows

`LexiconLineParser` retains the YAML body state across lines and returns ordered
rows (an empty code denotes an uncoded entry). It handles both code-first and
text-first layouts, optional frequency positions, overflow rejection, inline
comments, adjustment-directive filtering, and display/commit token decoding.
Normalization remains asymmetric exactly as in C#: ASCII code-first codes are
lowercased, while text-first codes retain their casing at this layer.

The C# parser loop was extracted without changing its body into `ParseMbLines`;
`ParseMbFile` still supplies `File.ReadLines` and its existing encoding detection.
The probe calls this actual loop, rather than a test-only duplicate. Full C# tests
passed following extraction. The new native code is not wired into C# production.

Windows ARM64 and x64 each pass 75,724 differential line-document cases, including
every UTF-16 unit adjacent to numeric text, multi-line YAML state, frequency limits,
directives, and custom number signs. Trace SHA256:
`5ea4f9d9ab4e1dedb9ac44b2bebfdb7d70cb516e396e6de24a1aca3352098b11`.
The initial Unicode-minus-sign mismatch was fixed and retained in the corpus.
ASCII hyphen fallback follows .NET 10's
[NumberFormatInfo sign rules](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Globalization/NumberFormatInfo.cs).
Positive/negative signs are explicit parser parameters; reading Windows current
culture is still a loader task, and this test is not general culture parity proof.

## File decoding

`ReadLexiconFile` reads one explicit path and emits parsed rows through a callback.
It recognizes `.dict.yaml` case-insensitively and matches CR, LF and CRLF line
boundaries. Decoding matches the current .NET 10 Core: UTF-8 by default (including
the old detection function's `Encoding.Default` fallback), UTF-16 BOM selection,
and StreamReader's remaining BOM recognition. In particular FF FE 00 00 retains
the existing UTF-16 selection; changing this to conventional UTF-32 LE decoding
would silently change the current Core's output. Invalid sequences use the same
replacement grouping as the tested .NET reader.

Windows ARM64/x64 each pass 83,607 byte cases against StreamReader and 60 temporary
TXT/YAML files against actual `ParseMbFile`, including Chinese paths, endian/BOM
combinations, supplementary text, damaged bytes and reader-buffer boundaries.
Byte trace SHA256: `7fec288a132d9ba60cdb9864d44ddf2b9da122ae9762082ef4e304930301737b`.
File-content SHA256: `daf523dc3cfaf5afb1637c6bb1910b1cff4480bf49c72e6091385b62e1065fec`.
Linux and both Windows architectures pass decoder unit cases in CTest.

This initial loader owns the full byte and decoded text buffers during a file
load, so bounded-memory streaming is still pending. It does not enumerate schema
directories, merge/deduplicate rows, acquire current culture or publish a table
snapshot yet. File-open/read failure throws; a future runtime loader must build
privately and only publish after successful completion. The test probe's `file`
operation is read-only; the Python harness creates and removes only its own
temporary fixture directory, never production tables.

## Native compact compilation

`CompactLexicon::Build` compiles ordered code/candidate entries into an owned
TCLX image. The string pool deduplicates exact UTF-16 text, without deduplicating
candidate positions or changing code enumeration order. Temporary build indexes
do not survive in the resulting object. Input mutation after compilation cannot
change an existing snapshot. Duplicate case-insensitive codes are rejected by
the same image validator used for file loading. Empty candidate lists and empty
text are legal. The existing experimental 256 MiB image cap still applies.

Windows ARM64 and x64 each pass 502 byte-for-byte comparisons with the actual C#
`CompactLexicon.Build`, including repeated/pooled text, empty keys/lists, mixed
ASCII casing, supplementary characters, NUL and unpaired surrogates. Trace SHA256:
`b9c337550f432202c8f3adec1473a74b170dff733939da3c85fd89f9d574abe9`.
The test operation is `entries` on the offline text probe. This is compiler parity,
not schema-runtime parity: inference of uncoded words, adjustment-file wiring and
directory ordering remain separate assembly
steps. Non-ASCII code comparison remains the existing unverified Windows gate.

## Coded-row assembly and normalization

`AssembleCodedRows` stably sorts descending frequency, normalizes code, groups
case-insensitive codes in first-insertion order and deduplicates candidate text
ordinally. Equal-frequency source order is retained; differently cased candidate
text stays distinct. It returns independent entries accepted by the compact
compiler. Uncoded rows are skipped at this stage, as in the C# map loop; their
construction-code inference must be applied before this stage in the runtime.

`DotNetCaseTable.h` is generated data from the Windows .NET 10.0.11 reference
runtime, not a runtime .NET dependency. `CodeCase.cpp` traverses Unicode scalars
while retaining malformed UTF-16 units. The table provides invariant lowercase
and the case-insensitive equivalence keys used during grouping. Generation:

```powershell
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll --native-core-case-table next/_run/CoreNative/DotNetCaseTable.generated.h
```

The output path must be new. Compare against the checked-in generated header
before replacing it; upgrading .NET/ICU requires regeneration and revalidation,
not assuming that OS casing tables always agree. Current header SHA256:
`af378796f0b0c27938d97a869b5bd2aa0d5e19b17f91334e269720b7de0889bd`.
This does **not** replace the compact reader's Windows sort comparator yet:
equivalence grouping and total ordering require separate parity checks.

Windows ARM64 and x64 each pass 69,712 differential requests: all Unicode scalars
in separated batches, every singleton UTF-16 unit, and 2,000 randomized grouped
row sets. Normalization invokes actual C# `NormalizeCode`; assembly reference
orchestration mirrors the production loop using .NET stable ordering, dictionary
and list behavior (it is not a full Reload test). Trace SHA256:
`46f28f683e8ca57d21327ddf0f80f2e88e0199c2c34eea02932dcbaeb1a7d605`.
Linux/ARM64/x64 CTest also checks assembled rows through compact compilation and
queries after the source rows are destroyed. No production directory is loaded.

## Adjustment actions

`ApplyAdjustments` operates on a private ordered assembly before compact snapshot
publication. It supports add, top, delete and advance directives with the C# tab
payload rules. Identity uses commit text, not displayed text: add/top preserve the
first matching stored entry's display, delete removes all identity matches, and
advance moves only the first match one place forward. Adding an existing identity
moves that entry to the end. Empty code buckets remain in place after deletion.
No inline comment stripping is applied to adjustment payloads, matching C#.

ARM64 and x64 each pass 5,000 command-prefix comparisons against actual C# private
adjustment methods, comparing the compiled TCLX after every prefix. The corpus
includes duplicate identities with different display text, malformed records,
escaped whitespace, supplementary/unpaired text, missing codes and literal `#`.
Trace SHA256: `5cbe28539a7a058546fe4db09425e65c3e897c1e1d9b0027164b7c7874cb99d2`.
Native CTest includes deterministic ordering/identity regressions. This does not
yet implement adjustment-file persistence, live editing
commands or schema-runtime snapshot replacement.

`ReadLexiconLines` now shares decoding and CR/LF handling between ordinary tables
and `LoadAdjustmentFile`. The latter loads either adjustment directives or custom
tab-separated rows into a private assembly. Missing files and directory paths are
ignored, as in the C# loaders. Custom rows remove all commit-identity matches and
insert the **new** stored display/commit text first; this intentionally differs
from a top directive, which preserves the existing display text. Initial-comment
lines are ignored for custom files; `#` inside payload text remains literal.

ARM64/x64 each pass 244 actual-file comparisons against C# `LoadAdjust` and
`LoadCustom`, including 120 UTF-8/UTF-16 files individually and accumulated in
sequence, plus absent-file and directory cases. Fixture-content SHA256:
`1869f582bd9912d7fe831bca153fe803fbe9846862736ebecd34f8812f3a6560`.
The ARM64 83,607 byte/60-file ordinary-table regression still passes after sharing
the line reader. All three platforms' CTest suites cover custom identity replacement.
This does not yet establish permissions/race-error parity or bounded load memory:
edit files are fully decoded and their lines buffered before applying. Runtime
snapshot publication and user-data writes remain unimplemented.

## Windows directory order（历史 ICU 路线，已由顶部 NLS 实现取代）

`GetOrderedLexiconFiles` enumerates top-level TXT and `.dict.yaml` files, placing
TXT enumeration before YAML for stable ties. Schema-named files take priority;
remaining ordering uses a stable culture comparison. Default culture comes from
the Windows user locale, with an explicit culture argument available to tests.
The module links the Windows SDK `icu.lib` and uses the OS ICU collator, matching
the default .NET ICU approach described in its
[collation implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Globalization.Native/pal_collation.c).
This adds a Windows system ICU runtime dependency, not a bundled model or .NET
dependency. Forced .NET NLS mode, app-local ICU and unusual sort overrides are not
supported/verified yet; Linux directory ordering explicitly throws rather than
silently substituting a different collation.

ARM64 and x64 each pass 240 real-directory comparisons with actual C#
`GetOrderedLexiconFiles`: 20 fixtures across nine explicit cultures, default user
culture and trailing separator/dot forms. Tests include the Tiger/quick-symbol
filenames, schema priority, accents/canonical equivalents, CJK, kana, numeric
names and directory exclusion. Filename corpus SHA256:
`402da0dba275dbeacf053735f5163fec32f4338b3acdf37dfc28c37d41b1a9aa`.
Enumeration races, access-denied behavior, special reparse points and non-ICU
deployment environments remain outside this evidence. The function does not yet
filter schema-specific construct/supplement files or assemble a full schema.

## Text elements and word construction

`TextElementStarts` implements the reference runtime's extended grapheme rules,
using generated .NET 10.0.11 property ranges instead of assuming that OS ICU's
grapheme version always agrees. Its boundaries follow the rules used by .NET's
[TextSegmentationUtility](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Text/Unicode/TextSegmentationUtility.cs).
It preserves UTF-16 offsets and treats unpaired surrogates as Other, without
changing the original text. Generate the property data into a new path:

```powershell
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll --native-core-grapheme-table next/_run/CoreNative/DotNetGraphemeTable.generated.h
```

Checked-in property header SHA256:
`223af80593a1bcd8c2c443e415549f704344bc49444e8c3bb57138f7c1f01f9b`.
Runtime version upgrades require regenerating and retesting, not blindly retaining
these tables. The generator uses reflection only in the reference test tool.

`ConstructWordCode` accepts an explicit single-text-element code lookup. It
preserves the ordered punctuation-removal rules, skips unknown elements, applies
the ASCII-letter doubled-code fallback, and implements single/2/3/4+ element
construction. Codes are sliced in UTF-16 units, as in C#, even for unusual codes.
Lookup construction and inferred-row appending are now implemented as described
below; this function alone is not a schema loader.

ARM64/x64 each pass 42,176 differential requests: all Unicode scalars in batches,
30,000 mixed grapheme-property sequences and 10,000 randomized construction cases.
References are actual `StringInfo.ParseCombiningCharacters` and the private
`ConstructCiFromLookup`. Trace SHA256:
`e4a2b1b389520299783d7bb43a546268641577d105c63c0ba82998d76a8674e8`.
Linux/ARM64/x64 CTest adds deterministic combining, emoji, regional-indicator,
two/three/four-character and unknown-character regressions. These are not yet
integrated into candidate UI or live editing behavior.

## Main-table schema pipeline

`BuildConstructCodeMap` preserves first explicit construct-file rows, then fills
missing single-text-element identities from main-table entries. Main codes must
have at least two UTF-16 units. Priority follows C# letter/nonletter and o/z rules;
ordinal code order breaks equal-priority ties (not shortest-code preference).
`AppendInferredRows` uses commit text to construct code while preserving stored
display text and frequency. It accepts a separate immutable uncoded input span.

The char-based letter property table is generated from .NET 10.0.11 via
`--native-core-letter-table <new-output-path>`. Header SHA256:
`17ad214a2d7c92144e4ce2c8e6aae580bb96ddbd131ab2bdb54d32111344d13b`.
ARM64/x64 each pass 66,536 reference-helper comparisons covering every possible
first UTF-16 unit plus randomized maps and actual construct files. Fixture SHA256:
`b01ac2d74e16dd1fc90c5eecc7793ec506d6e7570761238249494c7a3649da93`.

`LoadSchemaLexicon` now joins directory ordering, construct/supplement exclusions,
row loading, initial construction lookup, uncoded inference, frequency assembly,
final construction lookup, adjustments and compact compilation. The final
construction lookup is deliberately computed **before** adjustments, matching
the C# snapshot builder. It returns a private main table plus construction map;
it does not publish live state. Empty/missing directories yield empty data.

ARM64/x64 each pass 162 end-to-end comparisons against actual C#
`BuildLexiconSnapshot` main-table bytes and construction map: 80 multi-file schemas
under zh-CN/en-US, plus empty/missing paths. Tests include construct-file priority,
uncoded words in both sources, supplement exclusion, same-code file order, mixed
UTF-8/UTF-16 files and adjustments. Fixture-content SHA256:
`577e0d758f785f4a0518e18884be5d61b5f81cd6c522fb97559a6dd6ba30795f`.
The reference calls the documented pure snapshot builder on an uninitialized
test-only instance, avoiding constructor configuration writes and process startup.

Remaining schema work includes sentence supplements, pinyin ownership,
configuration overlays and live atomic publication.
Number signs currently retain parser defaults in this schema entry point;
non-default number-sign cultures need wiring, beyond the tested zh-CN/en-US cases.
The old custom loader exists for compatibility testing but is not called by this
pipeline, since current C# `BuildLexiconSnapshot` does not call `LoadCustom` either.

## Display and prefix metadata

`LexiconMetadata` adds comment/split/full-code maps, unique terminal codes,
nonterminal prefixes, short-symbol flags and automatic short-symbol keys to the
private schema result. `CodeSet` provides case-insensitive membership while
retaining original key spelling for enumeration. Deleted empty candidate buckets
remain relevant to short-symbol head detection but do not create nonterminal
prefixes or unique terminal candidates, matching the reference.

Comment and split files use their **raw directory enumeration** order, not the
schema-name sort. Repeated comments concatenate with spaces; repeated split keys
overwrite. Both decode escapes in the first two nonempty tab/space-separated
fields, without treating payload `#` as an inline comment. Full-code metadata is
keyed by commit text and keeps the longest code; equal lengths retain the first
code encountered. Prefix and short-symbol counts retain the C# algorithm.

The schema differential now compares all these fields against actual
`BuildLexiconSnapshot`, in addition to the compact bytes and construction map.
ARM64/x64 each pass the expanded 162-case corpus (UTF-8/UTF-16 annotation files,
repeated keys, escaped values, multi-character commit identities, empty adjusted
buckets and competing prefixes). New fixture-content SHA256:
`5a1498faa5c62698e1ce1a02c7568798bfdb2f0bd93049b1c284219d9ff81347`.
All three platforms' CTest suites include deterministic longest-code tie,
case-insensitive prefix and short-symbol regressions. Supplement records and live
display shaping are not part of this comparison yet.

## Independent pinyin loading

`LoadPinyinLexicon(executableDirectory)` resolves the executable-relative pinyin
reverse-table directory, reads only top-level TXT files in raw enumeration order,
and compiles stable frequency-sorted, normalized, exact-text-deduplicated entries.
It does not read YAML, recurse, apply schema filename priority or infer uncoded
words. Missing directories produce an empty compact image. Its result is separate
from `SchemaLexicon`, so the future runtime can retain one shared pinyin table
across schema switches rather than embedding it in each cached schema.

ARM64/x64 each pass 101 real-directory comparisons with actual C#
`LoadPinyinLexicon`, comparing compact bytes after mixed UTF-8/UTF-16 loading.
The fixtures cover casing, ü, repeated candidates, display/commit identities,
file ordering, excluded YAML/subdirectories and absent roots. Content SHA256:
`532d32a43b486b19c8b9329ab21053a4008cd0854a6dc71072c14b1643d31a3f`.
All three platform builds and existing CTest targets pass. Runtime ownership,
shared reload publication and temporary-pinyin key/UI behavior remain pending;
this test establishes table loading only, not a working reverse-input mode.

## Configuration parsing foundation

`ParseConfigLines` merges known keys over the 50 current C# defaults, retaining
default key spelling/order. It recognizes tab, ASCII space or comma separators
(not equals), allows explicit empty values, ignores unknown keys and keeps value
`#` text literal. Code-root normalization and boolean conversion follow the
reference; unsupported boolean strings return the caller's fallback.

`ConfigDefaults.h` is generated from actual `CoreRuntimeState.DefaultConfigPairs`:
`--native-core-config-defaults <new-output-path>`. SHA256:
`2ea6516adf0fd381ce7314120b7ee8b1e5789d44afae4f5ab70b7f720dde2ba6`.
Changes to production defaults require regeneration and a differential run.
ARM64/x64 each pass 1,012 requests comparing defaults, merge results and boolean
interpretations against reference orchestration using actual C# separator/path/
boolean helpers. Trace SHA256:
`5e538919828fc4a0642b0030938c8817e2a5215fd1cca00dec122d781551664b`.
CTest on all three platforms includes root preservation, empty override and
unknown-key regressions. The harness deliberately does not call ReloadConfig's
configuration writeback or autorun registration. File persistence, runtime version
publication, settings-derived behavior and startup integration remain pending.

`ReadConfigFile` now feeds the shared .NET-compatible byte/line decoder into
this parser. It is read-only: absent/unreadable files fail rather than publishing
defaults; the future runtime must own initial creation and transactional reload.
ARM64/x64 each pass an additional 112 actual-file comparisons against C#
`DetectTextEncoding` plus `File.ReadAllLines`, including UTF-8, both UTF-16/32
byte orders, BOMs, CR/LF/CRLF and invalid trailing bytes. The UTF-32LE BOM retains
the reference's UTF-16LE detection behavior. Tests verify input bytes remain
unchanged and missing-file requests fail without creating files. Corpus SHA256:
`541bde4f7957eef51b69a7e7ecdaef7487d1650b8ba18fc5139cee8d4183cf03`.
All three CTest suites pass on Linux, Windows ARM64 and x64. This does not yet
establish config persistence, path resolution or live runtime publication parity.

JSON uses the existing pinned nlohmann/json header under
`third_party/llama.cpp/vendor`; no llama.cpp model/scorer is linked at this stage.
See THIRD-PARTY-NOTICES.txt.

## Runtime path and schema selection

`ResolveCodeRoot` preserves C# root normalization and Windows rooted/relative
path semantics, including process-relative drive/root paths, extended device
paths and embedded-NUL rejection. `SelectSchemaDirectory` enumerates directories
before selecting an ordinal-ignore-case exact name or stable ordinal-ignore-case
first fallback; missing/empty/file roots produce no selection. It returns the
fallback decision but deliberately does not write config or advance live versions.
Windows ARM64/x64 each pass 1,130 read-only comparisons with .NET path/directory/
ordinal APIs across temporary schemas, Unicode names, case variants, relative,
drive-relative and extended paths. This probe mirrors the reference selection
orchestration without calling its persistence side effects. Linux rejects Windows
path resolution explicitly. Runtime assembly, cached switching, config writeback
and publication remain pending; this is not an input-capable runtime yet.

```powershell
python tools/test_core_native_paths.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

## Runtime lexicon ownership

`RuntimeLexicons` now assembles config, resolved schema/main metadata and the
independent pinyin image into an immutable shared snapshot. Writers serialize
disk preparation; readers acquire an atomic shared pointer without taking the
writer mutex. Full reload replaces both tables and clears the one-slot schema
cache. Explicit canonical schema switching retains the runtime-owned pinyin
pointer and caches only the schema just left. Missing/same-name switches are
no-ops; load exceptions leave the published snapshot and cache unchanged.
Retained reader snapshots keep their old buffers valid. Generation is a data
publication counter, not yet the input decoder's composition generation.

Windows `core_native_runtime` integration tests use private temporary files,
exercise A/B cache hits, third-schema eviction, disk edits, complete reload,
retained-reader lifetimes, missing-config failures, Win32 exclusive-file failures
during schema and pinyin loading, and 100 switches with a concurrent reader.
These establish native ownership/transaction behavior, not a full C# key-trace
comparison. In particular this preparation layer does not reproduce the current
C# switch's pre-load config writeback on failures: persistence and observable
failure policy must be reviewed when adding the runtime command layer.
No config writes, user-edit publication,
sentence supplements/model residency, composition migration or production IPC
are connected yet. The class is used only by isolated tests at this stage.

`RecentSchemas` now ports the two-entry, trimmed, ordinal-ignore-case MRU seed/
record/serialization rules. `RuntimeLexicons` seeds it on config load, records
successful schema loads/switches and mirrors the pair into the immutable config
snapshot. `SwitchRecentSchema` chooses a canonical existing other-history entry,
or the next sorted available schema on first use, and uses the same cache-aware
switch. It remains a data-layer API without shortcut dispatch or disk writeback.
ARM64/x64 each pass 10,000 randomized history/target requests; seed and record
invoke the actual C# private methods on an isolated uninitialized runtime, while
target selection mirrors its read-only orchestration. Trace SHA256:
`2a288b9da306f5b5791bae5d2b5c0a63d9f1fe409b345d0ace29600f1f5ec5cd`.
Native integration tests also verify recent A/B switching retains both the cached
schema image and shared pinyin image.

The extended directory probe now compares the entire `GetSchemaList` output,
not only the selected fallback. This exposed and fixed supplementary-vs-BMP
ordering in the new schema-name path: folded Unicode scalar order is required,
not raw UTF-16 order. Three minimal pair-directory cases exercise fallback too.
This correction is local to schema directory ordering; the existing compact
lexicon Unicode comparator gate remains open and must not be inferred complete.

```powershell
python tools/test_core_native_recent.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

## Explicit configuration persistence

`SerializeConfig` follows actual C# `BuildConfigLines`: header, known keys only,
default key order/spelling, tab separator, literal values including empties,
CRLF after every line and UTF-8 without BOM. Invalid UTF-16 surrogate units use
the same replacement encoding as `UTF8Encoding(false)`. ARM64/x64 each pass
2,002 byte-for-byte comparisons with the actual C# builder/encoder; trace SHA256:
`190efd261ee7b9aeb0e22a3520595133bb5144a0ff42415c445ac8c4a81f4c1e`.

`WriteConfigFile` explicitly creates/truncates the caller-selected file; on
Windows it uses write access with read sharing, matching the reference writer.
Like the reference it is not an atomic file replacement: a mid-write failure can
leave a partial file. It rejects embedded NUL paths, throws on open/write failure
and does not create parent directories. `RuntimeLexicons.SaveConfig` serializes
writers and saves a coherent snapshot without changing the data generation.
Tests write only private fixtures: exact disk bytes, read-back, truncation,
exclusive-file denial without changing the original file, missing-parent failure,
and recent-pair restoration into a new runtime followed by switching.

Automatic writeback during reload/switch/settings, config version semantics and
autorun registry updates remain pending.
No offline CLI command writes arbitrary config paths; the writer is currently
exercised through the isolated runtime tests only. Production directories are
unchanged.

```powershell
python tools/test_core_native_config_write.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`EnsureConfigFile` now creates missing parent directories and the default file,
without rewriting an existing regular file, following the reference first-start
helper's check/create sequence. `RuntimeLexicons.Initialize` serializes this
step with runtime writers before loading the data snapshot. Tests cover nested
parent creation, all-default readback, an absent default schema root with an
independently loaded pinyin table, byte-preserved existing comments/settings,
a directory at the config path and a file blocking its parent. Initialization
errors leave the previous published data unchanged (created directories/files
are not rolled back). Like C#'s helper this is not a cross-process create-once
transaction; the eventual host must establish single-instance ownership before
initialization. Default config path discovery, custom-file initialization and
automatic reload/writeback/autorun effects are still not wired into a host.

## Candidate range access

`ReadCandidatePage` normalizes code and reads only a requested compact-table
range. Empty normalized codes are not exposed even if the image contains an
empty-code bucket; missing codes and existing empty buckets remain distinguishable.
Page size clamps to 1..10 and page index clamps to the valid range, mirroring
the arithmetic of C# `InputMethodEngine.GetCandidatePage`. Only visible entries
are materialized (including unchanged packed display/commit tokens).
`RuntimeLexicons.GetCandidatePage` holds one snapshot for the entire query and
routes main/pinyin lookup to the corresponding image; returned strings remain
valid after schema changes.

Native tests on Linux/ARM64/x64 exercise 1,845 size/index combinations over 107
candidates, missing/empty codes, empty buckets, normalized casing/whitespace and
64-bit extreme requested indices. Windows integration checks main/pinyin routing
and retained page contents across a schema switch. These are native arithmetic
and ownership tests, not actual C# per-key differential traces. The stateful
selection and input/UI dispatch remain to be implemented; this API is the
data-access primitive only.

`CandidatePageTracker` now implements the stateful portion: exact raw UTF-16
code or mode changes reset the index, empty candidates reset to zero, changed
counts/sizes clamp the current page, and moves clamp at either end without
wrapping. A zero-delta move is a true no-op, including not changing identity.
ARM64/x64 each pass 50,000 operations against actual private C#
`GetCandidatePage`, `MoveCandidatePage` and `ResetCandidatePageTracker`, invoked
on isolated uninitialized objects with only config/lock/page fields prepared.
The harness never runs engine/runtime constructors, model services or IPC.
Trace SHA256:
`1e6e07fa6e217eb5727e05692a6a54defca75e3e6a009272e5106530597af3fe`.
Traces cover all six reference modes, raw casing/whitespace/surrogate changes,
candidate-count and page-size changes, resets and deltas -100,-1,0,1,100.
Movement uses widened arithmetic; it does not emulate signed-int overflow for
artificial extreme deltas (physical page actions use unit steps). This is not
yet integrated with key capture, candidate selection or composition edits.

```powershell
python tools/test_core_native_pages.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

## Output text/action preparation

`CandidateDisplayText` complements commit unpacking with the reference's empty
display fallback. `NormalizeOutputAction` accepts already-unpacked commit text
and returns ordinary output, open-add-word or toggle-hidden-candidates intent.
It supports the repeat buffer, armed decimal punctuation, nonempty pipe-separated
random choices (without trimming or recursive macro expansion), four date forms,
two time forms, localized day name and the fixed Chinese short weekday names.
Clock and random-index providers are lazy dependencies so tests are deterministic;
production local-time/culture/random providers are still pending. Repeat-buffer
and decimal-arm state transitions remain input-engine responsibilities.

Native tests exercise display/commit fallback, exact whole-token action matching,
random empty/space branches, no recursive action execution, dependency call counts,
date zero-padding, time, weekday and repetition. These are deterministic native
tests, not an end-to-end C# output differential. The module deliberately does not
execute actions or clear composition: wiring result flags, hide-setting persistence,
selection, actual commit and repeat-buffer updates belongs to the unfinished input
engine/host. No special-action UI or production IPC is invoked by tests.

`OutputServices` now provides Windows local-time fields, system-ICU standalone
weekday names and an independently owned uniform random-choice generator.
The invariant culture explicitly uses .NET's full English weekday names because
ICU root wide names are abbreviated. ARM64/x64 each compare 210 weekday names
(30 cultures, all seven days) to actual .NET `DateTimeFormat.GetDayName`.
Local-clock sanity and 9,900 random range checks run in native tests; random
seed/sequence identity with .NET is not promised or required for random phrases.
The service is designed for the serialized input engine and is not itself
thread-safe. Arbitrary thread-specific culture overrides, forced NLS/app-local
ICU and Windows user customization are not yet verified. The default host
binding into `OutputContext` and the actual output/commit pipeline remain pending.

```powershell
python tools/test_core_native_weekdays.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`OutputState` now owns the initial repeat text and decimal-after-digit flag,
overrides those fields when invoking macro normalization, and observes the final
post-normalization output. Handled nonempty output updates repetition even on
keyup; unhandled output does not. Decimal arming changes only on keydown, honors
unmodified top-row/numpad digit passthrough, and survives modifier keys including
CapsLock. Explicit clearing is exposed for the future focus/config/mode paths.
This is the corresponding subset of `PostProcessKey`, not its send-history,
smart-quote, diagnostics or event-dispatch logic.

ARM64/x64 each pass 135,168 comparisons with the actual C# digit eligibility
helpers: all UTF-16 units alone and before an ASCII digit, and every byte-sized
VK with all 16 modifier combinations. It preserves the single-text-element
restriction (half/full-width digits only, not combined/prepended digit clusters).
Trace SHA256:
`438a285b668522e5175b69e161ed3742d8b75b6d9583b79cc3a8e6f60f226808`.
Native state tests separately exercise handled/unhandled/key-up repeat updates,
modifier preservation, clear and normalization using retained state. A complete
per-key postprocessing differential and connection to actual commits remain open.

```powershell
python tools/test_core_native_output_digit.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`SendHistory` now stores committed text by text elements, exposes the most recent
20 elements for manual word creation, and tracks independent single/double smart
quote flags. Appending separate commits intentionally segments each commit on
its own, like C#; it does not recombine clusters across commit boundaries.
History itself remains unbounded as in the current reference (the public query
limit is not a storage limit). Passed-through Backspace removes one element;
quote deletion arms the corresponding quote state, while deleting other text
clears both deletion arms. Colon forcing and deleted-quote precedence reproduce
the source logic, including the existing non-obvious left-quote result after
deleting a right quote. Emitting a quote updates flags but does not append it;
the eventual postprocess path must append the actual final output exactly once.

Native tests pass on Linux/ARM64/x64 for empty history, Unicode clusters,
supplementary characters, backspace, both quote kinds, colon forcing and the
20-element query boundary. This module has not yet received a C# history trace
differential. Guessed passthrough history, host key filtering, postprocess ordering
and wiring to manual-add-word/actual commits remain pending.

`GuessPassThroughText` and `SendHistory.AppendPassThrough` now reproduce the
reference US-layout VK heuristic (space, Shift-XOR-Caps letters, shifted top-row
digits, numpad digits and the two English punctuation tables), rejecting
Ctrl/Alt/Win chords in the history wrapper. This intentionally is a guess, not a
keyboard-layout translation API. ARM64/x64 each pass 1,032 actual C# helper
comparisons spanning VK -1..256 and all Shift/Caps combinations.

`KeyPostProcessor` now composes macro/action normalization, unhandled-keydown
backspace and guessed text, final output history, and repeat/decimal observation
in reference order. Special actions clear result composition fields but return
intent instead of opening UI or toggling persisted settings. Native integration
tests exercise digit -> decimal -> repeat, Ctrl+A exclusion, backspace and add-word
intent. Full C# postprocess traces, handling nullable protocol result fields,
actual hide-setting execution, logging and use by an input-engine dispatcher
remain open. Default date/random providers must be supplied for those macros.

```powershell
python tools/test_core_native_passthrough.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

The postprocess/history integration now has an actual C# differential: ARM64/x64
each pass 20,000 steps invoking `PostProcessKey`, `EmitSmartQuote`, `GetLastCi`
and `GetSendHistoryCount` on isolated initialized fields (no constructors).
It compares result text/buffer/composing/add-word flags, recent history/count,
repeat buffer and decimal arm after every step. Scenarios mix keydown/up,
handled/unhandled text, all modifier flags, both smart-quote types, deletion,
colon, Unicode clusters, repeat and add-word macros. Trace SHA256:
`646a54e0fd4fd70b6ab5c78bee0c0b8920e563a2c0be4f72488e877fba88bfb7`.
The probe explicitly rejects the hide-candidates persistence action. Date/random
macros and logging are excluded; nullable output/buffer fields are compared as
empty strings, so protocol null/empty identity is not proven. This establishes
in-memory postprocessing parity for the covered traces, not frontend capture,
candidate selection, actual target-app commit or the complete input engine.

```powershell
python tools/test_core_native_postprocess.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`Punctuation` ports the plain Chinese resolver and shifted Chinese/English
symbol table choice, including English punctuation mode, slash/dunhao preference
and consuming the decimal arm on a plain period. Quotes are deliberately excluded
from these maps: quote/selection precedence remains the input dispatcher's job.
Shifted table access alone does not mutate decimal state; subsequent postprocessing
does, as in the reference. ARM64/x64 each pass 4,096 combinations of byte-sized
VK, Shift, English-punctuation setting, slash setting and initial decimal arm.
Plain cases invoke the actual private C# resolver; shifted cases inspect the
actual C# dictionaries with the same selection expression. Tests compare both
text (including missing/null) and resulting arm state. This does not establish
composition commit ordering or selector/shortcut/punctuation precedence yet.

```powershell
python tools/test_core_native_punctuation.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`CandidatePageKeyDelta` and tracker `MoveKey` now connect configured paging-key
recognition to the page index: brackets, Shift-Tab/Tab, PageUp/PageDown and the
default minus/equal pair. Config values are trimmed but compared case-sensitively;
unknown/empty values use the default pair. Unmatched keys leave tracker identity
and index untouched. A matched boundary key remains recognized even when its
page cannot move. Ctrl/Alt/Win, composition-mode eligibility, key-up filtering
and competition with code/selection keys remain dispatcher responsibilities.
The 50,000-operation ARM64/x64 page differential now additionally invokes actual
C# `IsNextPageKey`/`IsPrevPageKey` with mutable config and follows recognized
keys through actual `MoveCandidatePage`. Expanded trace SHA256:
`af6b290d8e17575834938e00192f9c1b2690e7ccc4267bda113b2053778c34d1`.
The prior tracker-only trace is historical; this expanded test is not yet a full
physical input trace or proof of key-dispatch precedence.

`SelectionKeys` now compiles rank 1..10 bindings with lowest-rank-first duplicate
resolution, top-row digit defaults, exact-side modifier overrides and generic
modifier fallback (including right Win -> left Win). Physical generic Shift,
Ctrl and Alt resolution preserves scan/extended behavior. Page-local selection
returns recognized/commit text: empty pages or out-of-range nondigit bindings
produce empty output; out-of-range bound digit keys emit the first candidate plus
the mapped rank digit (10 -> 0), not necessarily the physical digit.
Candidate commit packing is unpacked and text macros expand before appending
that digit. Add/hide tokens stay literal at this stage for final postprocessing.

Native tests cover these rank/modifier/default/empty/overflow paths, packed
candidates and repeat/add macro ordering on Linux/ARM64/x64. They do not yet
compare actual C# selection/composition completion end-to-end. Custom-selection
file loading, separate semicolon/quote setting paths, handled modifier key-up
suppression, composition clearing/mixed-prefix stitching and actual dispatcher
precedence remain pending. The selector consumes a supplied visible page; it
does not itself load a runtime table or publish input state.

`ParseSelectionBindings` now clones supplied base bindings and parses trimmed
lines with ASCII space/tab tokens, full-line comments, rank labels 1..10, explicit
empty bindings, unique key lists and last-line overrides. Numeric key tokens use
invariant signed decimal or .NET-style 32-bit hexadecimal (including signed bit
patterns); supported case-insensitive names match the existing C# name map,
including F1..F24 but not invented aliases. Inline comments are not accepted.
The parser returns the same category of Chinese error for an invalid label/key
and preserves previously parsed lines in its returned data, as the reference does;
callers must only publish when `success` is true. It never modifies the base.

Native tests cover defaults/empty overrides, duplicate numeric/name spellings,
signed boundaries, hexadecimal overflow, intentionally unsupported aliases and
partial/error results on all three platforms. ARM64 and x64 additionally each pass
11,983 differential cases against the actual private C# parser: successful status,
all returned bindings (including partial results on error), and exact UTF-16 error
text match. The suite includes every supported key name in both cases under every
rank, empty overrides, invalid labels, numeric limits and randomized multiline
input. This is parser parity, not input-dispatch or file-publication acceptance.
Trace SHA256: `afbdd9d8fab635c287ce77b2f85bdc974028c93151e34323cbcfd1c7b2db0bdd`.

```powershell
python tools/test_core_native_selection_config.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

The pre-existing decimal parser was extracted
unchanged as `ParseIntegerToken`; lexicon line parsing continues to call it with
its existing culture signs.

`ReadSelectionBindingsFile` reads an explicitly supplied file and returns complete
defaults on any read or parse failure (never the parser's partial bindings).
`RuntimeLexicons::ReloadSelectionBindings` uses the executable-relative custom
selection filename and publishes bindings plus their compiled key map together
in a new immutable snapshot. It reuses main/pinyin tables and preserves old
readers. Schema switching and table reload keep the current bindings; the future
host must invoke this distinct operation at engine startup and explicit selection
settings reload, matching the C# engine's ownership boundary.

The loader uses a separate StreamReader BOM policy, including UTF-32 LE detection;
the main lexicon keeps its intentional legacy UTF-16-first detection. Isolated
Windows runtime tests cover UTF-8 BOM, UTF-32 LE, missing and exclusively locked
files, whole-config fallback, empty bindings, duplicate-key precedence, snapshot
isolation and table reuse. Host invocation and physical key dispatch remain
pending. The read-only loader itself never creates files.

`BuildSelectionBindingsText` and `BuildSelectionFileText` generate the editable
ten-rank text and full commented file text. They preserve distinct key order,
emit invariant uppercase hexadecimal with two-digit minimum (negative Int32
values use eight digits), include empty/missing ranks, ignore out-of-range ranks,
and use CRLF without a final newline. Parsing and help generation share one key
name list, sorted by numeric value then ordinal name for file comments.
These builders produce UTF-16 text; BOM/UTF-8 encoding and disk writes are separate.

The selection-config differential now adds 1,001 formatting cases to the 11,983
parser cases: ARM64/x64 each pass all 12,984, comparing both generated texts to
the actual C# formatting methods, including all help lines. Expanded trace SHA256:
`f6e3b2f10972a7d7e91d26bd68fba1f9a07f520548d47954056a022d55bddb06`.
Native tests also verify format/parse/format stability with missing ranks,
duplicates, zero and negative key values. These differential cases do not prove UI
round trips, which still need implementation and end-to-end tests.

`WriteSelectionBindingsFile` writes full file text as UTF-8 with BOM using the
same extracted UTF-8 encoder/file writer as ordinary config persistence.
`EnsureSelectionBindingsFile` preserves an existing regular file and otherwise
writes defaults without creating parent directories, matching reference engine
startup. The check/create sequence assumes serialized startup, not cross-process
exclusive creation. `RuntimeLexicons::SaveSelectionBindings` validates supplied
lines before any disk mutation, creates the parent for explicit saves, writes,
then reloads and publishes a new snapshot. Invalid input returns the parser error;
I/O failures throw for a future protocol adapter to translate. Neither path
publishes new bindings on failure. As in C#, post-write read failure falls back
to defaults, and the file write itself is not atomic: a mid-write failure may
leave partial disk contents even though the in-memory snapshot is unchanged.

Windows isolated tests cover default-file BOM/content, preserving an existing
UTF-32 file, invalid-edit rejection, exclusively locked write rejection, successful
save/readback, empty bindings, old-reader isolation and main/pinyin reuse. The
shared encoder's 2,002-case C# byte differential was rerun on ARM64/x64 unchanged.
Startup host calls, conversion of a UI text string into lines, localized I/O error
responses and actual Dialog/IPC integration remain pending. No daily runtime files
are changed by these tests.

`OrdinaryComposition` starts the serialized ordinary-mode state owner: raw code,
page identity, lowercase-letter extension, known/prefix codes past maximum length,
unique-terminal automatic commit, dead-extension commit/restart or raw retention,
page-local configured selection, paging, Backspace, Escape, Enter, Tab and Space.
Empty candidate buckets do not count as valid extensions. Commit text conversion
occurs before leaving composition; global postprocessing remains the host's job.
The module receives an explicit schema/settings snapshot rather than reading
mutable runtime state midway through an operation.

This is an input-engine component, not an input-capable Core host. The caller must
route shortcut/modifier precedence first; mode switching, uppercase/pinyin,
mixed/sentence handling and physical key dispatch are not implemented here.
`EditKey` returns `nullopt` for an unimplemented branch, distinct from the explicit
Tab passthrough result: a future host must not silently pass unsupported branches.
Native traces cover page-two Space, long valid codes, dead extensions, terminal
auto-commit, literal Enter, unknown-code retention/reset, Backspace and Tab state.
An additional ARM64/x64 differential runs 120 randomized table/settings cases,
300 operations each (36,000 per architecture), invoking actual C# private idle
and composing key branches on a real engine with GUID-isolated configuration.
It compares handled/composing flags, commit text, owned raw code, response buffer
and the current candidate page after each operation. Lexicon content is ASCII in
this probe; generated punctuation is Unicode. Macros are absent and no postprocessing is invoked.
Idle nonletter operations other than enabled short-symbol starters are deliberately converted to `A` in both adapters
to keep this trace on the implemented composition branches. It is not a test of
the outer `ProcessKey` dispatcher, arbitrary Unicode output, modifier precedence,
key-up handling, or real IPC. Those remain required before deployment.

```powershell
python tools/test_core_native_ordinary.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

The composing `KeyDown` operation now orders shifted punctuation before custom
selection/edit keys, then semicolon/quote rank settings, unbound top-row digits,
plain punctuation, smart quotes and letter continuation. Plain punctuation uses
the reference's probe-then-idle-resolution sequence and converts a full-width
period after a digit-ending candidate into a decimal dot. No-candidate punctuation
clears composition without output. Pinyin re-entry still returns unsupported
without consuming it; the future outer dispatcher must implement that transition.
History/decimal state are supplied by the owning engine rather
than duplicated inside composition.

The same 120-case / 36,000-operation ARM64/x64 differential now includes shifted
keys, English punctuation mode, slash preference and semicolon/quote settings.
Idle steps deliberately enter ordinary lowercase or short-symbol composition. Dedicated
native tests cover a punctuation key rebound to custom selection, Shift precedence,
second-page constraints and no-candidate punctuation. This expanded trace remains
branch-level evidence, not acceptance of full physical key handling or IPC.

Short-symbol entry now handles enabled `;`, `/`, `[`, and `z`, including automatic
short-symbol output, repeated-key/empty-Space literal output and letter extension.
Bracket's early fallback precedes paging; the remaining quick symbols follow
editing and paging, matching the reference. Candidate-plus-short-symbol re-entry
retains the new raw code while suppressing that single response's composing echo
when the prior candidate produced text. It does not clear the new composition.
The ordinary differential now initializes the actual C# short-symbol metadata and
includes randomized starter entries, missing/empty roots and child codes in all
120 cases. Both architectures pass all 36,000 operations. Native regressions also
exercise explicit hidden-echo re-entry followed by successful short-code commit.

`UpperCaseComposition` implements uppercase-mode state transitions: shifted entry,
case-preserving letters, top-row digits, English punctuation commit, Space/Enter
commit regardless of the ordinary Enter-clear setting, conditional Tab clearing,
Backspace, Escape and unhandled-key state retention. Numeric separator acceptance
uses the invariant recognizer described below by default; commit conversion is a
mandatory injected service. Constructing without commit support fails. This avoids
silently treating timer/currency commands as literal English. Currency conversion
and timer scheduling implementations are described below; host wiring remains pending.

Native tests cover service invocation, append-vs-commit separator behavior,
clearing without commit, shifted quotes and unhandled numpad keys. The differential
adds another 120 cases / 36,000 uppercase operations against the real C# uppercase
branch, for 240 cases / 72,000 operations total per Windows architecture. Every
uppercase episode initially started with `A`; the later currency extension below
also starts with `D`/`S` and uses real conversion services. Results do not establish
parity for actual timer UI execution,
mode-switch integration, or production input readiness.

`IsUpperCaseNumericPrefix` replaces the missing numeric-prefix service with a
syntax recognizer matching C# `IsTimerOrCnum`: `D`/`d` and uppercase `S` prefixes,
invariant float syntax, ASCII numeric whitespace, Unicode-trimmed NaN/Infinity
fallback and numeric trailing NUL handling. No floating-point value is needed;
overflow/underflow still succeeds in .NET and thus remains a valid prefix here.
Comma-containing numbers are not accepted by this Float-style check even though
the eventual currency parser uses different rules. ARM64/x64 each pass 27,112
direct C# differential cases including special values, malformed exponents,
extreme exponents, whitespace, NUL and random inputs. Native tests additionally
verify the default service is called by uppercase separator handling. This does
not implement decimal currency conversion or actual timer scheduling.

```powershell
python tools/test_core_native_upper_numeric.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`ClassifyUpperCaseCommand` ports the timer/currency regex dispatch independently
of numeric-prefix recognition. It preserves case-sensitive `Ds`/`DS`/`S`, comma
acceptance, required final digits-or-commas, and .NET `$`'s single trailing-LF
behavior (not CRLF). `MakeUpperCaseServices` connects this classification to
mandatory currency/timer callbacks, preserving original command text; timer
requests suppress literal output, currency requests use the callback's result,
and noncommands remain literal. Real conversion/scheduling is still pending.

The numeric differential now compares both the actual C# numeric helper and
actual compiled regex fields across 37,544 cases per ARM64/x64 architecture.
Tests include malformed commands which the regex nevertheless accepts, rather
than "correcting" behavior by applying a stricter number parser. Native tests
exercise callback routing through uppercase Space/punctuation completion without
starting real timers. This evidence covers dispatch only, not decimal formatting,
timer timing, notifications, or lifecycle cancellation.

`ManualTimer` now provides the actual one-shot scheduling/lifetime mechanism and
a Windows desktop-information popup with the reference text/flags. It accepts
already-parsed minutes: nonpositive input leaves the previous timer intact,
positive input replaces it, milliseconds truncate toward zero, and nonfinite or
over-Int32 delays clamp to Int32.MaxValue. Its wait worker starts lazily on the
first valid schedule. Cancellation/destruction discard pending expiry and wake
the worker. Already-expired callbacks own their captures and run independently;
an open popup does not block a later timer or host destruction, matching the
reference's non-waiting Timer.Dispose behavior. No host pointer may be captured
without independent lifetime ownership. Callback failures are best effort.

Standalone timer tests use promise callbacks, never real popup windows. They
cover replacement, invalid-request retention, cancellation, zero-delay expiry,
delay limits and overlapping blocked callbacks through destruction. CTest gives
this concurrency test a 10-second deadline. The suite now has five Windows tests
and four Linux tests. Upper-command host wiring and visual notification acceptance are still pending;
the scheduler alone is not a completed end-user timer feature.

`ManualTimerCommandDelay` and `ManualTimer::ScheduleCommand` now connect admitted
`Ds`/`DS` commands to scheduling. The parser strips commas and the permitted final
LF, uses locale-independent floating conversion, handles overflow/underflow, and
applies the reference's multiply-then-truncate/clamp rules. Invalid/nonpositive
commands do not replace a pending timer. This API explicitly requires the existing
command regex grammar, not arbitrary strings passed directly to the private C#
scheduler: the admitted numeric alphabet is digits, comma and dot, so ordinary
CurrentCulture fallback is not needed after invariant parsing. Do not generalize
this parser to arbitrary localized numeric input without additional work.

ARM64/x64 each pass 12,015 ASCII command fixtures, comparing to actual .NET
double parsing plus the copied reference delay expression (not executing the
private scheduler). Coverage includes extreme magnitudes, subnormals, long
fractions, clamping boundaries, comma placement and final newlines. The isolated
native concurrency tests now also schedule through command text and verify an
invalid command preserves the pending callback. No visual popup was invoked.

```powershell
python tools/test_core_native_timer_command.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`ConvertUpperCaseCurrency` now implements regex-admitted `S` commands using
decimal digit strings, not floating point: 96-bit Decimal coefficient bounds,
scale at most 28, ties-to-even parsing precision reduction followed by the
reference custom format's two-place midpoint rounding. It emits financial Chinese
digits/units through 穰, suppresses zeros across groups, preserves the reference's
empty result for zero and its format-error text for invalid/overflow values.
The entry point is intentionally command-domain only, not a general localized
decimal parser. `MakeUpperCaseServices(timerCallback)` now uses this actual
converter by default while keeping timer ownership explicit.

ARM64/x64 each pass 22,533 cases against the actual private C# currency converter,
including all cent amounts 0..100, random up-to-33-digit integers/49-digit
fractions, commas, 96-bit limits and precision/rounding boundaries. A discovered
extra-zero bug across nonempty four-digit groups was fixed. The 72,000-operation
ordinary/uppercase differential now starts uppercase episodes with A/D/S and uses
actual numeric recognition/currency conversion. Its key alphabet excludes S, so
Ds/DS scheduling still cannot accidentally show a popup. Native tests additionally
verify S1 Enter emits 壹元整 through the real service. Host mode integration and
real frontend acceptance remain unfinished.

```powershell
python tools/test_core_native_currency.py --native next/_run/CoreNative/ARM64/Release/TigerClaw.Core.Native.Experimental.exe --dotnet dotnet --assembly next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
```

`PinyinComposition` adds temporary reverse-lookup composition with the leading ·
marker, shared compact pinyin table, mode-4 page identity, lowercase append even
with Shift, page-local selection, Space, literal/clearing Enter, Tab, Escape and
Backspace exit when the final letter is removed. Repeating the backquery key on
the bare marker emits ·. Unlike ordinary composition, paging and built-in
semicolon/quote selection precede custom selection. Shifted punctuation and smart
quotes are handled locally. Plain punctuation delegates the candidate-then-idle
transition to a mandatory host callback; that cross-mode host wiring remains
unfinished. A missing callback throws before changing composition/decimal state.
Only Start-generated marker compositions are supported here, not malformed
imported buffers containing a bare short-symbol starter in pinyin mode.

The differential adds 120 pinyin cases / 36,000 operations against actual C#
idle/pinyin branches and its real compact pinyin view, bringing the total to
360 cases / 108,000 operations per ARM64/x64. The pinyin key alphabet excludes
plain symbol re-entry; this is not evidence for that missing cross-mode path.
Native tests also cover custom-key conflicts, literal marker Enter, last-letter
Backspace, page-two selection and callback delegation. Candidate annotations,
frontend display and full input dispatch are still not integrated.

`CommitCandidateThenSymbol` now provides the ordinary/pinyin implementation for
that callback: clear old composition, commit the page candidate, then run symbol
re-entry into short codes or temporary pinyin. Empty candidates swallow the key;
a digit-ending commit followed by Chinese period emits an ASCII decimal point.
New composition state survives suppression of its one-response echo. Native
integration tests connect the real pinyin key branch to this callback, covering
comma, empty candidates, decimal output, backquery and slash-code re-entry.
This is not yet a full host dispatcher or a mixed-input resolved-prefix path;
the randomized differential above still excludes these new transitions.

`ChineseComposition` integrates idle, ordinary, uppercase and temporary pinyin
key-down dispatch. Its mode is derived from the retained composition owners,
not the response's composing flag: a punctuation-triggered restart can suppress
the response echo without losing the next input state. The ordinary backquery
path runs only after existing selection/paging/Shift priority. It shares tables,
settings and externally owned output/history state; the outer host must still
route language/modifier shortcuts and mixed/sentence sessions and observe final
output exactly once. It does not implement that outer host or IPC.

The differential now additionally drives 120 continuous Chinese-mode traces
(36,000 operations) through real C# idle/ordinary/uppercase/pinyin methods, with
no forced mode reset between keys. It compares response fields, retained raw,
current mode and visible page; the total is 480 cases / 144,000 operations.
These traces include plain punctuation and backquery re-entry, but exclude
modifier/language switching and timer commands. They intentionally do not call
the final output postprocessor on either side, so they are not evidence of
complete physical-key/commit/history integration.

`ChineseInputSession` now combines that dispatcher with one owned output/history
state and final postprocessing. In-branch macros use the previous repeat buffer
and decimal arm; only the completed response is observed. The caller must not
postprocess its response a second time. Another 120 continuous traces run actual
C# `PostProcessKey` after each branch and match the session, bringing the suite
to 600 cases / 180,000 operations. Native integration tests additionally cover
repeat-after-commit, history without duplicate observation, a passed-through
digit followed by decimal point, and C#'s deleted-quote inversion.
This entry remains Chinese key-down only, with no Ctrl/Alt/Win/CapsLock or key-up
dispatch. Settings actions are returned as intents; their host effects and
mixed/sentence/IPC integration remain pending.

`ActionShortcuts` ports exact modifier matching, gesture creation constraints,
reserved Ctrl+Space / Hook Alt+backslash / digit-reorder conflicts, and disabling
both colliding enabled action bindings. Its latch preserves one-shot add-word,
successful recent-schema switching, repeat suppression, and modifier-hold
rollover protection. Failed switches do not arm the latch. Physical key release
and final modifier release are separate calls because the reference routes them
at different points around modifier selection and Shift handling. `Reset` is
provided for focus/cancellation boundaries. Switch execution/migration is an
explicit callback; outcome values retain the distinct response-shaping branches.
Native tests cover latch sequences and duplicate bindings. The shortcut probe
compares 4,144 VK/modifier combinations, each valid gesture's 16 match masks and
four reserved-rule settings, to the actual shared C# implementation. Configuration
string parsing, settings reload and outer physical-key host wiring are not yet
implemented by this component.

`ShortcutGesture::Parse` and `ConfigString` now accept existing `Ctrl+VK_M`
configuration strings, ordinal-ignore-case modifier aliases and VK names, F1..24,
hex keys, .NET whitespace trimming and terminal-NUL hex parsing. Canonical output
orders Ctrl/Alt/Shift and uses named VK tokens where available. Duplicate
modifiers, Windows chords, Shift-only chords and multiple nonzero primary keys
are rejected. Preserve the reference quirk that a parsed zero key may precede a
later real key (`Ctrl+0x0+VK_M`). The shortcut differential now has 14,924 cases,
including 10,000 randomized strings, Unicode whitespace and invalid tokens.
`LoadActionBindings` applies runtime fallback from missing/empty/invalid configured
values to defaults, the legacy enable flags, reserved-key filtering and duplicate
binding suppression. `RuntimeLexiconSnapshot::actionBindings` is built from the
same configuration as the tables during reload, and is retained when copying a
snapshot for schema/selection changes. Old readers keep their old bindings. Tests
cover default add enabled/recent disabled, empty binding fallback, explicit
disable, reserved Ctrl+Space, reload to a new gesture and conflicting bindings,
plus old-snapshot isolation. The physical-key dispatcher still needs to consume
these bindings; parsing an empty value alone does not disable an enabled shortcut.

`ShiftToggleState` ports the reference Shift tracker for resolved keys: separate
left/right down states, chord-use suppression, modifier-selection skip, stray
release rejection and focus/reset clearing. Generic Shift follows the left side.
It reports interception/toggle decisions; language mutation and raw-code commits
remain host responsibilities. Preserve the reference behavior that releasing
each matched side can toggle even while the other side remains down. The caller
must mark other key-down events only after higher-priority handlers decline,
and call selection-release handling at its separate priority point. Native tests
cover repeats, both sides, chords, disabled toggle, blocking Ctrl, skipped
selection toggle and reset; full physical-key dispatch remains unfinished.

`CtrlSpaceState` ports the earlier-priority Ctrl+Space tracker, with explicit
UTC time injection and the reference's inclusive 250 ms Ctrl-up grace. It allows
the same Space-up/Ctrl-up fallback, excludes repeated Space-down toggles, disarms
on other key-downs while Ctrl is tracked, and resets on disabled or blocked
Ctrl/Space events. A successful decision must be applied by the host once, not
treated as an actual commit itself. Generic/left/right Ctrl use the reference's
single chord flag (this is not frontend physical modifier tracking). Tests cover
normal one-shot chords, rearming, grace boundary, missing down, Ctrl+M rollover,
reverse press order, repeat-only Space fallback, Shift blocking and disable.
No clock sleeps or production language/commit side effects are used in tests.

`ChineseInputSession::SetChinese` / `ToggleChinese` now implement external
language changes for its supported modes. Switching to English returns the raw
ordinary/pinyin/uppercase code (including the pinyin marker), records it in send
history, clears composition and decimal arm, and leaves repeat-buffer observation
to the original key-postprocessing contract. Redundant changes are no-ops.
English key-down passes through and still updates bookkeeping. `IsChinese` is
authoritative for language; `Mode` describes only the Chinese composition owner.
The differential adds 120 traces with interspersed actual C# `SetChinese` calls,
bringing it to 720 cases / 216,000 operations; direct tests cover literal marker,
S1 without currency conversion, redundant change and decimal-arm clearing.
Physical Shift/Ctrl+Space wiring, mixed/sentence suffix commits and CapsLock's
distinct candidate-commit rule still require the full host dispatcher.

The session now wires `CtrlSpaceKey` and `ShiftKey` to actual language changes,
literal commits and key postprocessing. These are separate priority stages so
modifier selection can run between them. `nullopt` means continue without
postprocessing; returned responses have already been observed and must not be
observed again. Empty Shift toggles pass through; Ctrl+Space toggles are handled
even without text. Preserve the existing C# history behavior: `SetChinese`
records a literal commit, and physical-key postprocessing records the response
again. Native integration tests cover actual ordinary/pinyin commits, repeat
buffer, history, empty toggles, suppression and reset. Full dispatcher ordering,
modifier-selection interception and physical event validation are still pending;
the prior differential count does not cover these newly wired stage methods.

`ModifierSelectionKey` now fills the priority stage between Ctrl+Space and Shift
for ordinary/pinyin sessions. It uses the current page and existing selection
map, clears composition on recognized selection (including empty/out-of-range
non-digit selections), skips the selected Shift's toggle, and consumes exactly
the matching resolved modifier's release. Uppercase/idle/English are excluded.
Postprocessing occurs once per returned response; unrecognized events return
`nullopt`. The host resets the held-selection set on focus/cancellation alongside
the other action/toggle trackers. Native integration tests cover generic versus
right-specific Shift, pinyin second rank, unmatched/repeated release,
out-of-range Ctrl selection, reset and uppercase exclusion. Full host dispatch
and differential physical-event acceptance remain pending.

The session owns `ActionShortcuts` and exposes the two release-priority hooks and
`ActionKeyDown`, translating add/switch/repeat/rollover outcomes into the original
response shapes and output action intents. A successful switch callback must
perform actual runtime switching and composition migration; tests use a stub
and do not establish schema-migration completion. Add-word leaves internal raw
untouched while suppressing its response echo. `OnFocusChanged` resets action,
language-toggle and selected-modifier latches, decimal state and pagination,
but preserves raw composition. `OnExternalCompositionCanceled` also clears raw;
`ResetCompositionForConfigChange` clears raw/decimal without resetting unrelated
key trackers. Native tests verify retained pinyin with page reset, add-repeat
rearming on focus, rollover response shape and cancellation distinctions.
Actual frontend focus publication remains outside this component.

`QuoteKey` now provides the early quote-event stage. It records down events,
suppresses duplicate output on their matching up, and implements missing-down
fallback for Chinese idle/ordinary/pinyin only. Fallback uses the current page's
first candidate plus a smart quote, not custom/third-rank selection, and clears
empty composition without emitting a quote. Ctrl/Alt/Win/CapsLock block fallback.
Unrelated non-modifier key-downs clear deleted-quote correction, while focus
resets the seen-down latch. Native tests cover normal pairs, missing down,
page-two pinyin, empty candidates, uppercase exclusion and focus reset.
Full physical-key host ordering and end-to-end differential traces are still pending.

`CapsLockKey` adds the post-shortcut CapsLock branch for supported modes. It
passes the physical key through while committing the full table's first entry
(not the current page); pinyin strips its marker for lookup and retains raw on
failure. Uppercase uses main-table lookup without currency/timer conversion.
After a nonempty composition it returns to Chinese idle. Pass-through commit
history is recorded but does not replace the repeat buffer. Direct tests cover
page-two pinyin, missing code, uppercase lookup, S1 literal and English idle.
The differential now inserts actual C# `ProcessKey` CapsLock events into ordinary
and language-switch traces: 960 cases / 288,000 operations per ARM64/x64 pass.
Other trace keys still use component branches, not the complete physical host;
mixed/sentence CapsLock and modifier interception remain separate integration work.

`ModifiedKeyDown` adds the post-action Alt/Ctrl/Win priority branch and
`PostProcessResult::cancelCompositionBeforePass`. Bare modifier keys preserve
composition; other modified keys cancel it before passing, unless ordinary-mode
page-local digit adjustment handles them. Adjustment callbacks receive the raw
code and packed candidate. Alt+digit consumes even a no-op advance, whereas
Ctrl+digit top / Ctrl+Shift+digit delete consume only successful changes. These
actions share the action-key latch, including repeat suppression and physical
release rearming. Tests use callbacks to verify operation, arguments and failure
paths; actual adjustment persistence is not wired here yet. Missing callbacks
throw before composition cancellation when an eligible candidate needs editing.

`ChineseInputSession::ProcessKey` now orchestrates the supported-mode event
pipeline: action validation/resolved VK, release latch, quote, Ctrl+Space,
modifier selection, Shift, action-key release, shortcuts, modified-key behavior,
CapsLock and ordinary dispatch. It accepts down/up aliases case-insensitively,
passes invalid actions and key-ups through postprocessing once, and retains
reference right-Control idle handling. Callback-backed runtime switching and
adjustments remain explicit; this is not yet an IPC host or mixed/sentence engine.
The differential adds 120 actual C# `ProcessKey + PostProcessKey` traces (36,000
events), including Shift down/up, quote fallback and Ctrl+C cancellation. Together
with component traces the suite is 1,080 cases / 324,000 operations. Those new
random traces exclude timer commands, mutation callbacks and timed Ctrl+Space;
native deterministic tests additionally cover scan-resolved modifier selection
and Ctrl+Space's priority over that stage. Production frontend acceptance and
complete runtime callback integration remain pending.

`RuntimeLexicons::AdjustCandidate` implements live advance/top/delete with
commit-text identity and preserved packed entries. Changes publish a new main
table and rebuilt unique/prefix/short-code metadata; construction/full-code/
comment/split metadata and pinyin remain unchanged, matching live C# updates.
No-ops do not advance generation. UTF-8 no-BOM CRLF adjustment operations append
to the active schema's 用户调整.txt using existing escaping; append failure does
not undo a published change. Windows tests cover snapshot isolation, no-ops,
new-code top, empty buckets, reload persistence, locked-log best effort and a
real unified Ctrl+2 -> runtime top -> reload sequence. This first implementation
rebuilds the compact image via a temporary full-table assembly; allocation and
large-table adjustment performance need follow-up, as do broader failure and
full differential tests. No production file was used by these fixtures.

`FixedLengthMixedDecoder` now ports full-raw fixed-length decoding: only segments
followed by another character become completed; missing candidates retain raw
casing, preferred text is keyed by raw start offset, and the active suffix plus
resolved prefix form the surface. It caches converted first candidates by
ordinal-ignore-case code and invalidates on lexicon version or explicit clear.
Resolvers are mandatory and exceptions propagate without caching a failed lookup.
Native tests cover segment boundaries, preference fallback, cache/version behavior
and raw versus Chinese commit composition. The standalone differential compares
10,004 cases to the actual C# decoder on ARM64 and x64: 5,000 randomized ASCII
cases, 5,000 randomized UTF-16 cases and four targeted cache/boundary cases,
including invalid/small max lengths and preferences. Unit-array transport
preserves embedded NUL, lone surrogates and splits inside surrogate pairs rather
than concealing them through JSON string replacement. Unicode fixtures use an
identity candidate resolver to exercise case-insensitive cache reuse independently
of display conversion. Mixed key editing/selection/session integration and full
schema migration are still pending.

`MixedComposition` now owns raw start/append/backspace/import and candidate
preferences. Appending beyond a full active segment can retain the current page's
converted first output. Reopening that segment by Backspace drops the preference,
so retyping cannot resurrect a stale selection. Import preserves raw casing and
drops all old preferences; rebuild clamps the engine max code length to 1..16
and publishes a revision for host page reset. Tests cover page-two preference,
backspace/retyping, imported raw, changed max length and empty edits. The host
must still wire this owner into the full mixed-mode dispatcher and target-schema
migration; import alone is not full schema switching.

The owner now has active-suffix candidate pagination, page-aware letter append,
and an edit-key stage (custom selection before page keys, Backspace, Escape,
Enter, Tab and Space). Completed-prefix text is prepended to Chinese selection
and Space commits, including missing-tail and out-of-range digit cases; Enter
instead emits the complete raw spelling unless configured to clear. Every
rebuild resets pagination. Deterministic tests cover page-two preference capture,
reopening/retyping, raw Enter, clear exits and prefix-preserving selection.
The active mixed `KeyDown` stage now handles shifted punctuation, semicolon/quote
selection, quote fallback and case-preserving letter append around that edit
stage. Plain symbols use the shared candidate-to-idle transition with an explicit
mixed prefix/session flag: even an empty mixed output may emit punctuation;
temporary pinyin re-entry retains its new owner while suppressing the commit
response's composition echo. Quick-symbol suffixes also prepend the mixed prefix.
Tests cover these paths without production IPC. Full mixed event differential,
unified session entry/lifecycle wiring, and schema-specific short-bracket priority
on imported symbol codes remain pending; this is not yet an operational mixed host.

`ChineseComposition` now accepts a runtime-bound mixed decoder and routes mixed
entry/key-down/pagination inside the Chinese dispatcher. Raw, ActiveCode and
Surface are separate accessors; mixed still uses the reference Ordinary mode ID.
Short-symbol and shifted-uppercase entry keep priority. Existing mixed sessions
continue even if the entry setting is disabled, and decoder version/max-length
changes rebuild before querying candidates or processing the next key. Callbacks
must outlive the dispatcher and resolve the current runtime snapshot; no table is
copied into the dispatcher. Enabling mixed without providing these callbacks
throws rather than silently using ordinary behavior. Native tests cover complete
letter-to-Space and letter-to-pinyin transitions. The outer quote-release edge
and schema migration remain incomplete; the production host must not enable
this partial integration yet.

The session now accepts that same injected decoder, exposes ActiveCode/Surface,
and uses the active suffix for candidate-adjustment callbacks and composition
echoes. Modifier selection prepends the mixed prefix; CapsLock resolves the full
table's first active-suffix candidate (or literal suffix) and prepends the prefix.
Language toggles continue to emit the complete raw code. Deterministic tests cover
these distinctions. Missing-key-down quote release remains a known reference
edge: C# CnComposing fallback uses only the active candidate and clears only its
input buffer, without combining/clearing mixed state. This requires a focused
behavior decision/regression, not an assumed ordinary-mode reuse. Full mixed
physical-event differential and schema/config migration remain pending.

Non-sentence schema migration now has a session entry: publish the target runtime
snapshot first, then call `RefreshAfterSchemaSwitch` with its settings. Ordinary
and mixed compositions retain the complete raw spelling and drop old preferences;
short raw becomes ordinary when mixed is disabled, while overlength raw becomes
a temporary mixed session. Pinyin/uppercase are retained with page reset. Mixed
Import also clears the converted-candidate cache even when the supplied version
is unchanged. Tests cover transitions in both directions, Enter after migration,
and same-version target data replacement. Sentence targets/uncommitted sentence
suffix migration and wiring this call into production runtime callbacks remain
unimplemented. This does not establish end-to-end schema-switch acceptance.

The Chinese trace probe now checks full raw composition separately from the
active-code buffer and supplies a fixture-bound mixed decoder. The event suite
adds 600 mixed traces (180,000 operations): Chinese key-down branches, optional
postprocessing, external language switches and CapsLock, with random tables,
page sizes/max lengths and punctuation/selection settings. Together with existing
traces this is 1,680 cases / 504,000 operations, passing on both ARM64 and x64
against the actual C# engine. These new mixed traces do not
include physical key-up/modifier ordering, schema migration or output macros;
the known isolated quote-release edge remains outside them. Both probes still
use isolated fixture data and never initialize production IPC or models.

The expanded suite adds 120 mixed physical traces (36,000 events) and 480
schema-setting migration traces (144,000 operations), totaling 2,280 cases /
684,000 operations, passing against C# on both ARM64 and x64. Physical coverage includes Shift releases, Ctrl+C and
CapsLock; quote events in this added group are down-only, explicitly excluding
the known isolated-up bug. Migration calls actual C# RefreshCompositionAfterSchemaSwitch
while changing max length/mixed enablement against the same fixture table, then
continues typing. It does not cover loading different schema files, sentence
targets, runtime shortcut callback wiring or real frontend focus timing.

`LoadOrdinarySettings` translates the canonical config keys into ordinary/mixed
input settings: .NET-compatible integer parsing with max/page clamps, existing
boolean parsing/defaults and trimmed/default page bindings. Number-sign arguments
allow a caller to supply its culture's signs; the default is +/-. Runtime snapshots
expose `InputSettings(decoderVersion)` so an event can derive settings from the
same immutable config it uses for tables and shortcuts. The caller must supply
the decoder lifecycle/version; the 64-bit snapshot generation is not silently
narrowed to int. Tests cover defaults, bounds, overflow, explicit switches,
Unicode whitespace and custom signs. Config monitoring, culture selection and
full serialized runtime event-host wiring remain pending.

`RuntimeInput` is the first isolated serialized event host over RuntimeLexicons:
it owns the session, binds mixed decoding to the retained current snapshot,
derives settings/toggles from config, routes recent-schema/adjustment callbacks,
and synchronizes published schema changes before input or candidate queries.
Event-local snapshots/settings stay alive across callbacks. Focus preserves raw
input; cancellation clears it. Windows tests load actual temporary A/B schema
files, type, switch, query the target candidate and commit, plus focus/cancel.
This is not a production server: callers must serialize runtime mutations;
IPC/replay/UI/model hosting, config-reload semantics, mixed macro lifecycle and
callback fault acceptance still need integration/tests. Sentence schemas are
explicitly rejected before accepting a snapshot until sentence decoding is wired.
The runtime object must outlive this noncopyable/nonmovable host.

ARM64/x64 runtime tests now exercise the host's actual callbacks using separate
temporary Old/New schemas with mixed input enabled: Ctrl+M migrates `aaaa`,
rebuilds the resolved prefix from the target table and preserves the active tail;
a held M cannot switch again, and releasing M/Ctrl permits normal continuation.
Ctrl+2 updates/persists the target candidate rank through RuntimeLexicons while
the previously completed segment retains its stored preference. Space commits
the old completed preference plus the new active winner. Reload plus schema
selection confirms adjustment persistence. All five native CTests pass on both
architectures. This verifies in-process callbacks, not TSF/Hook pipe delivery.

Runtime snapshot synchronization now checkpoints the session/settings/version
before applying a new snapshot and restores them on decode/conversion failure.
The runtime's published data is not rolled back; the next host access retries
the pending snapshot. A fixture switches an active mixed input to a date-macro
schema without a clock, verifies two failures preserve the original full raw
and surface, then supplies a clock and verifies migration completes. This local
checkpoint copies session state only on snapshot changes (not each key); its
cost with large caches/history still needs profiling. It does not undo external
provider side effects or make arbitrary mid-key dispatch transactional.
The host also owns a copy of repeat text and refreshes it before/after key
postprocessing and before page synchronization, avoiding a dangling view when
the session replaces its repeat buffer. General macro/exception differential and
faulted IPC response semantics remain pending.

Sentence work has started with `SentenceNgram`, a read-only view of the Windows
TCSKNM01 V2 model layout. It validates header/version, signed section counts,
range/trailing data, 4-GiB size limit and the positive unknown unigram, then
performs the reference binary lookups and double-precision interpolation/log
with optional unigram suppression. UTF-16 scalar resolution and observed-bigram
membership follow the C# implementation. Like the reference, this stage does not
scan all records for ordering/probability validity. Synthetic tests check known
interpolation, missing contexts/targets, probability floor, surrogate pairs,
every truncated prefix and trailing bytes. The view does not own/copy its image;
an immutable mapped-file lifetime owner, bounded score caches, real-model C#
differential and decoder integration are still required. No sentence schema is
enabled by this data-layer addition.

`ReadOnlyMapping` and `MappedSentenceNgram` now own the backing file/view without
copying model bytes. Windows uses read-only mapping and FILE_SHARE_READ, matching
the reference's write exclusion; Linux uses a read-only private mmap and requires
external writers not to truncate/mutate its backing file. Owners are noncopyable
and nonmovable; borrowed Model/Bytes views must not outlive them. Partial open,
mapping or parse failures release acquired resources through RAII. Windows
fixtures check mapped scoring, denial of concurrent writes, writable file after
normal destruction, parse failure and pre-map size rejection. Linux compilation
and existing tests pass, but equivalent Linux mapping-lifecycle fixtures are not
yet included. Real-model differential and bounded scoring caches remain next.

`tools/test_core_native_ngram.py` now compares mapped native scoring directly
with C# SentenceNgramModel on the actual Windows V2 model (SHA256
`6485240eb1a6acba3aa54ef4fdbb1f34c5d4b70a1caaff20aa2f296d5aca8b52`).
It samples 10,000 existing bigrams and 10,000 existing trigrams from the model,
adds 10,000 arbitrary scalar triples, 21 UTF-16/empty-token cases and 1,000
repeated queries. Both unigram modes plus observed-bigram membership are checked:
31,021 queries / 62,042 scores pass on ARM64 (maximum absolute error 0) and x64
(8.881784197001252e-16). Tolerance is abs 1e-12 / rel 1e-13, allowing platform log
rounding; this is not proof of bit-identical full decoder ranking. Both programs
map the same read-only model and use unit arrays to preserve lone surrogates.
The native scorer still has no cache; scoring-cache and Beam integration follow.

`CachedSentenceNgram` now wraps the raw immutable scorer with the reference
262,144-entry score cache and 65,536-entry observed-bigram cache. Hash mixing,
collision replacement, occupied/key checking and bit-63 separation of no-unigram
scores match C#. Entries are inline in fixed vectors; misses do not allocate.
The mapped owner exposes cached Model() and uncached UncachedModel(). Calls to
the cached wrapper must be serialized by the future decoder lock; it is not a
concurrent cache. Tests cover zero keys, forced collision replacement, clear,
repeated scoring and unigram-mode separation. Linux/ARM64/x64 native tests pass;
the real-model 31,021-query differential also passes again on both Windows
architectures with unchanged maximum errors (0 / 8.881784197001252e-16).
This verifies scores, not cache-hit performance or full Beam behavior.

`SentenceLexicon` now prepares decoder candidates from ordered source entries:
normalize codes, stable exact-text dedup, preserve source replacement order,
identify grapheme-single characters, and apply common-character/whitelist rules.
Primary grouping code prefers first-rank codes of length >=2, then shortest any;
optimal input code is independently shortest across all lengths/ranks. Equal
length ties retain source order. Filtering retains original candidate rank/log
rank and grapheme elements; proper prefixes/code-length sets include only kept
codes. Native tests cover one-key optimal input versus grouping code, filtered
rank gaps, whitelist, words, combining characters and supplementary scalars.
This index is not yet wired to Beam; source extraction from runtime metadata,
large-table differential/performance and full decoding remain pending.

`SentenceEdges` now enumerates eligible lexicon edges in code-length/rank order,
including suffix selectors, minimum consumed-end filtering, leading-only short
symbols and the one-key consumption rule. Explicit ranks select original ranks;
implicit non-first words require a whole-input edge, while the duplicate-single
switch admits non-first grapheme-single entries on segmented paths. Suffix scans
use a generated .NET 10.0.11 BMP char.IsDigit table; invariant integer parsing
retains overflow/non-ASCII failure and the distinct `0` (rank 10) versus `00`
(rank 0) behavior. Tests cover these boundaries and prefix-position constraints.
Edges borrow candidate pointers from the immutable index. Raw normalization,
reachability, score accumulation, Beam aggregation/pruning and asynchronous
generation handling are still required; this is not a completed decoder.

`SentenceBeam` now provides scored edge advancement and per-position buckets.
Advancement retains previous two graphemes, supplemental automaton state, maximum
rank and shared raw/text boundary links. Rank penalties, character reward and
ranking-only supplement/optimal-single rewards follow the reference arithmetic;
explicit selectors bypass rank penalty and optimal-single reward. Buckets merge
same-text log mass while retaining the rank-first representative, support either
rank-first or score-first top ordering, freeze after limiting, and preserve the
truncation flag when reopened. Unlike C#'s 256-item delayed aggregation, this
initial implementation aggregates immediately and uses partial_sort for exact
top selection; performance comparison remains pending. Tests cover reward/mass
separation, duplicate representative selection, merged mass and reopened pruning.
The full-recompute lattice below now connects expansion and final scoring;
evidence and the incremental decode cache are not yet connected.

`DecodeSentenceLattice` connects normalized raw input, ordered lexicon edges,
Beam advancement and final EOS/isolation adjustment. It preserves per-position
buckets and boundary chains, exposes expanded-state count and terminal Beam
truncation, and emits top candidates with score/mass distinctions intact. Expansion
order remains code length -> current state -> candidate, so duplicate mass and
boundary tie behavior are not accidentally reordered. Final score-first sorting
requires the duplicate-single switch plus at least one multi-edge completion;
whole-edge-only sets remain rank-first. Tests cover plain/explicit-selector
sentences, segmented-code rendering, whole-input rank priority, dead input and
empty normalized input. This is a full-recompute foundation with injected scoring,
not a complete SentenceInputDecoder: runtime sentence decoder wiring,
prefix constraints, incremental cache, early evidence, Qwen/generation
control and real-table/live-host end-to-end acceptance still remain.

`tools/test_core_native_sentence.py` compares this full-recompute lattice with
the actual C# `SentenceInputDecoder.DecodeFull`, using neutral transitions by
default, or a read-only V2 model with `--model`. `--supplements` enables overlapping reward entries;
`--isolation` enables the isolated-character scoring comparison. Its 4,000 deterministic cases mix malformed/dead
raw input, explicit rank selectors, common-character/whitelist filtering, optimal
single-character rewards and dense ambiguous lattices up to 24 keys. It compares
normalized raw, expansion counts and every visible candidate's text, segmented
code, maximum rank, base score and confidence log mass (score tolerance 1e-12).
This caught and fixed the missing C# per-UTF-16-unit `char.IsLetter` entry guard:
selector/symbol-only input, including supplementary letters without any BMP
letter, produces the empty result rather than entering the lattice. Dedicated
native regressions preserve that distinction. Full real-table decoding,
runtime sentence decoder wiring, incremental behavior and live-host acceptance remain
separate gates; this test does not claim those are complete.
ARM64 and x64 each passed all 4,000 cases / 8,614 candidate comparisons.
After the guard fix, Linux passed all four CTests and Windows ARM64/x64 each
passed all five CTests. These checks use only isolated experimental outputs.

`SentenceNgramTransition.h` connects the mapped/cached V2 scorer to lattice
transitions. Turning boundary scoring off removes only the unigram backoff when
either history token is BOS or the target is EOS; higher-order scores remain.
Both boundary policies are exercised by the differential. With the canonical
`sentence-ngram-v2.bin`, ARM64 and x64 each passed 4,000 cases / 8,614 candidate
comparisons without isolation, with maximum score/mass difference zero in this
fixture set. The probes load the model once per process through the isolated
`TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL` environment variable; the test script
sets/clears it for child processes only. There is no production-model deployment.

`SentenceIsolation.h` implements grapheme-based rare-character penalties using
injected rank and observed-bigram providers. Either neighboring observed bigram
exempts a character; otherwise constant or logarithmic rank weights apply.
Native regressions cover both neighbors, threshold/disabled cases, unknown ranks
and combined/supplementary graphemes. Both differential sides now independently
load their embedded canonical character ranks; no rank values are injected into
the native scorer. This does not wire the experimental decoder into the live
input host.
With `--model ... --isolation`, ARM64 and x64 each passed the same 4,000 cases /
8,614 candidate comparisons with zero maximum score/mass difference. Linux four
CTests and Windows ARM64/x64 five CTests passed after the isolation addition.

`SentenceCharacterRanks` embeds the same authoritative C# rank TXT through CMake,
with an explicit configure dependency so data edits regenerate the binary input.
The process-wide immutable default is initialized lazily once, independently of
schema snapshots. Parsing uses StreamReader-style decoding, .NET whitespace trim,
whole-line `#` comments and first-occurrence rank assignment with ordinal identity.
The frozen sorted array supports allocation-free rank lookup and `TakeTop`; unknown
text returns 20001. Source-order ranks remain intact despite lookup-order sorting.
The temporary construction hash map is released after initialization. Tests cover
CR/LF/CRLF, duplicate/blank/comment lines, supplementary text, case distinctions,
unknown values and bounded/unbounded top sets. The sentence differential with
`--isolation` additionally compares every canonical rank and six top-set limits
against C#, before checking the real-model lattice. Integration with the full
runtime sentence host, including lifecycle and decode locking, remains pending.
ARM64 and x64 each passed 20,003 rank queries (all 20,000 canonical entries plus
three unknown cases), all six top-set limits and 4,000 real-model/isolation
decodes with 8,614 visible candidate comparisons, maximum score/mass error zero.
Linux four and Windows ARM64/x64 five native CTests also passed after this change.

`SentenceSupplementMatcher` implements immutable grapheme-based trie/failure-link
matching. Weight creation clamps to 1..1,000,000,000 before the shared log reward
formula and 0..16 reward clamp. Duplicate entries and suffix hits take the maximum
reward at each emitted position, not the sum. State survives code-edge boundaries;
empty elements and invalid input state follow C# reset semantics. Read-only map
transitions accept string views without allocating temporary lookup keys. Native
regressions cover suffix/overlap matching, duplicate reward priority, grapheme
boundaries, weight clamps and state reset. Lattice advancement carries this state
and accumulates supplement rewards in ranking/SupplementScore while excluding them
from confidence log mass. The `--supplements` differential compares that separate
score as well as candidate order, segmentation and confidence. Live sentence
host integration remains; file loading and snapshot ownership are described below.
With `--model ... --isolation --supplements`, ARM64 and x64 each passed 4,000
decodes / 8,614 candidate comparisons, including supplement score and confidence
mass, with maximum numerical difference zero. The 20,003 embedded rank queries
and six top sets also matched. Linux four and Windows ARM64/x64 five native
CTests passed after the matcher addition.

`LoadSentenceSupplements` loads the schema's supplemental corpus using the shared
legacy lexicon encoding policy. It strips inline comments, trims with .NET rules,
splits only ASCII space/tab, defaults missing weight to 1000 and accepts only
positive invariant Int64 weights (including the reference's trailing-NUL behavior).
Overflow, nonpositive weights and extra fields are ignored. Last valid duplicate
value wins without changing first insertion order; the reward builder then clamps
accepted weights. `SchemaLexicon` owns immutable shared supplement entries, so
recent-schema caching follows the existing schema lifecycle and candidate edits
share the same supplement image instead of copying it. A full schema load rebuilds
the entries; no live decoder/matcher cache is implicitly added.

`tools/test_core_native_supplement_files.py` passed 302 path cases / 2,017 loaded
entries on ARM64 and x64 against the actual C# file loader, covering UTF-8 with/without
BOM, UTF-16, UTF-32, missing directories/files, duplicate values, comments, Int64
limits, signs, whitespace and trailing NULs. Native parser and runtime tests also
verify invalid duplicates cannot erase valid values and adjustment publication
retains the old immutable supplement pointer. Linux four and Windows ARM64/x64
five CTests passed after this integration.

`RuntimeSentenceDecoder` now owns a full-recompute decoding configuration: a
mapped V2 model and its caches, normalized lexicon, character-rank policy,
isolation policy and supplemental matcher. It extracts/deduplicates actual commit
text from compact candidates before assigning sentence ranks, matching the C#
runtime snapshot path rather than treating display labels as model input. Source
schema storage is not borrowed. One mutex covers all score/bigram cache accesses
across a complete decode; concurrent callers serialize, and returned lattice/text
results own their storage. Model load failures propagate rather than silently
pretending to support sentence mode with neutral scoring.

Windows tests construct the owner from an isolated mapped model and compact
schema, check reward/confidence separation and commit-text deduplication, mutate
the source schema, then run four concurrent callers (80 decodes total) against
the retained configuration and verify mapping release on destruction. The text
suite also checks compact-to-sentence extraction. This is a runtime decoder
building block, not physical-key host integration: schema/settings publication,
asynchronous generation rejection, retained context/prefix constraints,
early commit and the existing RuntimeInput sentence rejection still
need to be completed. Each instance currently owns its own mapping/caches;
cross-generation model sharing and isolation-result caching remain future work.
The owner/concurrency regressions passed on Windows ARM64 and x64, along with
all five native CTests on each architecture. Linux passed all four CTests,
including the standalone compact-to-sentence extraction check.

The lattice now supports reference-style incremental state reuse for a fixed
immutable lexicon/scorer/settings configuration. Same normalized raw input returns
the previous result; transitions involving at most four keys rebuild. Longer
appends expand only edges beyond the old raw boundary, beginning at the maximum
code length plus trailing selector span. Prefix deletions retain the existing
position states and re-emit without expansion; unrelated edits rebuild. The
runtime owner exposes a mutex-protected `Decode` with shared immutable result
snapshots and `ResetCache`, while `DecodeFull` remains an independent full decode.
Exceptions do not publish partially constructed cache state. To preserve callers'
old results, this first implementation copies retained buckets; performance and
memory optimization of that copy remain unmeasured and pending.

`tools/test_core_native_sentence.py --model ... --isolation --supplements
--incremental` compares 300 continuous 50-edit sequences to the actual C# incremental
decoder, including repeated input, append, selector changes, deletion and replaced
raw. ARM64/x64 each passed 15,000 results / 13,423 candidates with identical
expansion counts, order, segmentation and scores (maximum numeric error zero).
The same run checks all 20,003 rank queries and six top sets. Native owner tests
cover normalized cache identity, old-result lifetime, deletion reuse and reset;
Linux four and Windows ARM64/x64 five CTests passed. These results establish
reference parity for the tested sequences, not universal equivalence between
incremental and full recomputation or acceptance of a physical-key host.

`SentenceDecodeWorker` adds a serialized asynchronous executor with a single
coalescing pending slot. Every submit/cancel advances its generation; completed
stale jobs cannot publish and results can be taken only once. Exceptions are
returned as generation-tagged failures so the worker survives later requests.
Shutdown discards pending/unread results and joins a running synchronous decode
before releasing the provider. There is no forced cancellation of model work.
The provider must retain its decoder for the worker lifetime. The eventual input
host must recheck generation under its composition lock when applying a taken
result; taking a result does not freeze future submits or replace host-level
manual-selection/Qwen generation guards.

Dedicated semaphore-controlled tests cover coalescing, stale result rejection,
cancel while running, single consumption, exception recovery, unread cancellation
and draining shutdown. Windows runtime tests connect the worker to the actual
mapped-model RuntimeSentenceDecoder. Linux passed all five CTests and Windows
ARM64/x64 all six after adding the worker. A newly isolated translation unit
also exposed missing explicit algorithm includes in the sentence edge/Beam
headers; these are fixed. Physical-key composition integration, UI pending-state
stitching, synchronous completion races and Qwen wiring remain pending.

`HasCompleteSentenceCandidate` adds exact lexicon-path reachability without Beam
or language-model evaluation. It tracks only required-prefix progress and equality
with an optional excluded complete output. Distinct segmentations of the excluded
surface text never count as another candidate. Group-eligible queries restrict
implicit candidates to first rank unless the raw input contains an explicit
selector, matching C#; normal duplicate-single and explicit rank rules are still
enforced by edge enumeration. Raw normalization/letter guards are shared with the
lattice. The runtime decoder exposes the query on its immutable lexicon, without
touching mutable model/decode caches. Early-commit callers are not yet wired.

The sentence differential's `--paths` mode compares 60,000 prefix/excluded-text/
group-eligibility queries against actual C# `HasCompleteCandidate`. Native cases
also cover same-text alternate segmentations, impossible/partial prefixes, empty
exclusions, whole-input non-first ranks and explicit selection. This is the
uniqueness-check prerequisite for empty-code automatic commit, not completion of
the automatic-commit state machine or its confidence evidence aggregation.
ARM64 and x64 each passed all 60,000 query comparisons. Linux five and Windows
ARM64/x64 six native CTests passed after the query addition.

`BuildSentencePrefixEvidence` aggregates an already-narrowed evidence pool using
confidence log mass only, never ranking scores. It preserves reference insertion
order for `(text prefix, raw boundary)` pairs, normalizes prefix and boundary
mass separately and counts each raw boundary once per candidate. Boundary closure
uses the shared 0.99999 threshold, allowing negligible crossing paths without
requiring candidate text agreement. Empty pools and invalid text offsets follow
the reference guard behavior. Tests check text disagreement on a shared boundary,
material/negligible crossing paths, reward independence and empty input.

`tools/test_core_native_prefix_evidence.py` compares the actual private C# prefix
aggregator through an isolated test probe: ARM64 and x64 each passed 5,000 pools /
135,004 prefix records, including Unicode UTF-16 offsets and extreme finite log
weights. Maximum numeric error was 0 on ARM64 and 1.1102230246251565e-16 on x64;
all closure decisions matched. This is the aggregation primitive; the collector
below now connects visible/dropped-tail pools and proposal selection. The
generation-by-generation decision tracker is described below; live runtime wiring still remains.
Linux five and Windows ARM64/x64 six CTests passed after this addition.

`BuildSentenceEarlyEvidence` collects the already-selected visible candidates,
filters by the retained text prefix, and merges evaluated states immediately
before proper incomplete letter tails. A tail of at least two keys which is also
an exact lexicon code is not treated as incomplete. The complete-code path never
widens to the full Beam. Duplicate `(text, raw endpoint)` mass is combined while
retaining the higher-confidence boundary representation; partial Beam truncation
is propagated only when an eligible partial candidate actually contributes.
Evidence includes independent prefixes/boundary shares, neutral incomplete-tail
and low-confidence flags, the longest eligible compatibility proposal and its
raw-boundary projection. This does not execute any commit.

The sentence differential's `--evidence` mode compares all collector outputs,
including explicit required prefixes, against actual C# `EarlyCommitEvidence`.
Native regressions cover incomplete-tail support, required-prefix exclusion and
complete input without a spurious tail merge. Runtime result/cache integration is
described below; the input engine's actual commit application remains pending.

`RuntimeSentenceDecoder.DecodeResult` now publishes an immutable pair of lattice
and optional early-commit evidence. Cache identity includes normalized raw code,
the evidence switch and required text prefix. Changing evidence options on the
same raw reuses states without reporting new expansion; it cannot return the prior
prefix-filtered evidence. Candidate/evidence computation and allocation finish
before publishing the pair under the decoder lock, and old pairs retain their own
storage. Reset clears both cache projections. The existing `Decode` API projects
the lattice for callers which do not request evidence.

The worker is now `BasicSentenceDecodeWorker<Result>` with the original
`SentenceDecodeWorker` alias preserved. Runtime integration tests also exercise
the paired result type so one generation delivers candidates and evidence
together, with single-consumption semantics. Tests cover evidence off/on,
normalized repeat hits, changed/impossible prefixes, old-result preservation,
zero-expansion re-emission, cache reset and async paired delivery through the
mapped decoder. Linux five and Windows ARM64/x64 six CTests passed. This still
does not apply a commit or implement per-request mutable host settings: provider
configuration must remain stable, and physical-key/Qwen generation guards plus
the host-side commit state machine remain pending.
With real model, isolation, supplements and `--evidence`, ARM64 and x64 each
passed 4,000 full decodes / 8,614 visible candidates plus all early-evidence
fields (floating tolerance 1e-12), along with 20,003 rank queries. Linux five
and Windows ARM64/x64 six CTests passed after the collector addition.

`SentenceCommitTracker` now tracks independent `(text prefix, raw boundary)`
evidence across consecutive one-key generations. It applies the production
three-evidence/two-strong-evidence thresholds, retains supported comparison gaps
for at most three generations, rejects stronger divergent forks, and enforces
the retained-code floor and spacing from the previous commit. Repeated evidence
does not increment counts; stale results, suspension, obsolete lexicons and
truncated confidence reset the tracker. Current and immediately preceding raw
generations are accepted. Visible boundaries are required unless the evidence
contains merged incomplete tails. An accepted neural top constrains prefixes;
a supplemental visible winner takes precedence over that constraint.

The tracker returns an owned full-prefix proposal, not an OS commit. The host
must serialize observation/application, commit only the suffix beyond the old
committed prefix, update raw/text context and generations, and guard neural-result
identity. These host operations and empty-code automatic commit are not connected
yet. Dedicated native tests cover thresholds, repeated/skipped/stale generations,
gap expiry, forks, suspension, truncation, retained-code limits, committed text,
visible versus dropped-tail boundaries, previous-commit spacing, neural and
supplement constraints, and lexicon invalidation. Linux six and Windows ARM64/x64
seven CTests passed after these additions. This is native deterministic coverage,
not yet a C# tracker differential or physical-key end-to-end acceptance test.

`SentenceCompositionContext` provides host-owned raw/committed-prefix storage.
It retains original casing and decoder context after prefix application, returns
only newly committed text, and rejects proposals that fail to extend the active
raw/text prefix. All output allocations precede state mutation. Candidate suffix
projection requires the committed text prefix; incompatible or absent candidates
fall back to the live raw suffix. Literal exits likewise return only live raw.
Backspace consumes the last live key by clearing the composition, never by
deleting retained committed context into a new composition. Tests exercise two
successive commits, invalid proposal non-mutation, candidate mismatch, case
preservation, final-key deletion, literal exits and punctuation completion.
Linux six and Windows ARM64/x64 seven CTests passed. This storage primitive is
not yet wired into RuntimeInput: generation validation, tracker reset on edits,
candidate-list projection, continuation rank policy and actual OS commits remain
host responsibilities. No production deployment was performed.

`SentenceCompositionSession` connects the context and prefix tracker to a
host-side result-application boundary. Owned requests carry generation, lexicon
version, original raw and required prefix; application rejects stale requests,
wrong normalized decoder raw and duplicate delivery. The visible projection
restores original casing without copying the full lattice, filters incompatible
text prefixes, and remains available while an appended key is being decoded.
Manual selection suspends automatic prefix commits until an edit; backspace
resets evidence. A successful prefix decision applies only its new text suffix,
filters visible candidates, and advances generation so in-flight results cannot
undo the commit. Lexicon invalidation clears results/evidence while preserving
composition context (schema migration is a separate, still-unwired operation).
The worker must retain and return the request identity, not substitute its own
scheduling token. Tests cover stale/duplicate delivery, uppercase raw identity,
two-generation commit, post-commit filtering, manual-selection protection and
lexicon invalidation. Linux six and Windows ARM64/x64 seven CTests passed.
This remains an isolated session layer, not a physical-key runtime: worker
request routing, pending-display stitching, neural reranking/freeze, empty-code
commit, complete key dispatch and IPC/OS commit delivery remain unfinished.

The worker now accepts `BasicSentenceDecodeWorker<Result, Input>` (default Input
is the original owned UTF-16 raw string). `SentenceCompositionRequest` can travel
unchanged through Submit/provider/completion, including its required prefix and
lexicon version. Worker scheduling generation remains separate from composition
generation. A semaphore-controlled regression mutates the caller's original
request while the provider is paused and verifies that both decode and completion
retain the submitted snapshot. Windows integration now routes owned requests
through the actual RuntimeSentenceDecoder with a synthetic mapped-model fixture
and applies its lattice/evidence to SentenceCompositionSession. It tests original
uppercase raw preservation, duplicate application, a taken result becoming stale
before application, and recovery with the newest input. Linux six and Windows
ARM64/x64 seven CTests passed. This validates the request/result route in isolated
tests; a production event loop and physical-key host are still absent.

Session display now projects candidate boundaries onto the original-cased raw
buffer, stitches an appended pending suffix, trims a pending backspace prefix,
and falls back to live raw after a divergent edit. Committed raw is removed only
from display. `FinishSelected` returns no decision while decode is pending; a
host must obtain/apply the current decode before retrying, rather than commit an
old visible candidate. With a current result it emits the selected text suffix
plus punctuation, or literal live raw when no candidate exists, and invalidates
the old generation. Native tests cover segmented casing, append/backspace/divergent
pending display, deferred completion, selecting a later candidate and empty-list
punctuation fallback. Linux six and Windows ARM64/x64 seven CTests passed. These
are session APIs; key-to-action routing and UI publication are still unfinished.

The session now dispatches inner sentence control keys: Backspace, Escape,
Enter, Space, Tab/Shift+Tab and Up/Down. Navigation wraps only the visible page
(clamped to 1..10); Enter follows its clear setting; Tab clears an empty result
only when configured. Space on an empty candidate list leaves the composition
intact, unlike punctuation's literal fallback. Candidate-dependent controls
return `needsDecode` without mutation while a result is pending, so the host can
obtain the current generation and retry only the inner dispatch. Cancellation
invalidates outstanding requests. Tests cover navigation direction/page limits,
empty-space retention, clear settings, selected commit, cancellation and the
decode barrier. Linux six and Windows ARM64/x64 seven CTests passed. Letter/rank
append, empty-code auto commit, modifier/global shortcut handling and output
postprocessing are not routed through this inner control dispatcher yet.

`SentenceEmptyCodeTracker` implements the separate empty-code decision lifecycle.
It captures a current group-eligible candidate before an ordinary letter, tests
the extended raw for any complete path, defers while the selected last segment
is a proper code prefix or the retained-code minimum is not met, and checks
the original raw for another eligible output immediately before a unique-branch
commit. The strong-confidence branch uses eligible candidates' log mass, requires
an untruncated evidence pool and the visible winner, and does not replace its
acceptance rule with a uniqueness query. Explicit rank marks preserve rank
eligibility; a non-letter append cancels pending state. Its callbacks are exact
lexicon/proper-prefix queries, never Beam/model calls. RuntimeSentenceDecoder
now exposes the proper-prefix query alongside exact complete-path queries.
Returned proposals contain full text and the original raw boundary; share=0 is
an unused placeholder, not a probabilistic confidence estimate. The host must
apply the new suffix, retain all appended raw, reset on edits/navigation/settings,
and activate first-rank-only continuation after this kind of commit. Session
integration is described below. Callback-based tests cover deferral, hidden alternate
output rejection, minimum-retained raw and selector cancellation; this is not yet
a C# differential or real-key acceptance test.

`SentenceCompositionSession.AppendWithAutoCommit` now connects empty-code checks
before the append and probabilistic evidence after it, without a Beam call on
this path. It stages context/pending-state changes before exact queries finish,
preserves all deferred letters, applies only new committed text, invalidates the
old generation and filters visible text prefixes. Empty-code commits activate
first-rank-only continuation; explicit rank marks lift that projection, while a
probabilistic commit restores normal ranked-path eligibility. Disabled automatic
commit clears both trackers. Backspace, navigation, cancellation, lexicon changes
and final completion clear pending empty-code state. The raw append API is for
seeding/import only; live sentence append must use the automatic-aware API even
when disabled, including its 128-live-key limit. Tests use an actual SentenceLexicon
and exact path query to verify delayed commit, preserved suffix, continuation
filtering, explicit rank eligibility, literal exit and the input limit. Physical
key mapping, worker scheduling around these calls, Qwen and IPC remain unfinished.

`RuntimeSentenceInput` now provides an isolated inner sentence-mode event host
over a retained RuntimeSentenceDecoder, fixed settings, session and joined worker.
It maps letter, top-row/numpad digit and enabled semicolon/quote rank key-downs,
queues owned decode requests after edits, and pumps only applicable completions.
Candidate-dependent controls synchronously obtain the current decode when needed;
ordinary letter append uses only exact paths plus completed evidence, never a
synchronous Beam completion. Punctuation completion is a separate API so the outer
host retains symbol/smart-quote ownership. Cancellation invalidates worker and
composition state. Decoder failures remain observable; no synthetic candidate is
committed on failure. The caller must outlive the worker and serialize host calls.
Mapped-model Windows tests type actual inner key sequences and verify pending
Space synchronization, Tab navigation surviving late duplicate completion,
punctuation output, shifted letters, numpad/rank input and cancellation. ARM64 and
x64 seven CTests passed; Linux's existing six portable CTests also passed (the
new mapped-host integration test is Windows-only). The first test build used a
wrong expected second candidate (`c` instead of the fixture's `b`); the corrected
fixture passed without changing runtime behavior. This host is not yet selected
by RuntimeInput, which still rejects sentence schemas; outer modifiers, global
shortcuts, schema transitions, Qwen and production IPC/UI remain unfinished.

`SentenceNeuralRanking` implements the C# candidate-length convex calibration
when the pre-neural winner has 2..6 text elements, and the original additive
policy outside that range. Base-winner selection and final ordering preserve
rank-first versus score-first policy (duplicate singles plus a segmented path).
The result is an index/final-score projection of only the first five candidates;
base score, confidence mass and trailing candidates are not rewritten. The
session creates an owned neural request and verifies generation, raw, lexicon,
required prefix and exact candidate identities before applying its order once.
Manual selection freezes acceptance; edits re-enable requests. The accepted top
constrains probabilistic-prefix decisions for the same raw, with the existing
supplemental-winner override. Clearing composition/lexicon/empty-code commit
clears accepted neural identity. Injected-score tests cover mixed lengths,
outside-range additive weights, candidate mismatch, stale/duplicate application,
manual freeze and base-score preservation. Nonfinite scores are rejected. This
is not yet wired to a scorer or Sentence sidecar; kernel differential validation
is recorded below, while real Qwen lifecycle/transport remains outstanding.

`tools/test_core_native_neural.py` compares the native ranking kernel with actual
C# GetSentenceNeuralBaseTopLength, CombineSentenceNeuralCandidateScore and candidate
comparers. With seed 202609099, ARM64 and x64 each passed 10,000 candidate sets /
45,371 candidates, exact order and zero maximum score difference. Cases include
empty sets, 0..9 text elements drawn from ASCII/CJK, combining sequences,
supplementary characters and ZWJ emoji, rank/score ties, segmented versus whole
paths, duplicate-single setting off/on and 0..9 candidates (the tail after five
stays unchanged). This tests finite injected scores, not Qwen inference accuracy,
pipe behavior or the complete engine's asynchronous neural lifecycle. C# test
assembly built with zero warnings/errors. Invoke the script with `--native`,
`--dotnet` and `--assembly` as for the other text-probe differential scripts.

RuntimeSentenceInput accepts an optional injected neural scorer and runs it on a
separate latest-only worker. An applied Beam generation submits one owned
top-five request; synchronous control-key completion uses the same route and
does not enqueue a duplicate. Edits/cancel/completion invalidate neural work;
the session remains the final identity/manual-selection guard. Scorer exceptions
are exposed through LastNeuralError while the existing n-gram order stays usable.
The decoder's duplicate-single setting supplies the ranking policy. Both workers
join before the caller may release the decoder; an injected scorer must finish
or enforce its own bounded I/O, since thread destruction does not forcibly abort
it. Semaphore-controlled mapped-model host tests cover actual reordering,
manual navigation during scoring, and exception fallback followed by a successful
Space commit. This is scheduling integration with injected scores, not yet the
Sentence pipe client, model-process ownership, settings residency or Qwen inference.

`SentenceProtocol` adds the transport-independent UTF-8 JSON-lines rerank codec.
It emits type/sequence/generation/raw/candidates with exactly one terminating LF,
and validates response type, success, integer identities, exact cased raw and
finite numeric scores of the requested count. Requests require 1..5 candidates
and signed-64-bit-compatible identities. Invalid raw UTF-16 that cannot round-trip
without replacement is rejected; candidate strings use the existing UTF-8 encoder.
Responses above 64 KiB, malformed JSON and malformed score arrays are rejected.
Protocol tests exercise escaped newline framing, Unicode request strings, valid
scores, stale sequence/generation/casing, wrong score types/counts, numeric
overflow, concatenated responses and size limits. This codec neither opens a
pipe nor launches/stops a process. Windows overlapped I/O, cancellation/deadline
drain, isolated pipe fault tests and owned Sentence lifecycle remain unfinished.

`ScoreSentencePipe` implements the Windows rerank exchange over an explicitly
provided local pipe short name (no default production endpoint). Connection has
a 10-second default budget; after connection all writes/reads share a 5-second
response budget. Overlapped I/O waits on completion or stop-token cancellation;
timeout/cancel invokes CancelIoEx and drains completion before stack OVERLAPPED,
event or buffers are destroyed. Drain may exceed the deadline. Requests/responses
are bounded to 64 KiB, UTF-8 line fragments are assembled through LF, and the
protocol validator rejects stale or malformed replies. Handles/cancellation
callbacks have scoped ownership; client SQOS limits server impersonation.
Windows tests use only process-specific `TigerClaw.Core.Native.Test.*` pipes and
cover fragmented success, stalled-response timeout, cancellation, invalid JSON
response fields and absent-pipe connection timeout. ARM64/x64 nine CTests passed.
The scorer API can be injected into the neural worker, but automatic request
sequence ownership, stop-token propagation from that worker, hello/preload,
owned-process shutdown/PID checks and model residency are not connected yet.
No test contacts the production Sentence pipe or changes the daily runtime.

The generic worker now supports a `(const Input&, stop_token)` provider in
addition to its existing non-cancellable provider adapter. Each submitted request
owns a cancellation generation. Replacement, Cancel and destruction request stop
outside the worker mutex, then preserve latest-only result publication and joined
cleanup. Callbacks must remain quick and must not destroy the worker recursively;
legacy providers still drain normally if they ignore cancellation. RuntimeSentenceInput
accepts the cancellable neural scorer signature and forwards the token, while
existing scorer lambdas remain compatible. A portable regression reenters
TakeCompleted from a stop callback and confirms replacement returns only the new
result. Windows pipe tests wait until the isolated server has read the request,
then verify worker Cancel and worker destruction interrupt/drain a pending read.
Linux seven and Windows ARM64/x64 nine CTests passed. Model-process residency,
sequence-owning client integration and authenticated owned-process shutdown are
still unfinished; no production endpoint was contacted.

`MakeSentencePipeScorer` now returns the cancellable callback accepted by
RuntimeSentenceInput. It owns an explicit endpoint and shared atomic sequence;
copied callbacks share monotonically increasing identities, and failed/canceled
attempts never reuse a sequence. It rejects invalid endpoint/timeouts eagerly,
does not connect at construction, and throws rather than wrap the signed-64-bit
protocol counter. Isolated tests fail the first connection, then accept sequence
2 through a copied callback, and run a successful callback exchange through the
cancellable neural worker. ARM64/x64 nine CTests passed. The factory still does
not launch/preload a model, authenticate an owned server or implement shutdown;
these lifecycle operations remain required before production host integration.

Hello and shutdown now use the shared JSON-lines transport via ControlSentencePipe.
Control exchanges share one total timeout across connect/write/read (unlike the
rerank response budget reset). Shutdown requires a nonzero owned PID and verifies
GetNamedPipeServerProcessId on the connected handle before sending any bytes.
The future launcher must supply that PID from its retained process handle; this
API alone does not establish process ownership or protect against a caller using
a stale PID. Tests use fake servers in the test process for hello/matching-PID
shutdown and verify wrong-PID/no-PID rejection without sending a request. No real
process is terminated. A test-server cleanup race was found on x64: disconnecting
before a late synchronous Connect could strand that thread. Cleanup now repeatedly
cancels synchronous I/O until the server thread exits, then disconnects/closes.
Linux seven and Windows ARM64/x64 nine CTests passed after the correction. Actual
owned process launch/handles, graceful-to-forced release and setting/schema
residency coordination remain unfinished.

`OwnedSentenceProcess` now retains the CreateProcess handle for one explicitly
configured executable/model/local pipe. Start is idempotent while that process
is running; a dead handle is closed before restart. Arguments are individually
quoted and include parent PID; no shell, inherited handles, process-name kill or
attachment to existing processes is used. Preload starts then verifies a hello
against the retained process PID. Stop attempts PID-checked shutdown, waits, then
terminates only its retained handle and waits for termination before closing it.
The owner requires serialized calls and must outlive associated scorers. Its
destructor performs best-effort release; explicit Stop reports termination errors.
Dedicated Windows tests launch only this test executable as a non-scoring child
and cover start idempotence, independent-owner isolation, forced release/restart
and pre-canceled preload. ARM64/x64 ten CTests passed. These tests do not prove
successful real-model preload or graceful sidecar shutdown; settings/schema
residency, scoring/process coordination and production host integration remain
unfinished. Daily-runtime executables/models were neither launched nor replaced.

The dedicated owned-process test child now also implements minimal hello and
shutdown responses on its isolated `.graceful` pipe. Two successful Preload calls
retain the same child PID; Stop is checked through an independent process handle
for exit code 0 (forced termination uses 1), proving the graceful path actually
ran. A pipe name containing spaces exercises argument quoting. The child has a
parent-death/30-second watchdog and never loads a model. ARM64/x64 all ten CTests
passed, with twenty consecutive owned-process test passes on each architecture.
This adds successful fake-host handshake and graceful-release evidence, not
real-Qwen preload or settings-driven residency acceptance.

`SentenceServiceLifecycle` adds a provider-based serialized load/score/release
worker. Enable transitions cancel active operations outside the mutex, invalidate
pending/completed scores and schedule residency work. An off/on transition retains
the release barrier before loading again; failed release retries every 250 ms
without allowing a new load. There is no idle unload timer. Requests coalesce,
and completed scores are accepted only for the active lifecycle epoch and latest
request serial. The caller still applies composition identity validation when
consuming a completion. Destruction cancels work and drains owned cleanup; providers
must honor cancellation and release must eventually succeed (permanent cleanup
failure can block destruction). Provider-level tests cover disabled requests,
idempotent enable, cancellation while scoring, stale-result suppression, rapid
off/on ordering, consumable results, destructor release and cleanup retry. Linux
eight and Windows ARM64/x64 eleven CTests passed; the Linux lifecycle test passed
twenty consecutive runs. This layer is not yet wired to OwnedSentenceProcess,
schema/config eligibility or RuntimeSentenceInput; actual process/scorer ownership
and host integration remain the next required work, not established by fake
provider tests.

`OwnedSentenceService` now binds that lifecycle to an actual owned process and
its scoring pipe. Member destruction drains the lifecycle before destroying the
process owner; all launch/hello/score/shutdown operations are serialized on the
lifecycle worker. Owned scoring shares the owner's sequence counter and validates
the pipe server PID before transmitting candidate text. Standalone pipe scorers
retain their existing optional no-PID mode. SetEligible combines supplied sentence
and neural eligibility flags; configuration/schema extraction and input-host
attachment are not implemented by this wrapper. Dedicated test children return
synthetic scores to validate disabled requests, preload followed by scoring,
rapid off/on followed by successful scoring, and release. ARM64/x64 eleven CTests
passed, with twenty consecutive owned-service test runs on each. No real Qwen
model or production endpoint was used, so real-model acceptance and full host
integration remain open.

Sentence eligibility is now shared by the ordinary-host guard and owned service
configuration refresh: ordinal schema-name containment of U+6574 U+53E5 plus
automatic-sentence enable (default true), with neural enable (default true)
additionally required for service residency. Disabling neural scoring does not
disable sentence input. Existing boolean parsing and last-value semantics apply;
missing/invalid/empty values use the C# defaults. Unit tests cover these cases,
and the isolated owned-service test now drives preload/scoring/release through
actual config values and ordinary/sentence schema transitions instead of manually
supplied booleans. Linux eight and Windows ARM64/x64 eleven CTests passed. This
does not yet remove RuntimeInput's explicit sentence-integration guard: the
main input host still needs to route sentence events and refresh the service
from its runtime snapshots, then validate full input/selection/migration traces.

The inner RuntimeSentenceInput host now accepts an exclusive borrowed
SentenceServiceLifecycle, avoiding a redundant neural worker when using an
OwnedSentenceService. Beam completion submits through that lifecycle and Pump
applies its results with the existing composition/candidate identity and manual
navigation checks. CancelRequests cancels active scoring and clears pending/
completed scores without canceling preload or changing process residency; edits,
composition completion and input-host destruction use this path. The service
must outlive its input host and cannot be shared by concurrent input hosts.
Synthetic runtime tests prove score-driven reordering, navigation freezing and
canceling a blocked score without releasing/reloading the service. Linux eight
and ARM64/x64 eleven CTests passed; runtime tests passed twenty consecutive runs
on each Windows architecture. Outer ordinary/sentence event routing, live config
refresh during composition and end-to-end real-model acceptance remain open.

Sentence raw migration now has explicit session ReplaceRaw and input-host
ImportRaw/ExportUncommittedRaw APIs. Import copies the raw string before clearing
old state (including when passed an alias of the current suffix), advances one
composition generation, sets the target lexicon version and clears committed
context, selection, early-commit evidence and pending neural identity. It does
not replay physical keys, lowercase raw text or run intermediate auto commits.
The input host schedules one decode and cancels old neural work; export returns
only uncommitted raw. Tests cover a session after actual empty-code early commit,
rank-selection reset, old-result rejection, mixed-case/selectors and immediate
literal Enter after importing a newer composition. Linux eight and ARM64/x64
eleven CTests passed. The outer mode-switch router must still call these APIs;
these tests do not establish complete ordinary/sentence switching integration.

The non-sentence side now exposes raw import through ChineseComposition,
ChineseInputSession and RuntimeInput. Short input uses ordinary composition when
mixed mode is disabled; longer-than-maximum input uses the runtime-bound temporary
mixed decoder. Import prepares a separate composition before publication and
supports self-aliased raw input, preserving casing without replaying physical
keys. Ordinary schema refresh reuses the same implementation. Tests cover short
versus long imports, mixed active suffix, literal Enter, empty import, aliasing,
and preserving old composition when the required mixed decoder is absent. Runtime
tests also import uppercase lookup code and verify case-insensitive candidates
with case-preserving literal output. Linux eight and ARM64/x64 eleven CTests
passed. Both migration endpoints now exist, but the outer router still needs to
connect them and preserve modifier/shortcut/pinyin/postprocessing order.

An opt-in real-model executable is now available as the Windows CMake target
`core_native_real_sentence_smoke`; it is deliberately not registered with CTest.
Run it with two absolute arguments: the sidecar executable and the GGUF model.
It creates only `TigerClaw.Core.Native.Test.RealSentence.<pid>`, owns the launched
process, performs hello and two five-candidate scores with different generations,
checks finite/repeat-stable scores and PID reuse, and verifies graceful exit code
0 using a separately retained process handle. ARM64 and x64 runs both passed
against their `next/_run/SentenceCppArm64` / `SentenceCppX64` sidecars and the
canonical downloaded Qwen3-0.6B-Base-Q8_0 GGUF. Total observed smoke durations were
763 / 1113 ms respectively; these include launch, two requests and shutdown and
are not per-key latency benchmarks. Within each architecture repeated scores
matched at 1e-5 tolerance; cross-architecture scores were not identical (first
candidate -49.869 versus -50.1147), so this is not numerical parity evidence.
The tested sidecar SHA256 values are ARM64
`ef8139d55cb2a4c1ffdb3d0dcf0454917694d1539c4b296ff3e1283443e57224`
and x64 `0d06758e7d45fbd261468c2acc85274971a848e7078e6311c2e25f484e47d61d`.
No daily-runtime process or release file was used or changed. This proves the
native ownership/protocol path works with a real model, not complete Core input,
model-ranking quality, lifecycle/config transitions under real inference or
full C# host parity.

A native input/service integration regression was reproduced: decoding while
neural residency was disabled marked the composition as already scheduled even
though the service rejected the request, preventing scoring after enable without
another edit. Request now reports the accepted lifecycle epoch; input scheduling
tracks both composition generation and epoch and checks eligibility from Pump.
Rejected requests are not marked scheduled. Rapid off/on retries an unconsumed
score in the new lifecycle without requiring raw edits, while repeated Pump calls
do not submit duplicate scores and existing manual-navigation guards remain.
The regression failed before the fix and passes afterward, including a completed
but unconsumed score discarded during off/on. Linux eight and Windows ARM64/x64
eleven CTests passed. The outer mode router and real config event wiring are
still required; this fixes the inner host's service scheduling contract.

LoadSentenceInputSettings now prepares inner sentence controls from ConfigValues:
shared Enter/Tab clearing, semicolon/quote selectors and page size reuse ordinary
settings parsing; sentence auto commit defaults off, and retained raw count uses
signed Int32 parsing with current-culture signs and clamps to 0..32. The inner
host's Tab-clear default was corrected from false to the C# default true. Tests
cover defaults, overrides, duplicate/overflow/negative retained values, custom
signs and actual no-candidate Tab/Enter behavior with selectors disabled. Linux
eight and ARM64/x64 eleven CTests passed. Hosts can construct RuntimeSentenceInput
with this prepared settings object; live config refresh, decoder-specific settings
loading and complete outer mode routing remain unfinished.

LoadSentenceSettings now prepares decoder configuration for optimal-code frequency
limit, full-code whitelist and duplicate-single grouping. Missing frequency limit
defaults to 1500; present empty/negative/invalid/overflow values become 0, with
invariant signed Int32 parsing matching C#. The default whitelist is reused from
the generated ConfigDefaults; an explicitly empty value removes it. Whitelist
splitting uses complete .NET-compatible text elements and skips whitespace-only
elements, preserving supplementary and combining characters. Tests cover parsing
and a mapped synthetic decoder whose alternative segmented single-character path
exists only when the config enables duplicate-single grouping. Linux eight and
ARM64/x64 eleven CTests passed. RuntimeSentenceSettings remains explicitly
constructible for experiments; production-style callers must use the config
loader. Outer runtime refresh/routing and full C# behavior parity are still open.

RuntimeSentenceState now owns an immutable runtime snapshot, configured mapped
decoder and its input host, in destruction-safe order. Refresh prepares a new
decoder/input with both config loaders, imports only the old uncommitted raw,
then drains the previous host before attaching the exclusive lifecycle service.
Service eligibility is refreshed from the accepted snapshot. Identical snapshot
pointers do not rebuild; invalid snapshots are rejected without replacing the
old input. Tests replace the table while preserving uppercase raw, verify the
new candidate and cleared manual selection, reject an invalid replacement and
disable service residency via config. ARM64/x64 eleven CTests passed and runtime
tests passed twenty consecutive runs each. This initial owner rebuilds on every
distinct snapshot, including unrelated config changes; targeted reuse remains
an optimization. It accepts sentence snapshots only: the outer ordinary/sentence
router still needs to own transitions, physical modifiers, pinyin and output
postprocessing. Failure before publication leaves the old host intact, but file
failure under a live mapping has not been fault-injected in this test.

RuntimeSentenceState refresh now compares effective sentence controls and decoder
config when schema pointer/root/name are unchanged. Unrelated config snapshots
and neural-only toggles retain the same input/decoder, generation, selection and
committed context; service residency updates separately. Relevant config or table
changes still rebuild. Tests preserve manual selection across theme/neural changes
and preserve an actual empty-code early-commit prefix so Enter emits only the raw
suffix afterward. A repeat run exposed a test timing assumption in the earlier
unconsumed-score/off-on regression: a fast scorer could already be consumed by
the same Pump. That test now gates its second score until Pump has returned.
ARM64/x64 full eleven-test suites passed before this test-only stabilization;
the final runtime test passed fifty consecutive runs on each architecture.
Full outer mode/physical-event routing remains unfinished.

RuntimeSentenceState now exposes Leave (returns uncommitted raw and deactivates)
and MigrateToOrdinary (prepares a copied target ChineseInputSession, then disables
service residency, drains the sentence host/decoder and publishes the target).
The target decoder must already resolve its target table. A target import failure
leaves both sessions and service residency unchanged. Tests migrate after an
actual early commit, verify only `bc` transfers without changing repeat history,
reject long-code migration without a mixed decoder while preserving both inputs,
and check service release, repeated Leave and re-entry. A test assertion initially
assumed an empty repeat buffer; production initializes it to its repeat macro, so
the test now correctly compares the before/after value. ARM64/x64 all eleven
CTests passed. These explicit transition APIs still need connection to the full
physical-key/shortcut/mode router; they do not make the experimental Core a daily
runtime replacement.

MigrateFromOrdinary adds the reverse explicit entry transition: prepare the
configured mapped decoder and import complete raw casing before publishing and
clearing the source composition. An already active sentence target is rejected;
pinyin/uppercase source modes return false for the ordinary dispatcher to retain.
Refresh and entry share one publication/destruction sequence. Tests round-trip
uppercase raw `AA` through ordinary/sentence sessions with candidate lookup,
preserve the source and empty target on a missing model failure, and keep an
uppercase special composition out of sentence migration. ARM64/x64 eleven CTests
passed and runtime tests passed twenty consecutive runs each. Physical shortcut
capture and the main host's invocation of these transitions remain unimplemented.

TryProcessEditingKey on RuntimeSentenceState now adapts accepted sentence editing
key-downs into PostProcessResult and delegates common output/history processing
to the existing ChineseInputSession. It must be invoked after outer modifier,
quote, toggle and shortcut handling; it is not a substitute for that ordering.
Unsupported keys return null for the outer dispatcher. Idle Backspace/digits/
Shift-start, modified events, key-up, CapsLock state and special compositions are
not consumed here. Tests type `aa`, commit via Space, check composing/display
fields, repeat text and exactly one added history element, and preserve raw on
Ctrl+digit/key-up. ARM64/x64 full eleven-test suites passed; the final added
history-count assertion also passed in both runtime tests. Punctuation, language
toggle integration and invocation from the main physical router remain open.

The sentence editing adapter now handles punctuation after selector/letter
dispatch declines a key. Suffix resolution shares ordinary smart-quote and
decimal state, uses current snapshot punctuation settings, synchronously ensures
the current sentence candidate, then routes the combined text through common
output processing once. Tests cover Chinese comma, configured English semicolon,
enabled semicolon/quote selectors taking priority, alternating smart quotes,
shifted punctuation and missing-candidate raw fallback. ARM64/x64 eleven CTests
passed. The name TryProcessEditingKey now includes this sentence punctuation
fallback, but still requires the outer event-order stages and does not handle
idle punctuation or complete physical routing.

RuntimeSentenceInput now exposes a literal finish that clears composition and
cancels Beam/neural requests without decoding, honoring only uncommitted raw and
not the Enter-clear setting. The state adapter uses it for bare physical
CapsLock before the capsLock-state pass gate: raw casing is committed through
common output processing, CapsLock itself stays unhandled and the Chinese flag
is retained. Other keys while capsLock is on remain outside this adapter.
ARM64/x64 eleven CTests passed, including mixed-case literal CapsLock output and
no composition mutation from a caps-on letter. Ctrl+Space/Shift language toggles
still need integration with their existing outer state machines.

CtrlSpaceKey/ShiftKey in the ordinary session now accept an optional toggle
callback, preserving the original default behavior and state-machine order.
RuntimeSentenceState supplies the live uncommitted suffix through a shared
SetChineseWithExternalRaw path, then cancels sentence composition without changing
residency. Its CtrlSpace and Shift entry points remain separate so the outer
dispatcher can retain modifier-selection priority between them. Tests cover raw
casing on Ctrl+Space, no repeat/release double-toggle, Shift key-up literal commit,
and keeping the sentence runtime snapshot alive after language changes. Linux
eight and ARM64/x64 eleven CTests passed. These stages still need invocation in
the complete main host event sequence; end-to-end physical routing is not done.

RuntimeSentenceState now exposes FocusChanged and CancelComposition with distinct
semantics matching C#: focus delegates physical chord/action/page/decimal reset
to the common session while retaining sentence raw, generation, selection and
committed context; external cancellation also cancels the inner input workers
and clears composition. Neither operation changes schema-based residency. Tests
retain uppercase raw/manual selection through focus, prevent an orphaned Shift
release from toggling language after that focus reset, and ensure external
cancel stays empty after pending Beam work is drained. ARM64/x64 eleven CTests
passed. Production frontend focus/cancel message routing remains to be connected.

RuntimeSentenceState now provides a modified-key stage after dedicated action
bindings. It mirrors C# Alt/Ctrl/Win branch priority: bare modifiers retain raw,
while other modified keys pass through with cancellation-before-pass and cancel
pending sentence work. Ctrl/Alt digits do not reorder sentence candidates. The
stage leaves key-up and special pinyin/uppercase ownership to the outer router,
and does not unload schema-owned resources. Runtime tests cover all three masks,
bare keys, key-up, digit cancellation, drained stale Beam work and idle pass.
This stage still needs wiring into the complete physical event host.

The sentence state's quote stage shares outer physical-pair and deleted-quote
tracking while suppressing key-up fallback during a live sentence composition.
C# only performs that fallback in idle/ordinary/pinyin states; using the outer
session's apparent idle state would otherwise emit a stray quote. An optional
allowFallback argument defaults to true for all existing ordinary callers.
Runtime tests preserve raw/manual selection/generation on an orphan quote
release, reject duplicate output after a normal quote commit and retain actual
idle fallback. Full physical routing remains pending.

RuntimeSentenceState.ProcessKey now connects these stages through the existing
ChineseInputSession router via optional InputDispatchExtensions, rather than
maintaining a second event-order implementation. Quote observation, Ctrl+Space,
modifier selection, Shift, action bindings, modified shortcuts and editing keep
their existing priority. Sentence action responses project live composition;
unrecognized active-sentence keys pass without entering ordinary idle dispatch.
English, pinyin and uppercase paths remain owned by the common session. Runtime
tests now send complete event sequences through this entry: selection/Space,
orphan quotes, add-word repeat suppression, recent-schema callback priority and
response state, Ctrl+digit cancellation, Ctrl+Space literal commit, English,
uppercase and pinyin. Linux eight and ARM64/x64 eleven CTests passed after full
rebuilds. The test recent-schema callback does not perform real migration;
RuntimeInput snapshot synchronization still rejects sentence schemas, and main
IPC/physical frontend hosting remains unimplemented. This event entry is not a
production-ready Core or proof of end-to-end schema migration.

RuntimeInput now optionally owns a RuntimeSentenceState when given an explicit
n-gram model path (and optional borrowed lifecycle service). It routes serialized
events through the shared sentence-aware entry, synchronizes sentence eligibility
with real runtime snapshots, migrates uncommitted raw in both directions, and
keeps pinyin/uppercase compositions in the common session. Page pumps asynchronous
results and returns the configured visible prefix; Raw and SelectedCandidateIndex
expose the active backend instead of the outer session's empty ordinary buffer.
Focus/cancel/import also delegate to the appropriate owner. Tests use actual
table directories and recent-schema callbacks to round-trip uppercase raw,
clear old selection, commit with the target table, preserve focus and cancel
cleanly. A missing model leaves the old ordinary session intact; the caller can
restore the runtime schema and continue. Runtime schema publication itself is
not rolled back by this host. Without an explicit model path, sentence schemas
still fail explicitly. IPC, UI projection/notification, real frontend hosting
and production acceptance remain incomplete.
