# 虎爪整句 Fcitx5 Android 移植状态

更新日期：2026-09-05

TigerClaw：`/mnt/c/users/yc/desktop/bime_codex_src_20260513`

Fcitx5 Android：`/home/yc/fx5/fcitx5-android`，插件 `tigerclaw`

## 当前结论

独立插件和平台无关 C++ 核心已经落地，不依赖 Rime。真机可以输入，自动提前上屏
默认开启，补充语料在输入法激活时重载；选重后缀关闭时，标点会提交当前候选并输出
中文标点。Windows C# Core 的自动提前上屏仍默认关闭。

2026-09-05 已同步单字重码组句、完整码白名单、提前上屏/空码提交统一开关、
保留编码数量和长输入有界 Beam。主机测试、真实模型回归、性能基准和插件 Debug
构建已通过。现在的重点是完成真机性能、长期稳定性、Release 签名和分发验收。
Fcitx5 仓库的实际构建环境与最新状态以其 `HANDOFF.md` 为准。

## 范围

目标是一个独立的 Fcitx5 Android“虎整句”插件：

- 插件适配层负责 Fcitx 按键、preedit、候选、提交和配置；
- `tigerclaw_sentence_core` 是不依赖 Fcitx/JNI/UI 的纯 C++ 解码核心；
- 使用 TigerClaw 码表、TCSKNM02 模型、补充语料和同一套评分规则；
- 大模型不进入 Git；Qwen、Windows TSF、Overlay、Dialog 和普通虎码不在本移植范围。

```text
Fcitx5 Android
  -> plugin/tigerclaw adapter
  -> tigerclaw_sentence_core
  -> sentence-ngram-mobile.bin (TCSKNM02)
```

TigerClaw 仓库负责规范、导出器和黄金数据；Fcitx5 Android 仓库负责插件源码、
Android 集成和 APK。行为冲突时以 Windows C# Core、`AGENTS.md` 和自动测试为准。

## 规范来源

| 内容 | 位置 |
|---|---|
| 格图、Beam、候选和置信度 | `next/TigerClaw.Core/SentenceInputDecoder.cs` |
| KN 模型和缓存 | `SentenceNgramModel.cs` |
| 孤立惩罚、补充语料 | `SentenceIsolationPenalty.cs`、`SentenceSupplementModel.cs` |
| 按键和提前上屏状态机 | `InputMethodEngine.cs` |
| 自动测试/黄金导出 | `next/TigerClaw.Core.Tests/` |
| 移动模型转换 | `tools/convert_sentence_ngram_mobile.py` |
| Rime 移动端参考 | `rime/tiger_sentence/` |
| 冻结身份和模型 hash | `docs/FCITX5_ANDROID_BASELINE.md` |
| JSONL 格式 | `docs/sentence_golden_v1.md` |

## 必须保持的行为

- 完整 raw code 是权威状态；候选分段空格只用于显示。
- 一码段只在整段输入只有一码时合法。其他段至少消耗两键；`;`、`'`、数字为
  显式选重。整段不超过四码时允许全部名次，但首选路径必须领先。
- Beam 每输出一个 Unicode 字符奖励 `2.0`；使用完整 Modified Kneser-Ney V2，
  EOS 只在最终候选计算。
- 未分割的整段输入若是单字的最短可用编码（等长按码表来源顺序），该单字获得
  仅影响排序的 `5.0` 补偿；不要求同码首位，显式选重不加且不进入置信度。
- 生僻字孤立惩罚、补充语料奖励和确定性 Top-K 必须与 C# 一致。同文本不同路径
  合并置信度质量，缓存必须有上限。
- 追加只扩展跨越旧 raw 末端的新边；退格复用有效前缀。旧路径不能重复计分。
- 每个 `InputContext` 独立保存 composition、候选、增量格图、generation 和提前
  上屏证据；模型和只读码表可共享。
- 提前上屏按 `(文本前缀, raw 边界)` 独立累计。完整码路径只聚合已经收窄的可见
  候选；不完整尾码只把已经算好的 `states[consumedLength]` 格图并进证据池，
  不得把完整码路径重新扩到整个 Beam。边界是否封闭看该 raw 位置上可见/并池
  候选的质量加权占比是否达到 `0.99999`。两代都达到 `0.99999` 时两代即可确认，
  否则仍要求三代。低置信完整代和没有合格前缀的掉尾并池只对照、不加代数，
  最多跨三代。回退、缺代或手动遍历会失效/暂停证据，提交后必须保留完整不稳定
  尾部，且至少留下三个已完成解码的 raw 编码。
- 2026-09-01 的独立前缀累计、掉尾并池比较和加权边界封闭已同步到 Android
  `tigerclaw_sentence_core`。Qwen 短候选动态权重不适用，因为本移植没有神经重排。
- 完整候选可以进入提前上屏置信度，但概率提交边界至少保留三个已完成解码的 raw
  编码。`保留最少编码数量` 可提高该下限，也约束空码提交。空码提交属于同一个
  提前上屏开关；唯一候选后追加字母形成空码时，先确认尾段不能继续补成有效编码，
  再提交原候选并把新增编码全部留在下一段 composition。
- `允许单字重码组句` 默认开启。未显式选重时，分段路径可以让非首选单字按语言
  模型分数竞争；非首选多字词仍需显式选重，单边消费整个输入时仍保持首选在前。
  高频字最优码限制和完整码白名单仍然生效。概率提前提交保留已参与竞争的路径；
  空码提交后的续写保留解码器允许的重码单字路径；唯一性及强置信判断也计入合法的非首选单字。
- C++ 解码器在 raw 位置 24 之后把 Beam 从 2000 收窄为 48。候选密集主机基准中，
  40 码 P95 在提前上屏关闭时约 2.4--2.8 ms，开启时约 7.8--13.1 ms。
- Ctrl+数字留给目标应用。Space 提交候选，Enter 提交原始编码，Esc 清空，
  Up/Down 与 Tab/Shift+Tab 遍历候选。2026-09-10 对齐 Windows 的 Tab 继续输入锁定：
  Tab 仅高亮，下一字母固定文本和编码边界；开启提前上屏时直接提交已确认部分，否则回删
  至边界可解锁。选重键仍修改当前段，Up/Down 不单独触发锁定，模型上下文保留。

更细的算法行为由 C# 测试和 golden snapshot 固定，不在本文件重复维护。

## 数据和分发

移动模型位于仓库外：

```text
/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-ngram-mobile.bin
```

它是无损重排的 TCSKNM02，保留原始 float32 概率。构建必须校验 magic、版本、
大小和 SHA-256；模型缺失或损坏时要明确报错，不能静默打包空文件。

正式发布应避免每次插件升级重复复制大模型。优先让 APK 只带程序和小型数据，模型
由用户导入或单独下载到 Fcitx `StandardPath` 可定位的用户目录。补充语料也放在
用户目录，升级不得覆盖。

## 已完成

- 独立插件 APK、addon/input-method 注册和每上下文状态。
- TCSKNM02 读取、码表、合法切分、KN、孤立惩罚、补充语料和增量 Beam。
- C# JSONL golden、纯 C++ 测试和真实模型一致性链路。
- 候选/preedit/提交、选重、退格、标点和配置接入。
- 自动提前上屏和补充语料激活重载。
- 单字重码组句、完整码白名单和长输入性能基准。
- 真机基本输入链路。

## 剩余验收

- 在目标 Snapdragon 8 Gen 2/SM8550 设备记录冷启动、热态 append 的 P50/P95/P99、
  退格、长句、高歧义输入和实际 RSS/page fault。
- 连续输入至少 10 分钟，确认有界缓存不增长，无崩溃、漏键、重复提交或明显闪烁。
- 覆盖横竖屏、候选点击、应用切换、`InputContext` 销毁和迟到结果。
- 验证模型缺失、截断、版本错误、坏码表和坏配置均可恢复且不拖垮 Fcitx。
- 确认升级保留用户模型、配置和补充语料。
- 完成许可证、兼容版本、稳定签名、模型分发方式和可复现的干净安装流程。

建议性能目标：热态 append P50 不高于 8 ms、P95 不高于 20 ms，正常样例不连续
阻塞 50 ms 以上。是否需要异步解码必须由真机数据决定；引入时只能接受最新
generation，并从 Fcitx 事件循环更新仍存活的上下文。

## 构建与完成标准

```bash
cd /home/yc/fx5/fcitx5-android
./gradlew :plugin:tigerclaw:assembleDebug
./gradlew :plugin:tigerclaw:assembleRelease
```

实际环境变量、JDK/NDK 和 QEMU 配置以目标仓库 `HANDOFF.md` 为准。不要使用 Box64，
不要提交模型、APK、Gradle/CMake 缓存、性能大日志或签名秘密。

只有纯 C++ 测试和黄金一致性通过、真机长期输入稳定、性能达到目标或存在经确认的
例外、安装升级可复现且签名发布完成后，才可宣布移植完成。Qwen 未包含，不能宣称
与 Windows 神经重排完全等价。
