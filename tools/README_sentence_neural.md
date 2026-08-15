# 整句神经模型离线实验

这些工具用于离线数据处理、训练、评测和导出。TigerClaw.Core 使用导出的
`sentence-ngram-v2.bin`（发布时为受保护的 `.tcmodel`）。当前可选的
`TigerClaw.Sentence.exe` 通过 llama.cpp 使用 Qwen3 0.6B Q8 GGUF 重排前 5 个候选；
本页的小型字符 Transformer/ONNX 流程保留用于历史离线对比，不再进入输入法运行时。

## 工具

- `prepare_sentence_neural_data.py`：流式清洗 brightmart 语料，按文档划分训练、验证、测试集，生成 UTF-8 文本和 UInt16 token 文件。
- `sentence_neural_model.py`：小型字符因果 Transformer。
- `benchmark_sentence_neural.py`：CPU/DirectML 训练吞吐测试。
- `train_sentence_neural.py`：训练、验证和可恢复 checkpoint。
- `export_sentence_neural.py`：去除优化器状态，导出紧凑推理模型。
- `export_sentence_neural_onnx.py`：把推理 checkpoint 导出为独立进程使用的 ONNX 模型。
- `export_sentence_ngram_binary.py`：把实验 JSON n-gram 转为旧版紧凑二进制模型，仅供历史离线对比。
- `sentence_neural_reranker.py`：批量计算完整候选句的神经语言分。
- `test_sentence_qwen.py`：使用本地 Qwen3 Base 模型独立比较候选句概率，不连接输入法运行时。
- `benchmark_sentence_gram.py`：用同一套长编码、码表和 Beam Search 批量比较现有字符三元模型与 Rime BGC+BGW。
- `prepare_sentence_benchmark_cases.py`：从独立验证语料生成按来源均衡、按原始记录限额的扩大测试集。
- `rime_gram_model.py`：只读映射 `Rime::Grammar/1.0` 的 BGC/BGW 实验加载器。
- `evaluate_sentence_decoder.py`：比较字符、词频和神经重排排名。
- `evaluate_sentence_neural_pools.py`：在 Windows/DirectML 上重排由 WSL 导出的候选池。
- `try_sentence_input.py`：独立的 Windows 图形实验程序，按单字最优码和词语编码自动切分长编码并实时显示候选。
- `run_sentence_input_demo.bat`：使用隔离的 DirectML Python 环境启动图形实验程序。

## 当前 Windows 训练环境

```text
Python: C:\Users\yc\AppData\Local\TigerClawML\Python312-x64
venv:   C:\Users\yc\AppData\Local\TigerClawML\venv-directml
data:   C:\Archive\tigerclaw_sentence_ml\pilot200m
model:  C:\Archive\tigerclaw_sentence_ml\model10m
```

正式发布时，`publish.bat` 只把 n-gram 压缩并加密为经过 HMAC 校验的 `.tcmodel`；
Qwen Q8 GGUF 不加密并直接随包提供。发布密钥位于仓库外的
`C:\Archive\tigerclaw_sentence_ml\runtime\model-protection.key`，不要提交该密钥。

Python 是隔离的 Windows x64 3.12 环境，通过 `torch-directml` 使用 Adreno GPU；
没有加入 PATH，也不替换系统 Python。

## Qwen3 候选评分实验

下载 `Qwen/Qwen3-0.6B-Base` 后，可在已有 Windows DirectML 隔离环境中运行：

```bash
/mnt/c/Users/yc/AppData/Local/TigerClawML/venv-directml/Scripts/python.exe \
  tools/test_sentence_qwen.py --device cpu --dtype float32 --show-tokens
```

程序同时显示整句总对数概率、token 均分和汉字均分。它只读取本地模型，
不连接 Core，也不会改动输入法配置。当前设备上的短句批量评分以 CPU 更快；
`--device directml` 可用于对照测试，但不建议使用 DirectML FP16。

## Rime BGC+BGW 离线对比

下面的实验会从每条原始长编码重新构建完整格图并分别解码，不是只重排现成的
前若干候选。两组使用相同码表、Beam 宽度和选重惩罚；不会启动或修改 Core：

```bash
python3 tools/benchmark_sentence_gram.py \
  --max-cases 0 \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/baseline/bgc-bgw-character-vs-trigram-test-1000.json
```

默认逐字累计 BGC+BGW 搭配分，权重为调参集粗略选出的 `BGC=0.2`、`BGW=1.0`。
`--gram-mode boundary` 可模拟更接近 Rime 插件的“每个词典候选边界查询一次”方式。
2026-08-14 在 1000 条独立测试句加 1 条手工回归句上的结果为：现有三元模型
Top-1 97.60%、MRR 0.9859、平均 25.78 ms；BGC+BGW Top-1 67.13%、MRR
0.7627、平均 25.28 ms。这里的时间只统计已加载模型后的 Python 解码，不代表
以后原生运行时的启动和内存开销。已知的“不带一丝矫揉造作”由错误首选
“不蒙良为矫揉造作”改善为“**不带一丝**是远揉造作”，但正确整句仍排第二。
因此 BGC+BGW 适合作为补充搭配特征继续试验，不适合直接替换现有三元模型。

保留三元概率并把 BGW 作为小权重搭配奖励的实验命令为：

```bash
python3 tools/benchmark_sentence_gram.py \
  --experiment trigram-bgw \
  --max-cases 0 \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/baseline/trigram-bgw-002-vs-trigram-test-1000.json
```

在200条调参句上，`BGW=0.02` 和 `0.05` 没有改变任何首选；从 `0.8` 开始
出现净负收益。取较保守的 `0.02` 后，在同一批1000条独立测试句加手工回归句
上，Top-1 从97.60%升至97.70%，MRR从0.9859升至0.9866：原测试集首选
全部保持不变，并将“不带一丝矫揉造作”从第二候选提升为首选。因此当前实验
支持“现有三元模型为主、BGW低权重补充”，但在更多真实错例验证前仍不接入运行时。

扩大测试集可用下面两条命令复现。Benchmark 默认使用16个工作进程；传入
`--workers 1` 可串行执行并验证结果一致性：

```bash
python3 tools/prepare_sentence_benchmark_cases.py

python3 tools/benchmark_sentence_gram.py \
  --experiment trigram-bgw \
  --cases /mnt/c/Archive/tigerclaw_sentence_ml/baseline/tiger-sentence-validation-10000-cases.json \
  --max-cases 0 \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/baseline/trigram-bgw-002-vs-trigram-validation-10000.json
```

该测试集使用 webtext、news、baike 的独立验证文件。wiki 没有单独验证文件，
所以计数器固定保留每100条中的第100条不训练，验证集只从这1%保留记录取样。
四类来源各2500条，每条原始记录最多取1句，并排除原调参和测试集的2000条。
16进程完整运行时，各工作进程解码CPU时间之和不能当作单句墙钟延迟。

## Windows全量三元计数器

`SentenceNgramTrainer` 是独立的 Windows `.NET 10` 离线工具。它使用有界队列、
16个消费者的局部计数字典、有序运行段和多路归并；不连接Core，也不修改当前
运行时模型。完整计数命令为：

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- ^
  --corpus-root C:\Archive\brightmart_nlp_chinese_corpus ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16 ^
  --workers 16 --entries-per-run 2000000 --merge-fan-in 64 ^
  --min-bigram-count 1 --min-trigram-count 1
```

2026-08-14 的全量运行扫描15.7 GB输入和约902万条记录，处理54.12亿个
加权字符转移，用时221.2秒，峰值工作集3.66 GiB；输出789万个二元和
1.3065亿个未裁剪三元计数，共1.6 GB。wiki固定保留1%记录不参与训练，供
后续独立验证。`--sample-modulus 100` 可扫描全部文件并只处理1%记录；该基准
用时16.6秒、峰值1.66 GiB。1线程和16线程的小样本输出已做字节级一致性验证。

## 全量Modified Kneser-Ney模型

`build-model` 从上述精确计数生成字级三元 Modified Kneser-Ney 模型。它根据
count-of-counts估计三档折扣，并使用续接概率作为二元和一元回退。裁掉的观察项
所占概率质量会重新分配给回退项，因此任意裁剪阈值下每个上下文仍保持归一化。

```batch
dotnet run --project tools\SentenceNgramTrainer\TigerClaw.SentenceNgramTrainer.csproj ^
  -c Release -- build-model ^
  --counts C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-counts-w16 ^
  --output C:\Archive\tigerclaw_sentence_ml\trainer_v2\full-kn-m30-r20-p050-w025 ^
  --min-bigram-export-count 30 --min-trigram-export-count 30 ^
  --rescue-min-bigram-count 20 --rescue-min-trigram-count 20 ^
  --rescue-min-conditional-probability 0.5 --rescue-probability-weight 0.25
```

使用四类来源各2500条的非重叠验证集，并额外加入“不带一丝矫揉造作”回归句，
Beam Width固定为2000。结果如下；修正/退化是相对原20000条采样三元模型的
Top-1变化次数。

| 最低导出计数 | 文件大小 | Top-1 | MRR | 修正 | 退化 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 原采样三元 | 约14 MB | 95.17% | 0.97075 | - | - |
| 3 | 797 MB | 99.45% | 0.99685 | 446 | 18 |
| 10 | 390 MB | 99.27% | 0.99579 | 436 | 26 |
| 20 | 273 MB | 98.93% | 0.99389 | 423 | 47 |
| 30 | 226 MB | 98.82% | 0.99320 | 420 | 55 |
| 30 + 条件软救回 | 228 MB | 98.84% | 0.99328 | 421 | 54 |

当前推荐档以计数阈值30为主体，并对计数20--29、上下文条件概率至少50%的项
进行25%概率权重的软救回。它只比硬裁剪增加约2 MiB；10000条验证中相对硬裁剪
新增2次首选修正且没有首选退化，并修复“虎码官方整句版”被硬阈值误删局部统计
的问题。阈值20保留为偏准确率的备选，阈值3则是准确率上限参考。Core只读取
V2格式：开发版直接映射原始文件，发布版验证并解密到匿名页文件映射，不再兼容
旧版紧凑三元模型。ARM64开发发布先使用未加密模型验证真实输入效果，x86/x64
正式发布仍使用受保护容器。

完整对比命令：

```bash
python3 tools/benchmark_sentence_gram.py \
  --experiment kneser-ney \
  --kneser-ney /mnt/c/Archive/tigerclaw_sentence_ml/trainer_v2/full-kn-m30-r20-p050-w025/sentence-ngram-v2.bin \
  --cases /mnt/c/Archive/tigerclaw_sentence_ml/baseline/tiger-sentence-validation-10000-cases.json \
  --workers 16 --max-cases 0 \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/baseline/kneser-ney-m30-r20-p050-w025-vs-trigram-validation-10000.json
```

## 主要命令

```bash
python3 tools/prepare_sentence_neural_data.py \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/pilot200m \
  --train-characters 200000000 \
  --valid-characters 2000000 \
  --test-characters 2000000
```

```bash
/mnt/c/Users/yc/AppData/Local/TigerClawML/venv-directml/Scripts/python.exe \
  tools/train_sentence_neural.py \
  --data C:/Archive/tigerclaw_sentence_ml/pilot200m \
  --output C:/Archive/tigerclaw_sentence_ml/model10m \
  --device directml \
  --train-tokens 200000000 \
  --batch-size 64
```

中断后增加 `--resume` 可从 `latest.pt` 继续。生成的语料和模型不提交 Git。
长时间 DirectML 训练使用 `run_sentence_training_segments.ps1` 分段重启，避免
checkpoint 暂存缓冲长期占用共享内存。

## 运行时模型准备

```bash
cp /mnt/c/Archive/tigerclaw_sentence_ml/trainer_v2/full-kn-m30-r20-p050-w025/sentence-ngram-v2.bin \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin

```

Core只识别V2文件名和V2格式，不再读取旧版 `sentence-ngram.bin`。
`next/build_next.bat` 从上述 runtime 目录复制 n-gram，并从
`C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-gguf\downloaded\Qwen3-0.6B-Base-Q8_0.gguf`
复制 Qwen 模型。原生评分库由固定版本的 `third_party/llama.cpp` 子模块构建。

## 实时试用程序

在 Windows 资源管理器中双击：

```text
tools\run_sentence_input_demo.bat
```

程序加载完成后直接键入连续编码。每个字优先采用最短的首选编码，即该编码下
无需选重即可得到这个字；不存在首选编码时才采用最短编码，并用选重符号确定
非首选字。编码下还有其他候选不影响首选码判定。只有整个输入本身只有一码时
才允许一码切分。所有未带选重符的编码段都只返回该码位的第一候选；第二候选
及以后必须显式选重。其他情况下，每个切分连同选重标记至少占用二码。因此 `j2` 属于合法的
二码切分，而长串中的裸 `j` 不合法。不再使用“一码+空格”。码表中的
词语可以作为一条候选路径输出。编码后加 ASCII 分号表示明确选择第二候选，
加 ASCII 单引号表示第三候选，加数字表示指定候选位（`0` 表示第10位，也支持
`10` 及更大的十进制位次）。
退格、粘贴和清空都会触发候选刷新。“载入示例”会填入“今天早上我吃了两个
面包三根油条”的逐字最优编码。
默认使用字符模型产生 Beam，只对前20个候选使用神经权重0.40重排；旧词频权重
默认关闭。界面只用于离线效果验证，不连接 Core，也不会修改配置。

可使用一次性模式检查指定编码而不打开窗口：

```batch
tools\run_sentence_input_demo.bat --decode-once 连续最优码编码
```
