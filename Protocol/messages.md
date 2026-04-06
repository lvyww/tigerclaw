# BimeIPC 消息协议（当前实现）

## 概览

BimeTSF/BimeTSF2 与 BimeCore 通过命名管道通信。

- 管道名: `\\.\pipe\BimeIPC`
- 编码: UTF-8
- 分隔: 每条 JSON 以换行 `\n` 结尾
- 方向:
  - TSF -> Core: 请求/通知
  - Core -> TSF: 响应

## 通用约定

- `type` 为消息类型。
- `seq` 仅在“需要响应”的请求中使用，用于请求-响应配对。
- 布尔字段默认值由发送方显式给出，不要依赖隐式默认。
- 中英文状态以 BimeCore 为准，通过 `response.keyboard_open` 向 TSF 同步。

## 消息类型

### 1. `key`（TSF -> Core，需响应）

用途: 发送按键事件到 BimeCore，由 Core 决定是否处理、是否上屏。

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

### 7. `connect`（TSF -> Core，需响应）

用途: 握手/连通性确认。

示例:

```json
{"type":"connect","seq":1}
```

对应响应: `response`

### 8. `ping` / `pong`

- 请求: `{"type":"ping"}`
- 响应: `{"type":"pong"}`

### 9. `keyboard_open_close`（兼容保留，需响应）

用途: 直接设定 Core 中英开关状态。

示例:

```json
{"type":"keyboard_open_close","seq":104,"open":true}
```

对应响应: `response`

### 10. `query_state`（TSF -> Core，需响应）

用途: TSF 在焦点切换等场景主动查询 Core 当前中英文状态，避免语言栏显示漂移。

示例:

```json
{"type":"query_state","seq":105}
```

对应响应: `response`（关注 `keyboard_open` 字段）

## 统一响应 `response`（Core -> TSF）

示例:

```json
{"type":"response","seq":101,"success":true,"handled":true,"text_to_output":"你好","input_buffer":"nihao","is_composing":true,"keyboard_open":true}
```

字段:

- `success`: 请求处理是否成功
- `handled`: 当前按键/命令是否由 Core 接管
- `text_to_output`: 需要 TSF 代为上屏的字符串（可为空或省略）
- `input_buffer`: 当前输入串（可选）
- `is_composing`: 是否处于组词态（可选）
- `keyboard_open`: Core 当前中英状态（可选，`true`=中文，`false`=英文）
- `protocol_version`: 协议版本（`hello` 响应可选）
- `core_build`: Core 构建标识（`hello` 响应可选）
- `core_commit`: Core 提交短哈希（`hello` 响应可选）
- `core_branch`: Core 分支名（`hello` 响应可选）
- `core_path`: Core 进程路径（`hello` 响应可选）

说明:

- 对 `key` 消息，TSF 以 `handled` 决定是否吞键。
- TSF 不应自行切换中英文本地状态；语言栏状态同步只使用 `keyboard_open`。

## 错误响应 `error`（Core -> TSF）

示例:

```json
{"type":"error","code":3000,"message":"Unknown command: xxx"}
```

常见错误码:

- `1000`: 内部异常
- `1001`: JSON 格式错误
- `3000`: 未知消息类型
