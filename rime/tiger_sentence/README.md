# 虎整句（Rime）

独立方案，不依赖万象虎。码表从 `release_arm64/码表/虎整句/常用字词.txt` 导出。字频前 1500 的单字只保留最优码；更生僻的单字保留非最优码。

## 重新生成

```bash
python3 tools/export_tiger_sentence_rime.py
```

## 部署到小狼毫

把本目录的 `tiger_sentence.schema.yaml`、`tiger_sentence.dict.yaml`、`rime.lua` 拷到 `%APPDATA%\Rime`，把 `lua\` 拷到 `%APPDATA%\Rime\lua`，再在 `default.custom.yaml` 里加入 `tiger_sentence`，然后「重新部署」。

## 按键

- 字母连打整段编码
- 空码时数字直接上屏；有编码时 `;` / `'` / 数字写入编码，分别选第 2、第 3、第 N 候选（`0` 为第 10）
- 空格上屏当前整句；回车上屏原始编码；Esc 清码
- 上下方向键或 Tab / Shift+Tab 遍历候选
- 一码字只在整段就是那一码时合法
- 整段不超过 4 码时检索该码位全部字词，非首选按码表顺序排在首选之后；超过 4 码后裸码只取首选

语言模型用仓库外的 `sentence-ngram-v2.bin`（TCSKNM01），由 Lua 在首次解码时整文件读入。查找顺序：

1. `%APPDATA%\Rime\models\sentence-ngram-v2.bin`
2. `%APPDATA%\Rime\sentence-ngram-v2.bin`
3. `C:\Archive\tigerclaw_sentence_ml\runtime\sentence-ngram-v2.bin`

当前是纯 Lua 查表。解码对准确率无损：增量复用格网（追加只重解后缀、退格直接取前缀状态），并缓存精确 KN logp；EOS 只在出候选时另算，不写回 beam。出候选时再减去与 Core 相同的孤立生僻字惩罚：字频排名大于 3000、且左右都没有模型里真实出现过的二元组，每个字减 2。字频表由导出脚本写成 `lua/tiger_sentence_ranks.lua`。短码全检索与首选优先规则也与 Core 一致。不要把 228MB 模型提交进 Git。
