# 虎整句（Rime 独立实验版）

本方案使用纯 Lua 码表格图和本地 n-gram，不依赖 Windows Core，也不是 Windows
TSF 发布包的一部分。它仍处于实验阶段，尤其是自动提前上屏在不同 Rime 前端上的
闪烁、提交顺序和长时间稳定性需要分别验收。

## 生成与部署

```bash
python3 tools/export_tiger_sentence_rime.py
```

导出器从 `release_arm64/码表/虎整句/常用字词.txt` 生成方案数据。该目录是本机
日常 ARM64 运行环境，禁止用清理命令删除。

部署到 Rime 用户目录：

1. 复制 `tiger_sentence.schema.yaml`、`tiger_sentence.dict.yaml`、
   `tiger_sentence.supplement.txt` 和 `rime.lua`；
2. 复制 `lua/`；
3. 在已有 `default.custom.yaml` 中加入 `tiger_sentence`；
4. 重新部署，使 Lua 和补充语料重新加载。

不要覆盖用户已有的 `rime.lua` 或 `default.custom.yaml`；合并相应条目。默认在候选
窗口显示编码。用户自行开启 inline preedit 时，Lua 仍通过 `Candidate.preedit`
维护当前候选的分段编码。

## 输入行为

- 字母连续输入整句编码；空格提交候选，回车提交原始编码，Esc 清空。
- 有编码时，`;`、`'`、数字分别选择码表第 2、第 3、第 N 项，`0` 为第 10 项。
- Up/Down 或 Tab/Shift+Tab 遍历可见候选。
- 一码段只在整段输入只有一码时合法。
- 整段不超过四码时允许当前码位全部词条，但首选路径排在后选之前；更长输入的
  裸码只取首选，非首选必须显式选重。
- 分段空格只用于显示，不进入 raw code。

自动提前上屏默认开启：raw 长度超过四键后，每个严格连续的一键追加代都需要达到
0.995 置信度且公共前缀 raw 边界一致。连续两代的前缀质量都达到 0.99999 时两代即可
确认，否则仍需要三代。退格或手动遍历会清除/暂停证据。每次提交保留完整不稳定
后缀和至少最后一个候选字。Rime Lua 只能用 `commit_text`、`clear`、`push_input`
重建 composition，因此不同前端可能出现单帧刷新。

## 补充语料

用户目录根部的 `tiger_sentence.supplement.txt` 使用 UTF-8：

```text
# 词条 权重
茧师 1000
新词
```

权重默认为 1000，重复词条以最后一项为准。奖励与 Windows Core 相同：
`clamp(9 + 2 * ln(weight / 1000), 0, 16)`。它参与 Beam 排序但不进入提前上屏
置信度，不会创建码表中不存在的编码。修改后需重新部署。

## 模型

移动端优先使用仓库外的 `sentence-ngram-mobile.bin`（TCSKNM02）。它是 V2 模型的
无损分页重排，常驻稀疏索引约 2.1 MiB，上下文页 LRU 上限 8 MiB，不会一次读入
完整模型。

```bash
python3 tools/convert_sentence_ngram_mobile.py \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-v2.bin \
  /mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

查找顺序：用户目录 `models/`、用户目录根部、共享目录 `models/`，再尝试对应的
`sentence-ngram-v2.bin`；开发机最后回退到 `C:\Archive\tigerclaw_sentence_ml\runtime`。
大模型不得提交 Git。

## 验证

无模型路径：

```bash
lua tools/test_tiger_sentence_incremental.lua .
```

真实模型路径需要 Lua 5.3+：

```bash
lua tools/test_tiger_sentence_incremental.lua . --require-model
```

测试输出必须明确显示实际模型。LuaJIT 2.1 缺少这里使用的标准 `string.unpack` 和
`utf8` API，只适合无模型测试。候选顺序、增量/全量一致性、补充语料和提前上屏
证据应与 C# golden 分开核对。
