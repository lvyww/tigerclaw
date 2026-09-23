# Rime TCSKNM03 Q8 实验

2026-09-24，用户要求尝试 8 位量化。只做独立模型和读取器实验，不修改主线、安装或发布。结果归档：`C:\Archive\rime-q8-20260924\REPORT.md`。

已完成结果：模型 356.49 MB；完整 ZIP 312.89 MB、7z 221.00 MB。
冻结历史 20k 首选全部不变（19,929 正确）；新闻 30k 29,935→29,937，
articles 33,129 句 32,825→32,827。三组共 7 个首选变化：5 句救回、1 句退步、1 句两版都错。
另用当前 Rime 解码器复测同一历史 20k，Q16/Q8 首选全部不变，均 19,930 正确。
这是离线总体持平的证据，不表示量化改善泛化，也没有消除个别退步。

## 格式

输入是主线 `brightmart-char5-context128-tcs3-q16`，SHA256
`4e6d79b957a55edf35cd9e2e66c62bd0bbe598581b7dc088b462122a713172a7`。

输出仍用 `TCSKNM03` magic，但 **version=2**。不要误用 mohu 的 TCSKNM04：那是另一种格式。

- 概率：`q8 = round(q16 / 257)`，256 级。
- 回退：原码零保留；其他码为 `1 + round((q16 - 1) * 254 / 65534)`。
- header 内 min 定点单位仍是 `1e-7`；step 单位改为 `1e-9`，避免 Q8 的较大步长溢出 uint32。
- 概率和回退记录各占 1 字节；词 ID、后继个数、索引结构不变；所有偏移重建。
- 所有词和记录保留，不剪枝、不训练。不把“量化后数值为零”当成“未观察到”。
- 这是从已发布 Q16 重新量化，含已有的 Q16 误差；不是 KenLM 的 Q8 算法。

模型大小 356,492,204 字节，SHA256
`5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361`。

`tiger_sentence_fivegram.lua` 是主线 `9f742d2` 读取器的实验副本，支持 version 1/2。旧版读取器会拒绝 version 2，所以试用时必须同时更新该文件与模型；主线运行文件没有被覆盖。

## 复现

在仓库根目录：

```sh
g++ -std=c++20 -O3 -Wall -Wextra tools/FullPinyinEval/HigherOrder/TcsQ8/requantize.cpp -o /tmp/requantize-tcs-q8
/tmp/requantize-tcs-q8 /home/yc/tmp/shape-mix-rime/mainline-preserved.bin /tmp/sentence-fivegram-q8.bin
```

将读取器置于独立 `reader/lua/tiger_sentence_fivegram.lua` 后：

```sh
python3 tools/FullPinyinEval/HigherOrder/TcsQ8/validate.py INPUT_Q16 OUTPUT_Q8 READER_ROOT VALIDATION_OUTPUT
python3 tools/FullPinyinEval/HigherOrder/evaluate_external_shape.py --cases CASES --fixture /home/yc/tmp/tiger-shape-direct5/fixture --model OUTPUT_Q8 --tcs-reader-root READER_ROOT --output EVAL_OUTPUT --workers 4
```

本轮工作目录为 `/home/yc/tmp/rime-q8-20260924`。`evaluate_current.py` 对该目录中的 `current-q16`、`package` 两份隔离运行时执行实际主线对照，独立于冻结解码器评测。两份运行时使用同一新版读取器，分别加载 Q16/Q8 模型。

`report.py WORK_DIR` 核对样本和解码器哈希，输出逐句 CSV 和各集救回/退步统计。保留完整分母，包括目标不在候选池的行。历史 20k 在两种解码器下重复评测，不能将两行当作独立样本相加。

语料和测试集均沿用已有版本，未依照本次准确率调整量化参数。实际前端输入和延迟未验收。
