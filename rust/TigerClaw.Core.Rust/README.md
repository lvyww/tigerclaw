# TigerClaw Core Rust（平行实验）

该目录是 C# `TigerClaw.Core` 的独立 Rust 移植实验，不替换、不引用且不修改现有
C# 工程。Windows 受控替换测试使用生产管道 `\\.\pipe\BimeIPC`，运行前必须先
停止 C# Core；它仍是替换候选，不能隐式覆盖默认 Core。

当前阶段包含：

- UTF-8、每行一个 JSON 的协议状态机。
- `hello`、`query_state`、`ctrl_space`、`caret`、`ime_active` 和基础 `key` 骨架。
- Windows 多客户端消息模式命名管道服务，与 TSF 的
  `PIPE_READMODE_MESSAGE` 连接要求兼容。
- Windows GUI 子系统、Core 单实例互斥锁和启动时即时心跳。
- WSL/Linux `--stdio` 协议测试入口。

当前 `key` 已实现字母缓冲、退格、Esc、回车原码提交，以及有码表时的空格首选/数字选重、分号二选、单引号三选、方向键和 Tab 候选选择。中英文标点、Shift 标点、智能引号、顿号开关、数字后半角句点、中英切换原码提交和快捷键穿透取消组合已与 C# 对齐。整句已改为合法格图 Beam Search 与 TCSKNM01 V2 KN 打分，并已对齐孤立惩罚、补充语料、分段显示、数字/分号进编码、提前上屏、增量格图、KN 定长缓存、按键路径异步 Beam 和代次校验的 Qwen；提前上屏后的空格或标点只提交未上屏尾部。仍缺少与 C# 的金集逐条差分。正式切换到生产 Core 前必须先通过 Windows 逐消息差分测试。

## WSL 构建与测试

```bash
cd rust/TigerClaw.Core.Rust
cargo test
cargo build --release --target x86_64-pc-windows-gnu
cargo build --release --target i686-pc-windows-gnu
printf '%s\n' '{"type":"hello","seq":1}' | cargo run -- --stdio
# 可选：加载独立实验码表（不会读取正式 config.txt）
printf '%s\n' '{"type":"key","seq":1,"action":"down","vk":65}' \
  | cargo run -- --stdio --lexicon /path/to/lexicon.txt
# 可选：加载独立实验配置
cargo run -- --stdio --lexicon /path/to/lexicon.txt --config /path/to/config.txt
# 可选：加载只读整句 n-gram 模型（当前读取 TCSKNM01 文件格式）
cargo run -- --stdio --lexicon /path/to/lexicon.txt --config /path/to/config.txt --ngram /path/to/sentence-ngram-v2.bin
# Windows 可选：复用现有 Qwen sidecar（失败自动保留 n-gram 顺序）
cargo run -- --stdio --lexicon /path/to/lexicon.txt --config /path/to/config.txt \
  --ngram /path/to/sentence-ngram-v2.bin \
  --sentence-exe /path/to/TigerClaw.Sentence.exe \
  --qwen-model /path/to/sentence-qwen-q8.gguf
```

## Windows MSVC 构建

在 Visual Studio Developer PowerShell 中安装 Rust MSVC 工具链后运行：

```powershell
rustup default stable-x86_64-pc-windows-msvc
cargo test
cargo build --release --target x86_64-pc-windows-msvc
```

产物为 `target\x86_64-pc-windows-msvc\release\tigerclaw_core_rust.exe`；如需发布命名，可在复制时重命名为 `TigerClaw.Core.Rust.exe`。

当前已完成：基础按键状态、候选码表目录加载、空格/数字/分号/引号选重、方向键/Tab候选选择、自定义选重键文件与 Dialog 读写协议、完整配置键值与方案切换、编码伪装、Shift/Ctrl+空格中英切换、混合输入、整句 Beam/KN/异步解码、Overlay 主题字体与 composition 状态字段、只读 n-gram 评分、`client_session + event_id` 幂等重放，以及 x64/x86 GNU 交叉编译。注释/拆分、拼音反查，以及 TSF 真机差分仍未完成。

协议实验还支持 `reload_mb`（可选 `path` 字段）热重载码表，以及 `hello` 中的
`lexicon_loaded`、`sentence_model_loaded` 状态标志。`show_menu`、`show_config`、
`show_addci` 在 Windows Rust Core 中启动现有 WPF 界面。

另支持 `get_config` 查询实验配置、`set_config` 更新配置并按需重载码表/取消当前组合。
Windows 构建已支持按需调用现有 `TigerClaw.Sentence.exe`，发送前五个候选并按
`n-gram + 0.84 * Qwen` 合并排序；sidecar、模型、管道或响应失败时回退到 n-gram
顺序。调用位于后台线程且有代次校验，但连接/读取超时、latest-only 请求合并、
`raw_code` 校验以及提前上屏证据约束仍待与 C# 对齐；完整实机矩阵仍需继续验证。
