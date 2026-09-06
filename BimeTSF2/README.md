# BimeTSF2

当前项目交接入口是根目录 `AGENTS.md`，文档索引见 `../docs/README.md`。

本目录是 TigerClaw 当前 TSF DLL 构建来源。当前实现为 bridge-only：TSF 侧捕获 key/focus/caret/IME activation 等事件，通过 `\\.\pipe\BimeIPC` 转发给 TigerClaw Core，并把 Core 响应镜像回 TSF。

活动项目文件：

- `SampleIME/BimeTSF2.vcxproj`
- `SampleIME/BimeTSF2.vcxproj.filters`

当前桥接说明见 `SampleIME/BRIDGE_ONLY_NOTES.md`。

旧 SampleIME 文档和未参与当前桥接路径的 legacy source/header 已删除。需要查阅上游或历史实现时，使用 `../reference/SampleIME/`。
