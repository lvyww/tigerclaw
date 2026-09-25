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
| `../next/TigerClaw.Overlay.Native/README.md` | 主线 C++ Overlay、构建、WPF 回退与验收记录 |
| `../next/TigerClaw.Core.Native/README.md` | 平行 C++ Core（已暂停）：顶部为 2026-09-09 进度快照、恢复入口及剩余工作 |
| `../rime/tiger_sentence/README.md` | 独立 Rime 方案部署与开发 |
| `FCITX5_ANDROID_PORTING_PLAN.md` | Fcitx5 Android 当前完成度和剩余验收 |
| `FCITX5_ANDROID_BASELINE.md` | Android 移植冻结的仓库/模型身份 |
| `sentence_golden_v1.md` | 跨实现 sentence JSONL 黄金格式 |
| `COMPACT_LEXICON.md` | C# 主码表/拼音反查紧凑二进制布局、生命周期与内存基准 |
| `COMPACT_RANKING_PRIORS.md` | 整句主码、四码生僻字保护与紧凑 Top-5 词先验 |
| `SENTENCE_EARLY_COMMIT_EVAL.md` | 整句提前上屏准确度与积极性端到端评测 |
| `../tools/README_sentence_neural.md` | n-gram 训练和离线模型实验 |
| `../tools/FullPinyinEval/README.md` | 独立全拼切分、Top-10 Qwen 融合与离线评测 |

`用户使用说明书.pdf` 是 Markdown 手册的发布版附件；内容变更时应同步重新导出。

## 维护规则

- 实现细节由源码和自动测试兜底，文档只保留跨组件契约和不容易从代码发现的约束。
- 已完成的阶段计划改写成“现状 + 剩余验收”，不长期保留逐阶段任务流水账。
- 历史资料只有在仍能帮助复现问题时才移入 `docs/archive/`，否则直接删除。
- `reference/`、构建目录和发布目录不是当前文档来源。

- [虎爪全拼内测版：实现、构建与验收](FULL_PINYIN.md)
- [全拼性能优化：实现、测量、回归与部署](PINYIN_PERFORMANCE_20260922.md)
- [虎整句 Q8 五阶：主线格式、发布与验证](SHAPE_FIVEGRAM.md)
- [Articles 独立字符五阶：训练、产物与后续插值参照](../tools/FullPinyinEval/HigherOrder/ARTICLES_CHAR5.md)
- [corpus4 wsmerge：冻结形码准确率对照](../tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_ACCURACY.md)
- [corpus4 wsmerge：493 MB 压缩与准确率](../tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_500MB.md)
- [corpus4 wsmerge：实际频次剪枝 489 MB 与准确率](../tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_COUNT500.md)
- [主线上下文裁剪前五阶：三套冻结准确率](../tools/FullPinyinEval/HigherOrder/MAINLINE_FULL5_ACCURACY.md)
- [主线实际频次剪枝：346 MB TCS Q8 对照](../tools/FullPinyinEval/HigherOrder/MAINLINE_COUNT_PRUNING.md)
- [主线按各阶记录数对标剪枝：430 MB TCS Q8 对照](../tools/FullPinyinEval/HigherOrder/MAINLINE_COUNT_MATCHED.md)
- [Corpus4 按主线各阶记录数对标剪枝：414 MB TCS Q8 对照](../tools/FullPinyinEval/HigherOrder/CORPUS4_WSMERGE_COUNT_MATCHED.md)
- [Articles 与 Corpus4：同环境准确率和互补分析](../tools/FullPinyinEval/HigherOrder/ARTICLES_CORPUS4_COMPLEMENT.md)
- [Corpus4 与 Articles：先融合后按主线记录预算剪枝](../tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_MERGE_BUDGET.md)
- [Brightmart 排除新闻：独立五阶训练与评测](../tools/FullPinyinEval/HigherOrder/BRIGHTMART_NONNEWS.md)
- [Corpus4 / Articles / 非新闻三路融合与剪枝](../tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_THREEWAY.md)
- [Brightmart 仅新闻：独立五阶训练与评测](../tools/FullPinyinEval/HigherOrder/BRIGHTMART_NEWS.md)
- [Corpus4 剪枝版与 Articles：固定低权重概率插值及损失补偿](../tools/FullPinyinEval/HigherOrder/CORPUS4_PRUNED_ARTICLES_MIXTURE.md)
- [Corpus4 与 Articles：固定低权重概率插值实验](../tools/FullPinyinEval/HigherOrder/CORPUS4_ARTICLES_MIXTURE.md)
