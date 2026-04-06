# Bridge Entry Points（BimeTSF2 <-> BimeCore）

## TSF 侧入口（BimeTSF2）

- `CSampleIME::ActivateEx`
  - 初始化 key sink / 语言栏 / caret sink / pipe client。
- `CSampleIME::OnSetFocus`（ThreadMgrEventSink）
  - 触发 `focus` 上报与 `query_state` 同步。
- `CSampleIME::OnTestKeyDown` / `OnKeyDown`
  - 构造 `key(action=down)`，等待 Core `response`。
- `CSampleIME::OnTestKeyUp` / `OnKeyUp`
  - 构造 `key(action=up)`，等待 Core `response`。
- caret 相关 sink（layout/text-edit/composition anchor）
  - 触发 `caret` 上报（带 12ms 合并窗口）。

## Core 侧入口（BimeCore）

- `PipeServer.MessageReceived`
  - 原始 JSON 入站。
- `MessageHandler.HandleMessage`
  - 投递到 `InputEngine` 单线程串行处理。
- `MessageHandler.HandleMessageCoreAsync`
  - 分发 `key/caret/focus/query_state/ctrl_space/show_menu/hello`。
- `MainWindow.ProcessKeyFromTSF`
  - 核心按键处理、候选与上屏决策。
- `WinCandidate.UpdatePosition`
  - caret/focus 驱动候选窗定位更新。

## 同步边界

- TSF -> Core 的 `key` 是同步请求-响应（决定 `handled` 与上屏）。
- `focus/caret` 是异步通知。
- Core 内部 UI 调用统一使用 Dispatcher 投递（`PostOnUi` / `InvokeOnUiAsync`）。
