# 虎整句（Rime 独立实验版）

本方案使用纯 Lua 码表格图和本地 n-gram，不依赖 Windows Core，也不是 Windows
TSF 发布包的一部分。它仍处于实验阶段，尤其是自动提前上屏在不同 Rime 前端上的
闪烁、提交顺序和长时间稳定性需要分别验收。

## 生成与部署

```bash
python3 tools/export_tiger_sentence_rime.py
```

导出器从 `release_arm64/码表/虎整句/虎整句.txt` 生成方案数据。该目录是本机
日常 ARM64 运行环境，禁止用清理命令删除。

部署到 Rime 用户目录：

1. 复制 `tiger_sentence.schema.yaml`、`tiger_sentence.supplement.txt` 和 `rime.lua`；
2. 复制 `lua/`；
3. 在已有 `default.custom.yaml` 中加入 `tiger_sentence`；
4. 重新部署，使 Lua 和补充语料重新加载。

不要覆盖用户已有的 `rime.lua` 或 `default.custom.yaml`；合并相应条目。默认在候选
窗口显示编码。用户自行开启 inline preedit 时，Lua 仍通过 `Candidate.preedit`
维护当前候选的分段编码。

## 运行文件与码表

本方案没有使用 Rime 的 `table_translator`，运行时只有两个实质 Lua 模块：

- `lua/tiger_sentence.lua`：输入、整句解码、n-gram 和补充语料逻辑；
- `lua/tiger_sentence_data.lua`：生成的码表候选和字频数据。

根目录的 `rime.lua` 只负责向 Rime 注册 processor 和 translator，不承载算法。
过去的码表分片、用户覆盖 Lua 和单独的模型/补充语料逻辑模块已经合并或删除。

开发仓库中修改 `release_arm64/码表/虎整句/虎整句.txt` 后重新运行导出器即可。
发布包不再提供用户自定义编码覆盖层，避免出现多套码表来源；直接修改其他 YAML
或旧词典文件不会改变本方案候选。

## 输入行为

- 字母连续输入整句编码；空格提交候选，回车提交原始编码，Esc 清空。
- 有编码时，`;`、`'`、数字分别选择码表第 2、第 3、第 N 项，`0` 为第 10 项。
- Up/Down 或 Tab/Shift+Tab 遍历可见候选。
- 一码段只在整段输入只有一码时合法。
- 只有整个独立输入由单一码表边消费时，才隐式允许非首选；多段整句
  路径和自动上屏后的续写只取首选，非首选必须显式选重。
- 分段空格只用于显示，不进入 raw code。

自动提前上屏默认开启：raw 长度超过四键后，每个严格连续的一键追加代都需要达到
0.995 置信度且公共前缀 raw 边界一致。连续两代的前缀质量都达到 0.99999 时两代即可
确认，否则仍需要三代。退格或手动遍历会清除/暂停证据。每次提交保留完整不稳定
后缀并至少保留三个已完成解码的编码。唯一候选后追加字母形成空码时，会先检查该尾段能否继续
补成有效编码；确认已经不可能匹配后，提交原唯一候选并保留新输入的编码。Rime Lua
只能用 `commit_text`、`clear`、`push_input` 重建 composition，因此不同前端可能出现
单帧刷新。候选窗坐标和上下翻转由 Rime 前端管理，不属于 Lua 方案能力范围。

注意：Windows Core 于 2026-09-01 改为独立前缀累计、中性不完整尾码/低置信度完整代
和加权候选边界封闭检查；本 Rime 实验包尚未同步这组规则，验证完成前不要宣称行为完全一致。

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

## 性能与诊断

模型页和上下文查询使用有界缓存；候选的生僻字孤立惩罚另有 8192 项有界缓存，
避免连续按键时反复扫描和查询相同字串。缓存满后循环替换旧项，不会随使用时间
无限增长。

Lua 按一次 composition 在内存中汇总解码次数、模型缺页、读取字节数、提前上屏
证据构建次数和孤立惩罚缓存命中，不自动写入日志。调试时可从模块的
`performance_status()` 查看当前及上一次汇总。

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
