# 虎整句（Rime）

独立方案，不依赖万象虎。码表从 `release_arm64/码表/虎整句/常用字词.txt` 导出。字频前 1500 的单字只保留最优码；更生僻的单字保留非最优码。

## 重新生成

```bash
python3 tools/export_tiger_sentence_rime.py
```

## 部署到小狼毫

把本目录的 `tiger_sentence.schema.yaml`、`tiger_sentence.dict.yaml`、`tiger_sentence.supplement.txt`、`rime.lua` 拷到 `%APPDATA%\Rime`，把 `lua\` 拷到 `%APPDATA%\Rime\lua`，再在 `default.custom.yaml` 里加入 `tiger_sentence`，然后「重新部署」。默认把编码显示在候选窗口；用户自行开启 `inline_preedit` / `preedit_type: composition` 时，Lua 仍通过 `Candidate.preedit` 维护当前候选的分段编码和提前上屏后的实时尾码。

## 补充语料

用户目录根部的 `tiger_sentence.supplement.txt` 用于提升模型尚未覆盖的新词、流行词和个人词语。文件使用 UTF-8 编码，每行格式为 `词条 [权重]`，词条与可选正整数权重之间用空格或制表符分隔；省略权重时默认为 1000，空行和 `#` 注释会被忽略，重复词条以最后一行为准。用户明确写入的词条视为强偏好，奖励公式与 Windows Core 一致：`clamp(9.0 + 2.0 * ln(weight / 1000), 0, 16)`。奖励参与 Beam 排序但不计入置信度，文件不存在时保持原排序和快速路径。修改后重新部署以重新加载 Lua 模块。

## 按键

- 字母连打整段编码
- 空码时数字直接上屏；有编码时 `;` / `'` / 数字写入编码，分别选第 2、第 3、第 N 候选（`0` 为第 10）
- 空格上屏当前整句；回车上屏原始编码；Esc 清码
- `提前上屏` 默认开启，可从方案选单关闭。开启后，原始编码超过 4 键时，取三个严格连续一键追加代各自达到 0.995 置信度的最长提案之公共前缀，并要求该前缀在三代中对应相同的原始编码终点；置信度覆盖完整的 200 状态最终 beam、合并同文不同路径的概率质量，并纳入“已完成路径 + 合法但尚未打完的尾码前缀”。尾码假设只参与提前上屏判断，不进入候选窗，也不改变正常排序；如果任一参与计算的 beam 已被裁剪，本轮不会提前提交
- 每次可提前提交一个或多个稳定新字、距离上次提交至少新增三个编码键，并保留尚未稳定的全部后缀（至少保留候选最后一个字）；退格会清空稳定证据，手动上下翻选或 Tab 遍历候选后会暂停本次 composition 的提前上屏
- 实时未提交尾码最多接受 128 个原始编码字符；已提前提交、仅用于语言模型上下文的编码不占这个上限
- Rime Lua 的 partial commit 必须通过 `commit_text`、`clear`、`push_input` 重建 composition；提交粒度与冷却可减少候选窗刷新，但不同前端是否出现单帧闪烁仍需实机验证
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

默认的 `--index-stride 64` 面向移动端内存占用。桌面端如果更重视按键延迟，
可用 `--index-stride 16` 重新生成同名模型；常驻稀疏索引会增加约 6 MiB，
但首次遇到一个语言模型上下文时最多扫描的记录数降为原来的四分之一。

查找顺序优先 mobile 文件，找不到时仍兼容原来的 TCSKNM01 文件：

1. Rime 用户目录下的 `models/sentence-ngram-mobile.bin`
2. Rime 用户目录下的 `sentence-ngram-mobile.bin`
3. Rime 共享目录下的 `models/sentence-ngram-mobile.bin`
4. 上述位置对应的 `sentence-ngram-v2.bin`
5. 开发机 `C:\Archive\tigerclaw_sentence_ml\runtime` 下的 mobile、再到原模型

当前是纯 Lua 查表。解码对准确率无损：增量复用格网，追加时只生成跨过旧输入末端的新边，避免旧路径被重复计入置信度；退格直接取前缀状态。高歧义位置在扩展期间自适应合并相同文本，使用确定性的精确 Top-K 选择而不排序随后会丢弃的状态，处理完再冻结成紧凑数组，避免字典常驻整段 composition。缓存精确 KN logp 和真实二元组查询，并缓存分页模型的上下文位置；这些缓存都有固定上限，防止长时间使用后持续增长。码表候选的选重过滤和 UTF-8 拆分也会按词条复用。EOS 只在出候选时另算，不写回 beam。Beam 扩展时每输出一个 Unicode 字符增加 2.0 分，与 Core 使用相同的编码条件长度先验，抵消同码候选中纯语言模型对短文本的偏好。出候选时再减去与 Core 相同的孤立生僻字惩罚：字频排名大于 3000、且左右都没有模型里真实出现过的二元组，每个字减 2。字频表由导出脚本写成 `lua/tiger_sentence_ranks.lua`。短码全检索与首选优先规则也与 Core 一致。不要把大模型提交进 Git。

增量一致性测试默认允许无模型降级，并明确打印实际加载状态；需要验证真实模型时使用支持 `string.unpack` 和 `utf8` 的 Lua 5.3+ 执行 `lua tools/test_tiger_sentence_incremental.lua . --require-model`。LuaJIT 2.1 不具备这两个标准库 API，只适合测试无模型路径。
