# 虎整句主线：压缩五阶直接搜索

2026-09-22，419.93 MB 纯汉字五阶已接入维护中的 C# Core。形码直接搜索使用
五阶，每次扩展及 EOS 都参与评分。不是全拼模型，也不是只对三阶结果做重排。
现有 Qwen 设置仍可在最终候选上追加重排；本次验证关闭 Qwen、学习和提前上屏。

## 默认加载与生命周期

`InputMethodEngine.ReloadSentenceResources` 使用 `SentenceFivegramModel.LoadAvailable`。
先按既有优先级加载三阶模型，然后尝试 `Models/sentence-fivegram.klm`，再尝试
运行根目录的同名文件。三阶继续提供原有的观察二元组孤立字先验及兼容回退。
缺失五阶、无 DLL、旧 DLL 缺少 ABI、模型加载错误时回退三阶；完全没有有效
三阶时沿用原来的无整句模型行为。Core-only 升级仍不替换模型和 DLL。

模型身份：

- 原始文件：`C:\Archive\tigerclaw_sentence_ml\experiments\brightmart-char5-500mb-20260922\char5-context128-q8.klm`
- 运行文件名：`Models/sentence-fivegram.klm`
- 长度：419,929,926 字节（419.93 MB / 400.48 MiB）
- SHA256：`580ed90ced0ac72e453e0d647635cec2d3879e2e47ae1a231b96f49b2d34eafa`
- 依赖：对应架构的 `jointkenlm.dll`，保留原三阶文件。因此 420 MB 不是总模型或内存占用。

Beam 路径保存最近四个模型词 ID 和有效长度，BOS 进入历史且只保留一次。
KenLM `joint_score_history4` 在完整历史上评分；其结果已与独立完整状态整句
评分交叉核对。锁定前缀逐字重建同一历史，回删、增量复用和已上屏历史裁剪
均保留相同状态。路径仍以全文去重，不按最后两个字合并。

查询会话拥有独立且有界的 ID/评分缓存，持有 native SafeHandle 引用与三阶
查询租约。方案切换先停用旧解码器；已在执行的旧查询可以完成或取消，最后
一个租约释放后才关闭模型。既有 latest-generation、学习、选重、排名先验和
提前上屏阈值未改。五阶不支持旧实验用的 `scoreSentenceBoundaries=false`，会
显式拒绝，避免悄悄用三阶式无 unigram 边界语义替代 KenLM 的 BOS/EOS。

## 构建与发布

`next/build_next.bat`、`publish.bat`（含 no-Qwen）、`publish_arm64.bat` 默认调用
`next/stage_sentence_fivegram.ps1`：校验指定模型长度/SHA256，构建对应架构
KenLM DLL，复制五阶和 KenLM 许可证。可通过 `TIGERCLAW_SHAPE_FIVEGRAM_MODEL`
指定同一模型的其他源位置；内容必须匹配已验证哈希。

`pack_release.bat` 同时要求并包含五阶、DLL、许可证和原三阶。缺少 DLL 或模型
会报错，不产生看似支持五阶、实际静默回退的发布包。`publish_core_arm64.bat`
仍是 Core-only 路径，不升级依赖、不替换原模型；首次启用五阶需要完整发布。

本次构建输出仅在 `next/_run/ShapeFivegram/core-arm64` 和 `core-x64`。
首次集成验证时未部署。随后用户明确要求改用现有 `release_arm64` 安装：
2026-09-22 16:51 已完成全拼内测版解除注册和主线切换，详见下文。

独立 AOT 探针在注册、单例、IPC 启动之前分流，并保留启动上下文安全检查：

```batch
TigerClaw.Core.exe --shape-fivegram-probe <text-tab-code词表> <完整编码> <输出文件>
```

探针从自身目录加载模型，明确要求已选中五阶，无生产 IPC、UI、学习写入。

## 主线验证

- C# Core.Tests 全量通过，包括学习、提前上屏、异步/缓存、UI 和紧凑词表测试。
- 五阶专项：56,395 项检查、2,376 次快照比较，包含四字历史改变排名、
  真实模型完整状态评分、OOV、增量/回删、锁定、取消、历史裁剪、并发会话及释放。
- 原解码器专项：938,714 项检查、7,388 次快照比较通过。
- ARM64 和 x64 Native AOT 构建成功；真实探针候选与分数逐项一致。
  `nmbytqtwycjuqpumdgrlldhq` 首选均为“马云雷军入选大亨名单”。
- ARM64 AOT 在 DLL 缺失、旧 ABI 两种情况下均安全回退，未启动生产 IPC。
- 普通/no-Qwen 包内容、缺少 DLL 拒绝、旧 mobile 文件排除和压缩包 CRC 检查通过。
- 全量测试中的一条旧学习断言已对齐现行 `9 + 2*ln(weight/1000)` 权重：
  第二次纠正可越过 0.99，但仍低于强证据范围；没有改学习算法。

两万句使用原冻结码表、补充词、1500 高频字限制、白名单和主线排名先验。
C# 保持自身默认 Beam 2000，没有为了与 Lua 对齐而修改生产搜索宽度。
整批关闭 LLM、学习和提前上屏，不做延迟比较。

| 模型 | 旧集正确 / 10000 | 新集正确 / 10000 | 合计 / 20000 |
|---|---:|---:|---:|
| C# 原 m5 三阶 | 9941（99.41%） | 9952（99.52%） | 19893（99.465%） |
| C# 压缩五阶 | 9958（99.58%） | 9968（99.68%） | 19926（99.630%） |

五阶对三阶：旧集救回24、退步7；新集救回28、退步12；合计救回52、退步19，
净增33。两种模型的20000条首选输出，分别与上一轮冻结 Lua 实验完全一致。
这是本组数据上的一致性证据，不代表所有 Beam 候选或实际按键体验均已验证。

4814条目标与五阶训练文本整行重合，不能当独立留出集。实际 TSF 应用输入、
五阶下提前上屏正确率/置信度和延迟仍需单独验收，不能由离线命中率推断。

## 复现与证据

- `TigerClaw.Core.Tests --shape-fivegram-tests [五阶路径] [三阶路径]`
- `TigerClaw.Core.Tests --shape-fivegram-eval 五阶路径 三阶路径 冻结资源目录 cases.tsv 输出.tsv`
- `next/_run/ShapeFivegram/summary.json`：分集汇总和逐句与 Lua 的对照。
- 同目录 `mainline-20k.tsv`、`mainline-changes.csv`：全部输出及所有进退步。
- 同目录 `validation-manifest.json`、测试/构建日志、`probe-{arm64,x64}.tsv`。
- 压缩方法、训练来源及原始评测：[BUDGET5_SHAPE_20K.md](../tools/FullPinyinEval/HigherOrder/BUDGET5_SHAPE_20K.md)。

## 本机部署（2026-09-22 16:51）

按用户指定，安装目录为 `C:\Users\yc\Desktop\bime_codex_src_20260513\release_arm64`。
全拼内测版已解除注册，原目录和学习数据保留。HKCU/HKLM CorePath、启动项、
运行 Core/Overlay 路径和当前“虎整句”方案均已验证；实际进程映射确认加载本节
五阶模型。现有 Program Files 下四种 TSF DLL 与新构建哈希匹配。主线原配置项
保留，包括神经重排与提前上屏开启，因此当前日用配置不等于本文关闭两者的
离线评测配置。Core 自动补入缺少的14项拼音默认配置。安装/加载已验证，
实际应用输入仍是独立验收项。
证据：`next/_run/MainlineInstall/INSTALLATION.md` 与 `installed-runtime.json`。
