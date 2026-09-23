# 虎整句主线：TCSKNM03 Q8 五阶

2026-09-24，按用户要求，虎爪与 Rime 采用同一份 Q8 模型。虎爪已改为 C# 直接
映射 TCSKNM03，删除形码 KLM 和旧三阶读取路径。此项不涉及全拼模型。

## 默认文件

- 源：`C:\Archive\tigerclaw_sentence_ml\runtime\sentence-fivegram-mobile.bin`
- 运行文件：`Models/sentence-fivegram-mobile.bin`
- 大小：356,492,204 字节（356.49 MB）
- SHA256：`5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361`
- TCSKNM03 version 2，概率/回退各 8 位，字 ID 16 位。保留原主线全部词表和
  1–5 阶记录，从原 Q16 参数再次量化，无重新训练或剪枝。
- 同格式 version 1/Q16 仍可读取；构建默认严格校验上述 Q8 身份。

`SentenceFivegramModel.LoadAvailable` 只搜索 `Models/` 和运行根目录的上述文件名。
五阶独立提供观察二元组孤立字先验，和 Rime 当前实现一致，不再依赖旧三阶。
不存在 KLM、TCSKNM01/02 回退。模型缺失/损坏时返回无模型，保留已有普通词表
降级行为。旧三阶 fixture 读取器移至 `Core.Tests/LegacyFixtures`，仅历史测试使用，
不编入 Core。全拼模块自己的 KenLM 支持未改变。

## 读取与生命周期

C# 使用只读内存映射及有边界检查的直接读取。启动检查 header、词表、各阶总数、
bucket/index 范围与键顺序，查询时检查 block 和后继范围。缓存只保存有界的上下文
偏移和评分，模型页由 OS 管理，不复制整份模型到托管堆。

Beam 携带最近四个字 ID 与有效长度，BOS/EOS 和 OOV 语义与 Lua 一致。观察记录
的存在性独立于量化码零。空上下文的回退权重必须保留。会话持有映射租约，owner
退出后在途查询仍可读取，最后一个租约释放后关闭映射。锁定、增量、回删、取消、
历史裁剪维持既有状态规则。Beam 宽度、字奖励、词表、Qwen、学习、提前上屏未改。

## 构建与打包

`next/stage_sentence_fivegram.ps1` 校验模型长度/SHA256 后复制 Q8，不再构建或复制
形码 KenLM DLL。`TIGERCLAW_SHAPE_FIVEGRAM_MODEL` 可以指定相同内容的源位置。
Debug、x64/ARM64 完整发布均使用该步骤。`pack_release.bat` 要求 Q8 模型存在，
过滤遗留 KLM、旧三阶模型和 shape KenLM 依赖；不需要旧文件即可打包。
Core-only 路径仍只更新 Core；跨此格式升级必须同时带上 Q8 模型。

隔离 Native AOT 产物在 `next/_run/ShapeQ8/core-arm64` 和 `core-x64`。
探针在生产 IPC/单例启动之前分流，保留启动上下文保护：

```batch
TigerClaw.Core.exe --shape-fivegram-probe 词表.txt 完整编码 probe.tsv
```

## 验证

- 全量 Core.Tests 通过，包括 938,714 项原解码器检查、7,388 次快照比较。
- Q8 专项 56,278 项检查、2,376 次快照比较通过：四字历史、增量/回删、锁前缀、
  取消、历史裁剪、并发、owner/session 释放、缺失/损坏文件、旧路径不再加载。
- 1,341 条独立 Python/Lua 参考查询与 C# 评分逐项相同，最大差值 0。
- ARM64/x64 Native AOT 构建及实际探针通过；目录只有 Q8，没有 KLM、三阶或
  KenLM DLL；两架构候选/分数一致。
- 普通/no-Qwen 打包测试、缺失 Q8 拒绝、遗留模型排除、CRC 检查通过。
- 两万句 C# 主线复测及与 Rime 的逐句对比见 `next/_run/ShapeQ8/summary.json`。
  旧集 9959/10000，新集 9971/10000，合计 **19930/20000（99.650%）**，
  全部首选与当前 Rime Q8 逐句一致。Top5/Top20 均 19968/20000。
  C# 保持自身默认 Beam 2000，未为匹配 Lua 改动生产 Beam。

复现：

```text
TigerClaw.Core.Tests --shape-fivegram-tests [Q8文件]
TigerClaw.Core.Tests --shape-fivegram-scores Q8文件 queries.tsv scores.txt
TigerClaw.Core.Tests --shape-fivegram-eval Q8文件 冻结资源目录 cases.tsv 输出.tsv
```

Q8 量化实验：[TcsQ8/README.md](../tools/FullPinyinEval/HigherOrder/TcsQ8/README.md)。
此前 Q8 对 Q16 的 Rime 三组冻结测试仅 7 个首选变化，救回 5、退步 1、两版都错 1；
当前 Rime 两万句首选不变，均 19,930 正确。训练/测试来源存在重合，不宣称独立
泛化提升；旧 KLM 的 C# 19,926 结果属于旧模型与旧先验组合，不能只归因于位数。

## 部署范围

本轮更改主线源代码、默认模型、构建与打包产物，未覆盖日用 `release_arm64`。
2026-09-22 已安装的旧 Core/模型和个人配置、学习数据保留。需要同步替换 Core
与 Q8 才会在日用安装生效。离线与探针通过不代表 TSF 实际输入、提前上屏校准
或端到端延迟已经验收。

本轮产物和完整校验归档在 `C:\Archive\q8-mainline-20260924`：Rime 完整包、
虎爪 ARM64/x64 Core＋模型更新包、逐句结果、测试日志、源文件/模型/包哈希。
虎爪更新包需要已有对应架构安装，并非独立完整安装包。公开仓库未 push，Release
附件未上传。
