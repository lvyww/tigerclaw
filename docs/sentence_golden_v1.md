# tigerclaw.sentence.golden.v1

阶段 0 黄金快照格式。文件是 UTF-8、无 BOM、LF 换行的 JSONL。同一 C# 版本重复导出必须字节一致。

## 记录类型

第一行是 `meta`。每个用例先有一行 `case`，随后是按时间顺序的 `snapshot`。

### meta

```json
{"record_type":"meta","format":"tigerclaw.sentence.golden.v1","source":"TigerClaw.Core.Tests in-memory fixtures"}
```

不写入时间戳、主机路径或 Git commit，以免破坏确定性。仓库/模型身份见 `docs/FCITX5_ANDROID_BASELINE.md`。

### case

| 字段 | 含义 |
|---|---|
| `case_id` | 稳定短名 |
| `kind` | `decoder` 或 `engine` |
| `language_model` | `distinct` / `neutral` / `supplement_preference` |
| `beam_width` | Beam 宽度 |
| `emitted_character_reward` | 每字长度奖励 |
| `candidate_limit` | 可见候选上限 |
| `lexicon` | 码到词条列表，键按 UTF-16 码位排序，列表保持码表顺序 |
| `supplements` | `{text,weight}` 数组；无补充语料时为 `[]` |

### snapshot（decoder）

每次 append/backspace/reset 后一行。同时跑 `Decode` 与 `DecodeFull`，不一致则导出失败。

| 字段 | 含义 |
|---|---|
| `action` | `append` / `backspace` / `reset` |
| `key` | append 的字符；其他动作为 `""` |
| `raw` | 该步之后的完整 raw code |
| `decode.candidates[]` | 可见候选：`text`、`segmented_code`、`base_score`、`final_score`、`confidence_score`、`supplement_score`、`max_lexicon_rank`、`text_edges`、`raw_edges` |
| `decode.early_commit` | `proposal`、`raw_lengths`、`confidence_truncated`、`ignore_neural_constraint` |
| `full_matches_incremental` | 恒为 `true` |

分数使用 `G17` + `InvariantCulture`。`text_edges` / `raw_edges` 是从路径起点到每一段结束的累计长度。`raw_lengths` 的键按序排列。

### snapshot（engine）

在 `InputMethodEngine` 上执行 type/导航/上屏。用于锁候选遍历和提交时机，不替代 decoder 分数黄金集。

| 字段 | 含义 |
|---|---|
| `action` | `type` / `down` / `up` / `tab` / `shift_tab` / `space` / `enter` / `backspace` / `escape` / `semicolon` / `quote` |
| `key` | `type` 的字符串；其他动作为 `""` |
| `raw` | 引擎当前 input buffer |
| `selected_index` | 当前选中可见候选 |
| `commit_text` | 该键产生的上屏文本 |
| `input_code` / `active_input_code` | 显示编码（可含分段空格） |
| `candidates` | 可见候选文本 |

## 导出

```text
TigerClaw.Core.Tests.exe --sentence-golden-export [output.jsonl]
```

省略路径时写入 `next/TigerClaw.Core.Tests/golden/sentence_golden.v1.jsonl`。普通测试会重新导出到临时文件并与提交文件做字节比较。
