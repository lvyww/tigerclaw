# 高阶联合字音模型：冻结候选池重排

后续完整五阶 Beam、量化、上下文剪枝和逐键测试见 [BEAM_EXPERIMENT.md](BEAM_EXPERIMENT.md)。

2026-09-21，用户提供 `C:/Archive/tigerclaw_sentence_ml/joint_5gram/joint_5gram.{arpa,klm}`。

实验输出：`C:/Archive/tigerclaw_sentence_ml/experiments/joint-5gram-rerank-20260921/`。
`run.py` 固定读取原 joint Beam 200 的 8,019 条 test、每条 Top-50 文本及原字音路径。
KenLM 虚接口管理完整原生状态，支持五阶；一次 BOS/EOS，log10 转自然对数、每字 +2。
不使用 Qwen、分数融合、用户学习或训练集调参。它不是五阶 Beam 搜索：已被三元搜索
丢弃的候选或最终按文本去重丢弃的另一读音无法救回。

`score.cpp` 编译为 Linux ARM64 共享库，链接归档的 libkenlm.so；不要部署至 Windows。
`run.py` 为本次实验的固定路径可复现驱动，输出存在则拒绝覆盖。
`check.cpp` 独立逐状态 FullScore 检查实际命中阶数，并与截断至两个历史 token 的评分对照。
`bench.py` 对同集按 ID 排序前 100 句，预编码 Top-50，预热一次、交替模型顺序测三次。
耗时只含候选评分和 Python/FFI 开销，不含首次解码、模型加载和 UI。

验证：全部 400,950 条候选用原生状态重算三元分数，与冻结结果最大自然对数差
7.11e-14。完整 ID/输入校验通过；同批 1,050 条微信样本按 ID+code+text 配对。
全量原文命中 4,321 → 4,899，救回 1,002、退化 424；不是语义合理性人工评审。
训练/测试互斥与两模型训练语料一致性未独立验证；前三阶记录数量相同不等于证明训练一致。

没有改动共享解码器、Core、已安装输入法、模型原件或用户数据。

后续 500 MB 预算实验已完成，见 [BUDGET500.md](BUDGET500.md)：三阶 Q8＋剪枝五阶
混合重排合计 466.34 MB，命中 4641/8019（57.875%），尚未达到60%的期望目标。
这是有体积约束时的选择；本页完整五阶重排结果仍保留作为精度参考。没有部署安装版。
