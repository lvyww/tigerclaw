# 虎整句（Rime）

独立方案，不依赖万象虎。码表从 `release_arm64/码表/虎整句/常用字词.txt` 导出。字频前 1500 的单字只保留最优码；更生僻的单字保留非最优码。

## 重新生成

```bash
python3 tools/export_tiger_sentence_rime.py
```

## 部署到小狼毫

把本目录的 `tiger_sentence.schema.yaml`、`tiger_sentence.dict.yaml`、`rime.lua` 拷到 `%APPDATA%\Rime`，把 `lua\` 拷到 `%APPDATA%\Rime\lua`，再在 `default.custom.yaml` 里加入 `tiger_sentence`，然后「重新部署」。默认把编码显示在候选窗口；用户自行开启 `inline_preedit` / `preedit_type: composition` 时，Lua 仍通过 `Candidate.preedit` 维护当前候选的分段编码和提前上屏后的实时尾码。

## 按键

- 字母连打整段编码
- 空码时数字直接上屏；有编码时 `;` / `'` / 数字写入编码，分别选第 2、第 3、第 N 候选（`0` 为第 10）
- 空格上屏当前整句；回车上屏原始编码；Esc 清码
- 开启提前上屏：原始编码超过 4 键后，连续两次达到 0.995 置信度的稳定前缀会提交；始终保留候选最后一个字，已提交前缀继续作为后续解码上下文
- 上下方向键或 Tab / Shift+Tab 遍历候选
- 一码字只在整段就是那一码时合法
- 整段不超过 4 码时检索该码位全部字词，非首选按码表顺序排在首选之后；超过 4 码后裸码只取首选

移动端优先使用仓库外的 `sentence-ngram-mobile.bin`（TCSKNM02）。它保留
`sentence-ngram-v2.bin` 的全部 n-gram 和原始 float32 概率，只把数据重排为上下文页；
Lua 常驻 unigram 和约 2.1MB 的稀疏索引，并用上限 8MB 的 LRU 缓存按需读取上下文页，
不再把 228MB 模型一次性读入内存。生成命令：

```bash
python3 tools/convert_sentence_ngram_mobile.py \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

查找顺序优先 mobile 文件，找不到时仍兼容原来的 TCSKNM01 文件：

1. Rime 用户目录下的 `models/sentence-ngram-mobile.bin`
2. Rime 用户目录下的 `sentence-ngram-mobile.bin`
3. Rime 共享目录下的 `models/sentence-ngram-mobile.bin`
4. 上述位置对应的 `sentence-ngram-v2.bin`
5. 开发机 `C:\Archive\tigerclaw_sentence_ml\runtime` 下的 mobile、再到原模型

当前是纯 Lua 查表。解码对准确率无损：增量复用格网（追加只重解后缀、退格直接取前缀状态），并缓存精确 KN logp；该缓存限制为 32768 项，防止长时间使用后持续增长。EOS 只在出候选时另算，不写回 beam。出候选时再减去与 Core 相同的孤立生僻字惩罚：字频排名大于 3000、且左右都没有模型里真实出现过的二元组，每个字减 2。字频表由导出脚本写成 `lua/tiger_sentence_ranks.lua`。短码全检索与首选优先规则也与 Core 一致。不要把大模型提交进 Git。
