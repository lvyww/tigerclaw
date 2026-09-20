# 整句模型训练与离线实验

本目录包含整句语料处理、n-gram 训练、模型转换和离线评测工具。当前 Windows
运行时只使用：

- n-gram：20% 原发布模型与 80% mohu 214 MiB 模型的融合裁剪版（2026-09-19）；Windows 发布包携带 `sentence-ngram-v2.bin`，读取器也支持 `sentence-ngram-mobile.bin`；
- `sentence-qwen-q8.gguf`：Qwen3 0.6B Base Q8，重排前五个 n-gram 候选。

两者均为仓库外原始文件，发布后只读映射，不加密。旧 compact n-gram、字符
Transformer 和 ONNX 流程只保留作历史实验，不进入当前运行时。

## 目录和工具

- `SentenceNgramTrainer/`：Windows .NET 10 外部计数与 V2 模型构建。
- `convert_sentence_ngram_mobile.py`：把 V2 无损重排成移动端 TCSKNM02。
- `convert_sentence_ngram_windows.py`：把 TCSKNM02 无损重排回 Windows TCSKNM01；保留零值观察记录和空回退上下文。
- `export_tiger_sentence_rime.py`：导出 Rime 虎整句码表和排名数据。
- `benchmark_sentence_gram.py`：离线比较 n-gram/搭配实验。
- `SentenceLengthEval/`：原虎整句按 2～6 字分档、真实 ARM64 Q8 scorer 的配对准确率评测；口径与运行方式见其 README。
- `prepare_sentence_benchmark_cases.py`：生成非重叠验证集。
- `test_sentence_qwen.py`：单独检查 Qwen 候选文本概率。
- `evaluate_sentence_decoder.py`、`evaluate_sentence_neural_pools.py`：候选池评测。
- `prepare_sentence_neural_data.py`、`sentence_neural_*`、`train_sentence_neural.py`：
  已停用的字符 Transformer 实验。

生成语料、模型、checkpoint 和评测大文件不得提交 Git。

## 当前模型位置

```text
C:\Archive\tigerclaw_sentence_ml\runtime\sentence-ngram-v2.bin
C:\Archive\tigerclaw_sentence_ml\runtime\sentence-ngram-mobile.bin
C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-gguf\downloaded\Qwen3-0.6B-Base-Q8_0.gguf
```

`publish.bat` 和 `publish_arm64.bat` 从上述仓库外位置复制 v2 格式 n-gram，
成功复制后移除输出目录中旧的 mobile 文件。`pack_release.bat` 要求存在 v2 模型，
并在暂存时排除残留 mobile 文件（保留源文件）；普通包和 no-qwen 包均适用。
这是 2026-09-20 用户为优先考虑下载包体而选择的发布策略，不回退融合模型参数。
`next/build_next.bat` 的调试输出与 Rime 仍使用 mobile；Core-only 发布不改模型。
不要把模型复制回源码目录长期保存。

### Windows 双格式读取

Core 根据文件头识别 TCSKNM01 / TCSKNM02。自动加载按 `Models/` 下 mobile、
运行目录下 mobile、`Models/` 下 v2、运行目录下 v2 的顺序查找。
某个文件加载失败会继续尝试下一个；若两个格式都有效，mobile 优先。
读取器不改变 Beam、学习、Qwen 或提前上屏算法。

两种格式都使用只读内存映射，映射页由操作系统管理。Mobile 不复制整个模型或
Lua 式数据页缓存；每个查询会话分别缓存最多 16384 个二元、16384 个三元上下文
的位置、后继数和回退系数，复用原有固定大小评分缓存。上下文查询先查稀疏索引，
然后跳过后继数据，一次扫描并缓存该索引块的上下文位置，最后二分查找后继。
空上下文仍保留回退系数；零概率后继仍算已观察记录。
格式边界和稀疏索引在加载时校验，访问上下文时继续校验页边界和记录长度。
旧模型释放仍等待所有查询会话结束，不跨解码器共享可变缓存。

验证入口：Core `--sentence-review-tests` 含双格式合成模型测试；
`--ngram-format-compare <v2> <mobile> <codes.txt> <scalar-queries.tsv> <cases.tsv> <output-prefix>`
核对真实模型、逐键候选及提前上屏证据，并记录输入/回删耗时。
查询 TSV 为三个 Unicode 码点整数；句子 TSV 为 `id/dataset/code/text` 四列。
基准输出包含逐键 TSV 与 p50/p95/max JSON，模型读取不等于实机 UI 响应时间。

2026-09-20 验证：完整 Core 回归通过（整句 938714 项检查、7388 个快照），
ARM64/x64 Native AOT 构建通过，普通/no-qwen 包含 mobile 模型的隔离打包测试通过。
融合模型 40004 组查询含/不含 unigram 分数逐位相等，二元观察结果相等；
48 条句子（13–77 编码）两轮输入/回删共 10052 对解码结果，候选、评分和提前
上屏证据一致。基准为 ARM64 Windows 上的 Release 托管测试程序，保留 OS 文件
缓存，无 Qwen/学习/补充语料，采用生产 Beam 与排序参数；不是前端实机验收。

| 长句编码位置 >24，单位 ms | v2 p95 | mobile p95 | v2 最慢 | mobile 最慢 |
|---|---:|---:|---:|---:|
| 第一轮输入（1440 对） | 17.33 | 18.33 | 35.69 | 47.34 |
| 第一轮回删（1400 对） | 9.71 | 9.94 | 30.05 | 32.28 |
| 第二轮输入（1440 对） | 16.93 | 16.81 | 42.53 | 47.75 |
| 第二轮回删（1400 对） | 10.06 | 9.65 | 32.17 | 27.26 |

第二轮复用解码器和查询缓存，第一轮仅查询会话新建，不是冷磁盘启动。
第一轮首键 48 对的 p95 为 1.37/1.62 ms，最慢 2.02/12.79 ms；模型加载单次
为 1.20/4.56 ms。暂不能宣称 mobile 更快或尾延迟全面改善；确定收益是当前模型
文件减少 45.90 MiB（17.65%），并与 Rime 共用文件。结果及逐键明细保存在
`next/_run/mobile-ngram-20260919/comparison-prefetch.{json,tsv}`。

2026-09-19 按用户选择，将上述两个 n-gram 源更新为单文件融合 214 MiB 版。
移动格式为 224,475,584 字节（214.08 MiB），SHA256
`23216acd8319885aa2431ffbf2231dab4677c5d4abb55a08a404450a15b865ca`；
Windows 格式为 272,600,424 字节（259.97 MiB），SHA256
`de4e925d01cdadf2a7e0a6b7dfff4e9b953cd220c2398eb1b81ab6e5714537db`。
两者包含相同 float32 参数；Windows 转回移动格式逐字节一致。
原模型保存在仓库外 `backups/pre-fused214-20260919/`。

融合先物化 `0.2*log(P_原发布)+0.8*log(P_mohu)`，保留二元观察并集，
再按三元直接贡献裁剪并补给回退。它不是重新训练的原始计数 KN 模型，
也不强制将评分归一化。旧集与 fresh 集共两万条首选正确 19,852（99.260%），
原发布版为 19,800；该评测使用 Rime `201eb79`、compact、无 Qwen/学习/提前上屏。
语料已参与权重选择，不是独立确认，也不代表 Windows/Qwen 或提前上屏的实机准确率。
实验完整结果在 `next/_run/ngram-214-materialize-20260919/`。

## 训练 KN V2

先对完整语料做有界并行计数：

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- ^
  --corpus-root C:\Archive\brightmart_nlp_chinese_corpus ^
  --articles-root C:\Archive\articles ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16-articles ^
  --workers 16 --entries-per-run 2000000 --merge-fan-in 64 ^
  --min-bigram-count 1 --min-trigram-count 1
```

`--articles-root` 可省略。指定后会递归读取其中所有 UTF-8 `.txt` 文件，每个文件
作为一篇权重为 1 的文章，并处理全文；JSON 字段仍受
`--max-field-characters` 限制，纯文本文章不受该字段上限截断。

再用相同裁剪参数构建文章增强档：

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- build-model ^
  --counts C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16-articles ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-kn-m30-r20-p050-w025-articles ^
  --min-bigram-export-count 30 --min-trigram-export-count 30 ^
  --rescue-min-bigram-count 20 --rescue-min-trigram-count 20 ^
  --rescue-min-conditional-probability 0.5 --rescue-probability-weight 0.25
```

该档以 count 30 为主体，对 count 20–29 且条件概率至少 0.5 的项按 0.25 权重软
救回。归档评测中模型约 228 MiB，在四类来源各 2500 条的独立验证集上 Top-1
为 98.84%。这是离线基准，不等同于真实输入法端到端准确率。

构建完成后复制到 runtime 目录，并重新执行 Core 测试、golden 导出和独立验证集。

## 移动模型

```bash
python3 tools/convert_sentence_ngram_mobile.py \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

TCSKNM02 只重排记录和增加分页索引，不改 float32 概率。转换后必须核对模型 hash，
并分别运行 Rime 增量测试和 Fcitx5 C++ golden/真实模型测试。

## Qwen 检查

```bash
/mnt/c/Users/yc/AppData/Local/TigerClawML/venv-directml/Scripts/python.exe \
  tools/test_sentence_qwen.py --device cpu --dtype float32 --show-tokens
```

该程序不连接 Core。当前运行时的 Qwen scorer 对每个候选做孤立文本概率评分，没有
Tiger 编码上下文，因此短候选可能被错误提升；这是已知方法限制，不是候选索引问题。

## 评测原则

- 调参集、测试集和最终验证集必须按原始记录隔离。
- 同时报 Top-1、MRR、修正/退化次数和解码墙钟时间。
- 多进程 CPU 时间总和不能当作单句延时。
- 候选或分数规则变化时更新 `next/TigerClaw.Core.Tests` 和 sentence golden。
- 真实运行模型测试必须明确打印加载的文件、大小、格式和 SHA-256；不允许静默
  降级后仍报告“测试通过”。
