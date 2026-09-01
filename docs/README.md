# TigerClaw 文档索引

项目当前状态和开发入口以根目录 `AGENTS.md` 为准。不要再新增并列的总览或阶段
交接文档；具体信息放到下列已有文档中。

## 当前文档

| 文档 | 用途 |
|---|---|
| `../AGENTS.md` | 当前架构、关键不变量、构建方式和 agent 交接 |
| `../Protocol/messages.md` | Core 主命名管道协议 |
| `../Protocol/sentence_messages.md` | Sentence 重排 sidecar 协议 |
| `../用户使用说明书.md` | Windows 版安装、配置和排障 |
| `../整句虎使用说明.md` | Windows/Rime 整句输入操作 |
| `../更新日志.txt` | 面向用户的版本变化 |
| `../BimeTSF2/SampleIME/BRIDGE_ONLY_NOTES.md` | TSF bridge-only 边界 |
| `../rime/tiger_sentence/README.md` | 独立 Rime 方案部署与开发 |
| `FCITX5_ANDROID_PORTING_PLAN.md` | Fcitx5 Android 当前完成度和剩余验收 |
| `FCITX5_ANDROID_BASELINE.md` | Android 移植冻结的仓库/模型身份 |
| `sentence_golden_v1.md` | 跨实现 sentence JSONL 黄金格式 |
| `SENTENCE_EARLY_COMMIT_EVAL.md` | 整句提前上屏准确度与积极性端到端评测 |
| `../tools/README_sentence_neural.md` | n-gram 训练和离线模型实验 |

`用户使用说明书.pdf` 是 Markdown 手册的发布版附件；内容变更时应同步重新导出。

## 维护规则

- 实现细节由源码和自动测试兜底，文档只保留跨组件契约和不容易从代码发现的约束。
- 已完成的阶段计划改写成“现状 + 剩余验收”，不长期保留逐阶段任务流水账。
- 历史资料只有在仍能帮助复现问题时才移入 `docs/archive/`，否则直接删除。
- `reference/`、构建目录和发布目录不是当前文档来源。
