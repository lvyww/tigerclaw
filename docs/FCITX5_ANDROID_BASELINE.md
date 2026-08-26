# Fcitx5 Android 移植基线

记录日期：2026-08-27。  
本文件冻结阶段 0 开始时的仓库与模型身份。大模型不进 Git。

## 仓库

| 仓库 | 分支 | Commit |
|---|---|---|
| TigerClaw `bime_codex_src_20260513` | `main` | `59062a6f0142b50d54ec13b0bd30cfdcad0e2c86` |
| TigerClaw `third_party/llama.cpp` | `master` | `9b05354ec6fb58b4e665e9a39ebc40285c015638` |
| Fcitx5 Android `/home/yc/fx5/fcitx5-android` | `master` | `6998502528cd26efb6079556da92003638e4179c` |
| Fcitx5 Android `lib/fcitx5` | | `442edbc9b1880c766ff9cc9a9ac67e864b200595` |
| Fcitx5 Android `lib/fcitx5-lua` | | `05db9ee519d448a64ccbe216044e8e0342e8c536` |
| Fcitx5 Android `lib/libime` | | `65003b6020623affcdbda14ce9292f408fb7a3ad` |
| Fcitx5 Android `lib/fcitx5-chinese-addons` | | `0d3fd0408d03abc63cdb700559f85a387b98735f` |

开始阶段 0 时 TigerClaw 工作树已有无关本地修改，不得回退。Fcitx5 Android 工作树仅有未跟踪的 `HANDOFF.md`。

## 模型（均在仓库外）

路径前缀：`/mnt/c/Archive/tigerclaw_sentence_ml/runtime/`

| 文件 | 用途 | 大小（字节） | SHA-256 |
|---|---|---|---|
| `sentence-ngram-mobile.bin` | 移动端 TCSKNM02，C++ 核心读取 | 224475584 | `e3953f8f1526b871eb81fe887a8ae4a6edf79edffd33e388b95e2915adadb2d3` |
| `sentence-ngram-v2.bin` | Windows C# Core KN V2，黄金数据规范源 | 238789568 | `6485240eb1a6acba3aa54ef4fdbb1f34c5d4b70a1caaff20aa2f296d5aca8b52` |

阶段 0 提交的 JSONL 黄金集使用 Core.Tests 内存词表和确定性测试语言模型，不读取上述文件。真实 TCSKNM02 一致性黄金数据在阶段 3 用同一 C# 版本从 `sentence-ngram-v2.bin` 导出。

## 构建环境（Fcitx5 Android）

以 `/home/yc/fx5/fcitx5-android/HANDOFF.md` 为准：Fedora 44 ARM64 WSL2、JDK 25、NDK 28.0.13004108、QEMU user-static、`QEMU_LD_PREFIX=$HOME/.local/share/fx5-x86-root`。不要使用 Box64。
