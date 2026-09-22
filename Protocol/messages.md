# BimeIPC 消息协议

实现入口：`next/TigerClaw.Core/ProtocolHandler.cs`。修改协议时必须同步 TSF、Dialog
或 Native Hook 调用方。

## 传输

Overlay 的共享内存发布协议见 [ui_state.md](ui_state.md)，独立于此命名管道。

- 命名管道：`\\.\pipe\BimeIPC`
- UTF-8 JSON，每行一个对象
- 需要响应的请求携带 `seq`；响应使用相同 `seq`
- 通知不返回内容
- Core 是中英文状态、composition 和候选的唯一权威

TSF 与 Native Hook 的 `key` 请求可携带 `client_session` + `event_id`。同一次物理按键超时重试必须
复用这两个值；Core 返回首次缓存响应，不再次执行按键。

## 消息清单

`show_menu` 不增加 JSON 字段。TSF 在发送前通过连接的管道取得 Core PID，
调用 `AllowSetForegroundWindow` 传递本次点击的前台权限；Core 在触发菜单事件前
只向路径匹配的同套 Overlay 传递权限，不使用 `ASFW_ANY`。
Native Overlay 尝试取得前台，但菜单显示不以授权成功为前提；菜单存续期间
使用独立的外部点击／Esc 检测补齐后台菜单的关闭行为，关闭后停止检测。
所有菜单使用独立的临时透明宿主，避免状态更新隐藏浮窗时中断菜单。
收到菜单事件后立即显示，不等待 `get_schema_list`；方案列表后台刷新，
展开方案子菜单时固定本次显示列表与命令 ID 的对应关系。

| 类型 | 主要调用方 | 响应 | 用途 |
|---|---|---:|---|
| `hello` | TSF/Hook | 是 | 协议、构建和前端配置握手 |
| `query_state` | TSF/Hook | 是 | 查询中英状态、composition 和版本 |
| `key` | TSF/Hook | 是 | 物理按键 |
| `ctrl_space` | TSF | 是 | 语言栏触发中英切换 |
| `show_menu` | TSF/Overlay | 是 | 打开状态菜单 |
| `show_config` | Overlay | 是 | 打开设置 |
| `show_addci` | Overlay/快捷键 | 是 | 打开加词 |
| `reload_config` | Dialog | 是 | 重载配置、码表和相关资源 |
| `reload_mb` | Dialog/Overlay | 是 | 重载当前码表快照 |
| `get_config` / `set_config` | Dialog | 是 | 读取或修改配置 |
| `get_schema_list` | Dialog | 是 | 方案列表和当前方案 |
| `get_selection_key_config` / `set_selection_key_config` | Dialog | 是 | 自定义选重键 |
| `add_ci` / `construct_ci` / `get_last_ci` | Dialog | 是 | 加词、构词、历史文本 |
| `get_send_history_count` | Dialog | 是 | 已发送文本元素计数 |
| `open_mb_folder` / `export_mb` | Dialog/Overlay | 是 | 打开或导出码表 |
| `open_official` | Dialog/Overlay | 是 | 打开虎爪 GitHub 开源仓库 |
| `exit_core` | Overlay | 是 | 回复后退出 Core |
| `focus` / `caret` / `ime_active` | TSF | 否 | 窗口、光标和激活状态 |
| `composition_canceled` | TSF/Hook | 否 | 前端已取消 composition |
| `learning_commit` | TSF | 否 | 整句 Tab 改选的文档上屏结果回执 |
| `hook_native_disabled` | Hook | 否 | Native Hook 禁用状态 |

未知消息返回 `success:false` 的普通 `response`，不使用独立错误消息类型。

## `key`

```json
{"type":"key","seq":101,"client_session":"1234-1","event_id":"42","frontend":"tsf","action":"down","vk":65,"scan":30,"shift":false,"ctrl":false,"alt":false,"win":false,"capsLock":false,"numLock":false,"repeat":1,"extended":false,"caret_x":800,"caret_y":500,"width":2,"height":20}
```

字段：

- `action`：`down` / `up`（兼容 `key_down`）。
- `vk`、`scan`：虚拟键和扫描码；扫描码也兼容 `scan_code`。
- `shift`、`ctrl`、`alt`、`win`：修饰键。
- `capsLock`、`numLock`：锁定状态；也兼容 snake_case 名称。
- `repeat`、`extended`：重复次数和扩展键。
- `caret_x/y/width/height`：可选的按键时新鲜光标位置。
- `frontend`：`tsf`、Hook Native 标识或省略。
- `client_session/event_id`：可选幂等身份。
- `learning_ack_version`：可选；`1` 表示前端支持文档上屏结果回执。缺失或其它版本不产生学习回执。

Core 以 `handled` 决定前端是否吞键，以 `commit_text` 要求前端上屏。

## 通知

```json
{"type":"caret","x":100,"y":200,"width":2,"height":20,"frontend":"tsf"}
{"type":"focus","hwnd":123456,"processId":4321,"processName":"app.exe","className":"Class","windowTitle":"Title","frontend":"tsf"}
{"type":"ime_active","active":true}
{"type":"composition_canceled","frontend":"tsf"}
{"type":"hook_native_disabled","disabled":true}
```

焦点变化会清理按键/chord 临时状态。`ime_active` 只表示 TigerClaw profile 当前激活，
用于状态窗显隐；它不替代 Core 的 `keyboard_open` 中英状态。

### 整句自学习回执

```json
{"type":"learning_commit","client_session":"1234-1","learning_receipt":"0123456789abcdef0123456789abcdef","applied":true}
```

Core 仅在支持回执的按键响应中携带可选 `learning_receipt`（32 个小写十六进制字符）。
生成响应不写入学习记录；TSF 同步提交返回 `S_OK` 后才发送 `applied:true`，失败则发送
`false`。`S_FALSE` 或仅调度异步编辑不代表上屏成功。通知没有响应，避免污染按键回复流。
令牌绑定 `client_session`，有效期 30 秒，最多保留 128 个；成功或失败确认均只消费一次。
按键重试沿用缓存的同一令牌。焦点、外部 composition 取消和配置版本变化会作废待确认令牌；
关闭自学习时不接受确认。旧前端和未实现回执的 Native Hook 仍可正常输入，但不会学习。

## 统一响应

```json
{"type":"response","seq":101,"success":true,"handled":true,"commit_text":"你好","input_buffer":"ni hao","keyboard_open":true,"cancel_composition":false,"expect_keyup":false}
```

通用字段：

- `success`：请求本身是否成功。
- `handled`：按键/命令是否由 Core 接管。
- `commit_text`：前端应提交的文本，可省略或为空。
- `input_buffer`：对外 composition。混合输入时是解析前缀加活动尾码；整句时可含
  仅用于显示的分段空格，不等于权威 raw code。
- `keyboard_open`：`true` 中文、`false` 英文。
- `cancel_composition`：先取消前端旧 composition，再应用本次状态。
- `composition_tracking`：当前是否为需异步刷新分段的整句 composition。
- `composition_pending`：当前 raw 的本地解码是否仍在后台运行。
- `expect_keyup`：仅按下事件响应携带。`true` 表示 Core 的当前按键状态还需要
  对应物理键的释放事件；`false` 表示 TSF 可直接放行该 KeyUp 而不访问 Core。
  前端遇到旧 Core 未返回该字段或按下请求失败时必须按 `true` 处理。
  Windows TSF 还必须始终转发 Space 和引号键的孤立 KeyUp：系统保留快捷键或宿主
  可能不提供对应 KeyDown，Core 用这两个释放事件完成 Ctrl+Space 和引号回退。
  当前 TSF 不做严格 Down/Up 配对：修饰键或一次性动作开启20个 KeyUp 的宽限窗口，
  窗口内释放事件全部转发并逐次递减；修饰键释放会刷新窗口。Space和引号始终
  转发但不主动刷新窗口。这样可以容忍宿主丢失、拆分或重排键盘回调。
- `config_version`、`lexicon_version`：对应快照版本，部分响应携带。
- `error`：失败原因，部分写操作携带。

`hello` 还可返回 `protocol_version`（当前为 2）、`core_build`、`core_commit`、
`core_branch`、`core_path` 和 Hook Native 所需配置。`query_state` 对 TSF 保持轻量，
候选列表由共享内存提供给 Overlay，不通过 TSF 响应传输。

编码伪装只修改对外活动尾码，不能改变 Core 保存的 raw、候选查找或提交文本。

## Dialog 数据字段

- `get_config`：返回 `config_text`、`config_version`。
- `set_config`：请求 `key`、`value`；返回 `changed`、配置/码表版本或 `error`。
- `get_schema_list`：返回换行分隔的 `schema_list` 和 `current_schema`。
- `get_selection_key_config`：返回 `config_text`、`default_text`、`config_path`。
- `set_selection_key_config`：请求 `config_text`；失败保留原绑定并返回 `error`。
- `construct_ci`：请求 `text`，返回 `code`。
- `get_last_ci`：请求非负 `history_len`，返回 `text`。
- `add_ci`：请求 `code`、`text`。
- `open_mb_folder`：成功返回 `path`。
- `export_mb`：失败时返回 `error`。

自定义选重键支持十进制、`0x` 十六进制和 `VK_*` 名称；空绑定行用于清除该选位
默认键。


### Full-pinyin v1 additions (optional)

Key/query responses may include `input_cursor` (UTF-16 offset in `input_buffer`, -1 means legacy end placement). Updated TSF caches it with the response and applies it to initial and existing preedit ranges. Other schemes keep -1.

Native Overlay publishes a full-pinyin-only `CandidateSelectionToken`. A click uses a fixed 33-byte `WM_COPYDATA` payload (dwData `0x54435059`, one action/index byte and 32 ASCII lowercase hex token bytes) to the foreground thread's `TigerClaw.CaretCoalesceWindow`. TSF validates the payload and foreground/protected context, then enqueues it in its existing FIFO retry queue. No keyboard input is injected. The pipe request is a `key` message with `vk: 0`, `scan: <action/index>`, `candidate_token`, stable `client_session/event_id`, `action: down`, and `learning_ack_version: 1`. Core rejects stale menu tokens; successful selections use the same replay cache and successful-insertion learning receipt as physical keys. No physical keyup is expected. Focus/clear invalidates tokens and queued events.

`full_pinyin_info` returns `full_pinyin: bool`. Existing `add_ci` dispatches to the independent user-word store when this engine is active; `code` contains one untoned pinyin syllable per Unicode character, separated by spaces/apostrophes. `delete_pinyin_word` accepts the same `text`/`code` pair. These operations never edit shape-code or temporary reverse-lookup tables.


Pinyin candidate action/index byte: `0..9` select, `16..25` pin, `32..41` unpin, `48..57` forget learned corrections. The low four bits are the page index; other values are invalid. Management never submits application text. All actions preserve the immutable candidate token and physical-event replay identity. The native candidate context menu retains application focus; stale tokens are rejected after a decode, edit, menu change or focus change.

`pinyin_preferences` returns an `items` string, one line per record: `kind<TAB>code<TAB>base64(UTF-8 text)`. Kinds: phrase, pin, word, learned. `pinyin_manage` accepts `action` (phrase_add, phrase_delete, pin, unpin, forget), `code`, `text`. Phrase/pin updates are atomic and idempotent. Forget queues durable TCL1 undo records, then asynchronously refreshes active candidates; its immediate success acknowledges queue acceptance. These requests use the current pinyin scheme. The UI does not delete entire journals.
