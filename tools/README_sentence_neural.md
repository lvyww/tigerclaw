# 整句神经模型离线实验

这些工具用于离线数据处理、训练、评测和导出。TigerClaw.Core 使用导出的
`sentence-ngram.bin`，可选的 `TigerClaw.Sentence.exe` 使用导出的 ONNX 模型；
训练过程本身不进入输入法运行时。

## 工具

- `prepare_sentence_neural_data.py`：流式清洗 brightmart 语料，按文档划分训练、验证、测试集，生成 UTF-8 文本和 UInt16 token 文件。
- `sentence_neural_model.py`：小型字符因果 Transformer。
- `benchmark_sentence_neural.py`：CPU/DirectML 训练吞吐测试。
- `train_sentence_neural.py`：训练、验证和可恢复 checkpoint。
- `export_sentence_neural.py`：去除优化器状态，导出紧凑推理模型。
- `export_sentence_neural_onnx.py`：把推理 checkpoint 导出为独立进程使用的 ONNX 模型。
- `export_sentence_ngram_binary.py`：把实验 JSON n-gram 转为 Core 使用的紧凑二进制模型。
- `sentence_neural_reranker.py`：批量计算完整候选句的神经语言分。
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

Python 是隔离的 Windows x64 3.12 环境，通过 `torch-directml` 使用 Adreno GPU；
没有加入 PATH，也不替换系统 Python。

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

## 运行时模型导出

```bash
python3 tools/export_sentence_ngram_binary.py \
  --input /mnt/c/Archive/tigerclaw_sentence_ml/baseline/ngram-20000.json.gz \
  --output /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram.bin

/mnt/c/Users/yc/AppData/Local/TigerClawML/venv-directml/Scripts/python.exe \
  tools/export_sentence_neural_onnx.py \
  --checkpoint C:/Archive/tigerclaw_sentence_ml/model10m/best.pt \
  --output C:/Archive/tigerclaw_sentence_ml/runtime/sentence-transformer.onnx
```

`next/build_next.bat` 从上述 runtime 目录复制模型，并从隔离 Python 环境的
`onnxruntime/capi` 复制 Windows x64 原生库。首次准备环境时安装与托管程序集
同版本的 CPU wheel：

```batch
C:\Users\yc\AppData\Local\TigerClawML\venv-directml\Scripts\python.exe -m pip install onnxruntime==1.24.4
```

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
