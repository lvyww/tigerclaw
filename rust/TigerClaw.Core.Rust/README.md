# TigerClaw Core Rust（平行实验）

该目录是 C# `TigerClaw.Core` 的独立 Rust 移植实验，不替换、不引用且不修改现有
C# 工程。Windows 实验管道固定为 `\\.\pipe\BimeIPC.Rust`，不会与正式 Core 的
`\\.\pipe\BimeIPC` 冲突。

当前阶段包含：

- UTF-8、每行一个 JSON 的协议状态机。
- `hello`、`query_state`、`ctrl_space`、`caret`、`ime_active` 和基础 `key` 骨架。
- Windows 多客户端命名管道服务。
- WSL/Linux `--stdio` 协议测试入口。

当前 `key` 已实现字母缓冲、退格、Esc、回车原码提交，以及有码表时的空格首选/数字选重、分号二选、单引号三选、方向键和 Tab 候选选择；不代表已达到 C# Core 的完整候选、混输或整句行为。正式切换到原管道名前必须先通过逐消息差分测试。

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

当前已完成：基础按键状态、候选码表只读加载、空格/数字/分号/引号选重、方向键/Tab候选选择、独立配置读取与热加载、混合输入基础状态机、整句基础切分候选、只读 n-gram 模型评分、`client_session + event_id` 幂等重放保护，以及 x64/x86 GNU 交叉编译。Qwen 排序、完整码表规则和 TSF 真机差分仍未移植。

协议实验还支持 `reload_mb`（可选 `path` 字段）热重载码表，以及 `hello` 中的
`lexicon_loaded`、`sentence_model_loaded` 状态标志。`show_menu`、`show_config`、
`show_addci` 在 Rust 实验中返回成功响应，但不会启动 WPF 界面。

另支持 `get_config` 查询实验配置、`set_config` 以 JSON 对象更新部分配置并清空当前组合。
Windows 构建已支持按需调用现有 `TigerClaw.Sentence.exe`，发送前五个候选并按
`n-gram + 0.84 * Qwen` 合并排序；sidecar、模型、管道或响应失败时回退到 n-gram
顺序。该调用目前是实验路径，正式接入 TSF 前仍需增加后台线程和超时隔离验证。
