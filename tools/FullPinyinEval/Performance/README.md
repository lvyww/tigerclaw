# 全拼性能与一致性验证

此目录调用维护中的 C# 全拼实现。所有输出必须使用新文件名；不修改词库、模型、
偏好或学习日志。Windows ARM64 输出统一放在 `next/_run/FullPinyin/performance-20260922/`。

## 构建与离线模式

在仓库根目录的 Windows 命令行执行：

```bat
dotnet publish tools\FullPinyinEval\Performance\Performance.csproj -c Release -r win-arm64 --self-contained -o next\_run\FullPinyin\performance-20260922\probe
next\build_pinyin_native.bat ARM64 next\_run\FullPinyin\performance-20260922\probe
```

探针使用与 Core 相同的 Native AOT `IlcOptimizationPreference=Speed`。
`Joint.exe` 参数：

```text
bench   <schema-directory> <new.csv>
profile <schema-directory> <new.csv>
golden  <schema-directory> <new.tsv> <cases.jsonl>
scores  <schema-directory> <new.txt>
sharing <full-pinyin-schema> <new.txt> <xiaohe-schema>
```

- `bench`：固定 8 组输入，每组逐字追加，运行三轮；后两轮共 230 键为热态。
  包含搜索、五阶重排、中文/英文/表情菜单。每键等待完成，不模拟并发连打。
  资源加载包含拼写索引预热；不保证 OS 文件缓存为空。末尾内存是显式 GC 后的
  Private Bytes，另记工作集及峰值；不等同于模型文件大小或长期运行内存。
- `profile`：另开诊断分阶段计时，不能拿其绝对耗时与无诊断基准混用。
  批量评分进入后，标为 `native_ms_sampled` 的计数只覆盖标量查询，不再代表
  全部原生评分时间；批量时间包含在 `score_ms` 中。
- `golden`：8019 条冻结 test 全句，加 115 次追加、107 次退格，共 8241 行。
  SHA256 签名包括 Top50 文本、双精度分数、词频、切分、音节、raw 边界、罚分、
  消耗长度以及最终菜单；不以“首选一样”替代一致性。冻结源为
  `C:\Archive\tigerclaw_sentence_ml\experiments\joint-pinyin-20260921\cases.jsonl`。
- `scores`：固定种子构造上下文，对比批量状态评分和原标量 API，402 万次结果
  要求 double 位模式完全相同。
- `sharing`：两个实际方案必须共享底层词库/模型，验证并发查询、关闭一个方案后
  另一个仍可查询，以及模型拥有者释放后解码查询租约仍有效。

比较与汇总（Python 标准库）：

```bash
python3 tools/FullPinyinEval/Performance/check.py compare baseline.tsv optimized.tsv
python3 tools/FullPinyinEval/Performance/check.py bench final-bench-1.csv final-bench-2.csv
```

性能必须串行测量，不同时进行编译、全量 golden 或其他重型实验。
完整原版源码快照、原版探针和原始数据保存在该次 `_run` 证据目录；未用后来
优化的实现生成“原版”签名。日常回归还需执行 Core 的 `--full-pinyin-tests`、
`--full-pinyin-real-tests`、`--fivegram-feedback-tests` 和 `--learning-tests`。
真实模型/回执测试使用带 `full-pinyin-test-fixture` 标记的隔离目录；回执测试
要求新日志，不能指向用户日常目录。

## Core 调度

ARM64 AOT Core 支持隔离入口：

```text
TigerClaw.Core.exe --full-pinyin-performance <marked-fixture-root> <new.csv>
```

该入口不创建生产 IPC、MMF 或 Overlay。以 100/60/30 ms 间隔输入，记录按键返回、
后台候选发布和确认时间，检查提交与可见首选一致。后台取消数仅统计已执行工作，
合并掉的未执行请求不算一次已执行取消。此结果不是 TSF 或真实候选首帧延迟。

## 明确调用的真实窗口探针

Windows CMake 构建此目录可得到 `pinyin_window_probe.exe`：

```bat
cmake -G "Visual Studio 18 2026" -A ARM64 -S tools\FullPinyinEval\Performance -B next\_run\FullPinyin\performance-20260922\window-build
cmake --build next\_run\FullPinyin\performance-20260922\window-build --config Release
pinyin_window_probe.exe C:\path\new-window-results.jsonl
rem 可选：从零起始的轮次/用例编号继续，示例补最后三个用例
pinyin_window_probe.exe C:\path\resume-results.jsonl 2 5
```

它会显示自己的 RichEdit，进程内激活已安装虎爪，发送真实键，分 100/60/30 ms
三档检查 24 次上屏。每次发送前检查前台、焦点、修饰键和 TSF profile；失焦立即
停止，不向用户其他窗口发送输入。采用当前用户设置，不能解释为全新用户准确率。

探针在构建目录生成一份加入观测点的原生 Overlay 源码副本，生产 Overlay 源码和
二进制不变。副本只读实际 UI 通道，使用独立心跳/菜单端点及不可用的命令管道；
在测试窗口中显示自己的候选窗。仅统计非空菜单 token 对应的完成代，不能将
pending 时留存的旧列表算作新候选。记录 UpdateLayeredWindow 成功、窗口可见并
完成 DwmFlush 的首帧时间，不是物理屏幕光电测量，也不是给日常 Overlay 注入日志。
合并而未显示的中间代单列为 `unpresented`，不能静默从延迟统计中删除。

实现和本机结果见 [PERFORMANCE_20260922.md](../../../docs/PINYIN_PERFORMANCE_20260922.md)。
