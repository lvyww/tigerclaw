# 整句模型训练与离线实验

本目录包含整句语料处理、n-gram 训练、模型转换和离线评测工具。当前 Windows
运行时只使用：

- `sentence-ngram-v2.bin`：完整语料裁剪后的 Modified Kneser-Ney V2；
- `sentence-qwen-q8.gguf`：Qwen3 0.6B Base Q8，重排前五个 n-gram 候选。

两者均为仓库外原始文件，发布后只读映射，不加密。旧 compact n-gram、字符
Transformer 和 ONNX 流程只保留作历史实验，不进入当前运行时。

## 目录和工具

- `SentenceNgramTrainer/`：Windows .NET 10 外部计数与 V2 模型构建。
- `convert_sentence_ngram_mobile.py`：把 V2 无损重排成移动端 TCSKNM02。
- `export_tiger_sentence_rime.py`：导出 Rime 虎整句码表和排名数据。
- `benchmark_sentence_gram.py`：离线比较 n-gram/搭配实验。
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

`next/build_next.bat`、`publish.bat` 和 `publish_arm64.bat` 从这些仓库外位置复制
运行模型。不要把模型复制回源码目录长期保存。

## 训练 KN V2

先对完整语料做有界并行计数：

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- ^
  --corpus-root C:\Archive\brightmart_nlp_chinese_corpus ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16 ^
  --workers 16 --entries-per-run 2000000 --merge-fan-in 64 ^
  --min-bigram-count 1 --min-trigram-count 1
```

再构建当前推荐档：

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- build-model ^
  --counts C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16 ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-kn-m30-r20-p050-w025 ^
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
