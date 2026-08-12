# BimeIPC 消息协议（当前实现）

## 概览

BimeTSF2、Dialog、Native Hook 与 TigerClaw Core 通过命名管道通信。

- 管道名: `\\.\pipe\BimeIPC`
- 编码: UTF-8
- 分隔: 每条 JSON 以换行 `\n` 结尾
- 方向:
  - 前端 -> Core: 请求/通知
  - Core -> 前端: 响应

## 通用约定

- `type` 为消息类型。
- `seq` 仅在“需要响应”的请求中使用，用于请求-响应配对。
- 布尔字段默认值由发送方显式给出，不要依赖隐式默认。
- 中英文状态以 TigerClaw Core 为准，通过 `response.keyboard_open` 向前端同步。
- 可选整句神经重排使用独立管道，见 `Protocol/sentence_messages.md`；它不改变 BimeIPC 的前端协议。

## 消息类型

### 1. `key`（TSF -> Core，需响应）

用途: 发送按键事件到 Core，由 Core 决定是否处理、是否上屏。

示例:

```json
{"type":"key","seq":101,"action":"down","vk":65,"scan":30,"shift":false,"ctrl":false,"alt":false,"win":false,"capsLock":false,"numLock":false,"repeat":1,"extended":false,"caret_x":800,"caret_y":500}
```

字段:

- `action`: `"down" | "up"`
- `vk`: 虚拟键码
- `scan`: 扫描码
- `shift`/`ctrl`/`alt`/`win`: 修饰键状态
- `capsLock`/`numLock`: 锁定键状态
- `repeat`: 重复次数
- `extended`: 是否扩展键
- `caret_x`/`caret_y`: 可选，按键时光标屏幕坐标

对应响应: `response`

### 2. `hello`（TSF -> Core，需响应）

用途: 建立连接后的协议/版本握手，便于定位“DLL 是否最新”“Core 是否匹配”。

示例:

```json
{"type":"hello","seq":2,"protocol_version":2}
```

对应响应: `response`（包含 `protocol_version`、`core_build`、`core_commit`、`core_branch`、`core_path`）

### 3. `ctrl_space`（TSF -> Core，需响应）

用途: 由 TSF 显式请求 Core 执行“中英切换”。

示例:

```json
{"type":"ctrl_space","seq":102}
```

对应响应: `response`

### 4. `show_menu`（TSF -> Core，需响应）

用途: 请求 Core 弹出状态窗上下文菜单（语言栏右键等）。

示例:

```json
{"type":"show_menu","seq":103}
```

对应响应: `response`

### 5. `focus`（TSF -> Core，通知）

用途: 通知当前焦点窗口信息。

示例:

```json
{"type":"focus","hwnd":123456,"processId":4321}
```

说明: 当前实现不要求响应。

### 6. `caret`（TSF -> Core，通知）

用途: 通知光标位置，供 Core 更新候选窗定位。

示例:

```json
{"type":"caret","x":100,"y":200,"width":2,"height":20}
```

说明: 当前实现不要求响应。

### 7. `ime_active`（TSF -> Core，通知）

用途: 通知本输入法是否处于激活态，供 Core 控制状态窗显隐。激活态 = 本 IME 是当前选中输入法（profile）且焦点落在可编辑文档上；两者任一不满足即未激活。

示例:

```json
{"type":"ime_active","active":true}
```

字段:

- `active`: `true`=已激活（状态窗显示），`false`=未激活（状态窗隐藏）

说明:
- 当前实现不要求响应。
- TSF 在 `ActiveLanguageProfileNotifySink::OnActivated`（输入法切换）和 `ThreadMgrEventSink::OnSetFocus`（焦点变化）时计算并上报；`Deactivate` 卸载前强制上报 `false`。
- Core 收到后将其折入 `OverlayUiState.HideStatusBar`（未激活时强制隐藏）。

### 8. `query_state`（TSF -> Core，需响应）

用途: TSF 在焦点切换等场景主动查询 Core 当前中英文状态，避免语言栏显示漂移。

示例:

```json
{"type":"query_state","seq":105}
```

对应响应: `response`（关注 `keyboard_open` 字段）

## 统一响应 `response`（Core -> TSF）

示例:

```json
{"type":"response","seq":101,"success":true,"handled":true,"commit_text":"你好","input_buffer":"nihao","keyboard_open":true}
```

字段:

- `success`: 请求处理是否成功
- `handled`: 当前按键/命令是否由 Core 接管
- `commit_text`: 需要前端代为上屏/回放的字符串（可为空或省略）
- `input_buffer`: 前端应显示的当前 composition 字符串（可选）。开启“中英文不限长混合输入”时，它是“已解析前缀 + 活动尾码”的表面显示串；已解析前缀中的有码段显示候选字词，无码段显示原始英文编码，因此不等同于 Core 保存的本轮完整原始编码。整句模式下，它会按照当前首选候选的切分路径插入显示空格；Core 保存的原始整句编码仍不含这些空格。
- `keyboard_open`: Core 当前中英状态（可选，`true`=中文，`false`=英文）
- `protocol_version`: 协议版本（`hello` 响应可选）
- `core_build`: Core 构建标识（`hello` 响应可选）
- `core_commit`: Core 提交短哈希（`hello` 响应可选）
- `core_branch`: Core 分支名（`hello` 响应可选）
- `core_path`: Core 进程路径（`hello` 响应可选）
- `cancel_composition`: 要求前端先取消旧 composition（可选）。该字段可与 `handled:true` 和新的 `input_buffer` 同时出现，此时应先取消旧串，再应用本次响应。

说明:

- 对 `key` 消息，TSF 以 `handled` 决定是否吞键。
- TSF 不应自行切换中英文本地状态；语言栏状态同步只使用 `keyboard_open`。
- 编码伪装只作用于 `input_buffer` 中的活动尾码；大写尾码按对应小写字母的位置伪装。已解析前缀中的候选字词和无码英文段均不伪装，原始大小写保持不变。

## 错误响应 `error`（Core -> TSF）

示例:

```json
{"type":"error","code":3000,"message":"Unknown command: xxx"}
```

常见错误码:

- `1000`: 内部异常
- `1001`: JSON 格式错误
- `3000`: 未知消息类型
