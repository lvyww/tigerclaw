# 虎爪全拼内测版

> 2026-09-22 16:51：用户已要求解除全拼内测安装，改用现有 `release_arm64` 主线虎整句。原 r3 目录和学习数据保留，以下安装记录作为历史证据；当前注册和运行状态见 [虎整句五阶文档](SHAPE_FIVEGRAM.md)。

2026-09-21 实现于现有 C# Core。新增方案，不新增另一套 Core，不恢复暂停的 C++ Core。使用字音联合 KenLM trigram + Beam 200，不调用 Qwen/LLM。现阶段为内测构建，尚未完成真实应用与一周试用验收。

本机安装记录（2026-09-21）：用户卸载原 ARM64 版后，已按其明确要求将 r3 安装到 `C:\Users\yc\Desktop\虎爪全拼内测-r3`。TSF 注册及安装 DLL 哈希已核对，Core 与原生 Overlay 正常运行，实时 IPC 确认当前方案为“虎爪全拼”。按 Win+空格切换到虎爪即可开始试用；运行期间保留该目录，配置、用户词与学习记录保存在其中。原 `release_arm64/` 未被覆盖。安装检查记录位于 `next/_run/FullPinyin/install-r3/`，不代表应用输入验收已经完成。

## 方案与输入方式

内测包提供“虎爪全拼”和“虎爪小鹤双拼”两个独立方案，共用相同冻结中文词典、字音模型和解码实现，各自保存用户词、短语与学习记录。方案由 `schema.json` 的 `engine: full_pinyin` 识别，双拼另外声明 `layout: xiaohe`。形码和临时拼音反查仍走原来的分支。

全拼默认开启简拼和拼写兼容：`nh` / `nhao` / `nih` 可输入“你好”，`zhongg` 可输入“中国”；`'` 强制音节边界。`nv/lv` 表示女/吕类音节，`nue/nve`、`lue/lve` 和 `ju/jv` 等按音节兼容。原始输入和大小写始终保留，不对整串字符串做替换，`nver` / `nv'er` 仍可输入“女儿”。

完整全拼路径优先，只有不能按完整音节解释时，才展开简拼和错拼；n/m/hm/ng 等独立辅音感叹音不独占短输入的简拼入口，句中已有至少两个完整元音音节时仍保留原全拼路径。没有完整路径时保留末音节补全。简拼按实际消耗的按键锁定、编辑和学习，不把一个声母当作完整音节长度。

设置中的 `全拼错拼纠正` 默认关闭；开启后处理一个音节内相邻字母错序，例如 `zhogn → zhong`，已有合法完整音节不作此类纠正。当前不做邻键、漏键、多键的通用编辑距离纠错。以下模糊音分别勾选，默认全部关闭：n/l、z/zh、c/ch、s/sh、en/eng、in/ing、an/ang。每条匹配使用一组模糊规则，不在同一音节叠加多组规则。

小鹤双拼按独立键位映射输入：`nihc → 你好`、`vsgo → 中国`、`ybhhka → 银行卡`、`isqk → 重庆`。零声母 `aa/oo/ee/ah/eg` 等遵循小鹤规则；最后仅输入一个键时可补全。全拼简拼、错拼、模糊音开关不作用于双拼。

## 选词与编辑

- 默认候选是整句首选和前缀词；固定短语和手动置顶可以排在前面。F2 切换整句候选菜单。Tab/Shift+Tab、上下键只移动选择；数字、小键盘数字、空格或鼠标点击确认；PgUp/PgDn 翻页。
- 部分选词只锁定预编辑前缀，全部确认后一次上屏。左右/Home/End 编辑未锁定编码；Backspace 到边界时解锁；Delete 删除后一个编码字符。
- Ctrl+Left/Right 按音节移动；Ctrl+Backspace 删除前一个音节，在锁定边界时解锁。Ctrl+Delete 留给“忘记学习”。
- Ctrl+[ / Ctrl+] 取当前高亮词的首字 / 尾字，输出该字并结束本次输入，不学习整词纠正。
- Escape 取消；Enter、中英文切换、CapsLock 输出原始编码。没有概率提前上屏或形码空码顶屏。
- 完整拼音、已识别简拼和字词可用标点确认；末音节补全不由标点直接确认。工具表达式、网址和邮箱中的标点属于原始内容。
- 全拼预编辑显示实际编码；TSF 使用 UTF-16 光标位置。首次确认可以等待资源加载，普通字母输入保持异步。资源错误时保留编码，可用 Enter 输出或 Escape 取消。

## 英文、网址与简繁

`拼音英文候选` 默认开启。英文词及固定中英混合词从独立词表召回，支持 `Windows`、`wifi`、`typec`、`windowsbanben` 等；可先选择英文前缀，再继续处理后面的中文。英文输入首字母大写时生成首字母大写候选，前两个字母大写时生成全大写候选。英文与混合词不进入中文 n-gram，不自动给连续英文插入空格。

输入 `user@example.com`、`https://example.com`、`www.example.com` 或 `Type-C` 时进入原文状态，数字和标点保留，空格或 Enter 确认。含点的邮箱用户名在首段能够识别为英文时支持自动进入原文状态；其它复杂原文可先切英文模式。原文状态持续到确认或取消，避免删除中途误变成汉字。

`拼音繁体输出` 默认关闭。开启后使用 Windows 简繁转换显示候选并提交繁体，解码和纠正学习保留原始简体路径。它不是台湾、香港地区词汇本地化转换。

## 用户词、短语和候选管理

Ctrl+= 打开加词窗口，普通用户词填写“词条＋逐字无调拼音”，例如 `虎爪 / hu zhua`；最多 10,000 条，每条最多 16 个 Unicode 字符。用户词保存在方案目录的 `pinyin-user-words.json`，每条词边增加 9 分排序偏置。

点击窗口中的“短语与候选管理…”可查看固定短语、置顶、用户词和学习记录。固定短语填写字母短码和任意文本，例如 `yx / me@example.com`；支持换行，短码最多 128 字符、内容最多 4096 字符。选中列表记录可删除短语或用户词、取消置顶或忘记学习。

候选右键菜单及快捷键：

| 操作 | 快捷键 | 保存与作用范围 |
|---|---|---|
| 置顶 | Ctrl+P | 当前候选所消耗的编码；同一编码保留一个置顶 |
| 取消置顶 | Ctrl+L | 取消该编码、词条的显式置顶 |
| 忘记学习 | Ctrl+Delete | 异步撤销该编码、词条已有纠正记录；不删除基础词典或手动用户词 |

短语和置顶保存为独立 `pinyin-preferences.json`，先原子保存后发布。学习保存为 `.tigerclaw-learning-pinyin-v1.log`，忘记操作追加原日志支持的撤销记录；之后明确纠正仍可重新学习。升级必须保留这三个用户文件。

`全拼纠正学习` 默认开启，只在新版 TSF 确认目标应用成功插入后记录明确纠正。第一候选不自动强化；仅选中、锁定、取消或插入失败不会学习。沿用 30 天半衰期、1000/次权重和 16 分上限。学习召回和显式置顶都必须满足当前编码的合法字音路径，固定短语与英文另行处理。

## 日期、符号与虎码辅助

| 输入 / 操作 | 结果 |
|---|---|
| `/rq`、`/sj`、`/xq`、`/dt` | 日期、时间、星期、日期时间 |
| `/r101.05` | 壹佰零壹元零伍分 |
| `/v(2+3)*4` | 20；另一个候选可输出完整算式 |
| `/fh`、`/jt`、`/emoji` | 常用符号、箭头、表情 |
| 拼音后加反引号，再输入虎码 | 按当前未确认首字的虎码前缀筛选 |

计算器仅支持十进制数、括号、`+ - * / %`；不执行脚本。金额最多两位小数、绝对值小于十万亿元。表达式模式的数字用于输入，用空格确认；日期和符号等已完成的命令也可数字选词。`拼音表情候选` 默认开启，会在“笑脸”“点赞”等相关中文后提供表情，且不抢占中文首选。

虎码辅助示例：`ni` 后输入反引号与 `jx` 可筛选“你”；`kcvb` 可筛选“泥”。有锁定前缀时辅助当前未确认的首字。辅助约束进入搜索，不只是过滤已经裁剪的候选。辅助状态 Backspace 删除虎码，删空后再按一次退出；反引号也可退出。它使用包内虎码数据，不修改用户形码方案。

## 源码与数据

| 路径 | 职责 |
|---|---|
| `next/TigerClaw.Pinyin/` | 共享字音解码、词典、会话、资源和用户词 |
| `next/TigerClaw.Pinyin.Native/` | KenLM 查询 DLL；x64/MSVC、ARM64/ClangCL |
| `third_party/kenlm/` | 查询源码、原始 SHA-256 清单、许可证 |
| `next/TigerClaw.Core/InputMethodEngine.FullPinyin.cs` | Core 状态、异步代际、候选确认、学习接入 |
| `next/Schemas/虎爪全拼/schema.json` | 方案描述模板 |
| `tools/FullPinyinEval/Joint/` | 直接引用共享解码器的冻结评测入口 |
| `next/TigerClaw.Core.Tests/FullPinyinTests.cs` | 会话、Core、协议与持久化测试 |

固定基线为原始 65,120 条拼音词典、`tokens.json` 和 `joint_3gram.klm`（474,049,472 字节）。模型 SHA-256：

`8a9aec29184dc12ae2f4740500102ca9e48a9a77e78ffeea0be25111ca27f2fc`

模型从方案的 `resources/` 读取，KenLM 只读映射；查询缓存受限。切换方案不闲时卸载模型，进程退出后释放。资源文件改变后重启内测 Core；用户词窗口中的增删即时生效。

第三方 KenLM 来自本机 libime 固定提交 `4cb443e60b7bf2c0ddf3c745378f76cb59e254e5`。唯一上游修改是 Windows UTF-8 文件路径转换为宽字符 `_wopen`，支持中文目录。查询库使用静态 MSVC CRT，模型本身不是可执行代码。随内测包附 `kenlm-source.zip`，包含源码、补丁后的文件和全部相关许可证；模型训练说明见 FullPinyinEval 交接文档指向的外部记录。

英文资源来自 `iDvel/rime-ice` 固定提交 `9e66b0729083b37d217312294f6d516c8d7234be`，原始词表、LICENSE 和 SHA-256 来源清单位于 `third_party/rime-ice-english/`，也随内测包原样分发。小鹤键位依据 Rime 官方 `double_pinyin_flypy.schema.yaml`，实现使用独立映射表。

## 隔离构建与打包

在 Windows 仓库根目录执行：

```bat
next\build_full_pinyin.bat ARM64
next\build_full_pinyin.bat x64
```

需要 .NET 10 SDK、Visual Studio C++/Windows SDK、ARM64 ClangCL（构建 ARM64 KenLM）与 .NET Framework 4.8.1 开发组件。脚本只构建到 `next/_run/FullPinyin/`；不注册 TSF、不停止已有进程、不覆盖 `release/` 或 `release_arm64/`。两个架构的构建请顺序执行，Dialog/Shared 有公共中间目录。

WSL 打包示例：

```bash
python3 tools/package_full_pinyin.py --arch ARM64 --name arm64-internal-20260921-r3 --dictionary original \
  --model /mnt/c/Archive/tigerclaw_sentence_ml/model-pinyin/models/joint_3gram.klm \
  --tokens /mnt/c/Archive/tigerclaw_sentence_ml/experiments/joint-pinyin-20260921/tokens.json
```

打包要求目录不存在；验证冻结模型/词典/token-map 的 SHA-256 和原生 PE 架构，生成逐文件 manifest，不包含 GGUF，不复制原有运行目录中的配置或用户数据。

内测包包含现有安装/卸载脚本，但本次不执行。安装会使用现有虎爪身份，并影响已安装的虎爪；不是并行安装的另一个产品。ARM64 包包含 ARM64X wrapper、原生 ARM64/x64 侧载 DLL 和 Win32 DLL；x64 包包含 x64/Win32 DLL。对应应用仍须实机验收。不要将内测目录直接覆盖日常 `release_arm64/`。

无需注册、无需启动生产 IPC 的 AOT 加载检查：

```bat
TigerClaw.Core.exe --full-pinyin-probe "码表\虎爪全拼" "probe.json"
```

输出文件必须不存在。此命令在启动上下文检查后、注册/单实例/IPC/UI 之前执行，加载实际模型并记录完整/分隔/补全/ü 音节用例。

## 验证记录与剩余验收

本机试用反馈：“要优化好久”（`yaoyouhuahaojiu`）在干净 r3 模型中排第 2，首选为“要有话好久”；其分数分别为 -19.156915 与 -16.854639。精确全拼、默认简拼和 Beam 2000 均复现，属于基础模型的同音词排序偏差。基础词典已有“优化”，不是词条缺失。默认菜单按 F2 切整句候选，再按 2 可以选中正确整句。隔离真实模型测试确认：一次成功提交回执后，纠正学习将正确句升为首选，新引擎重新加载后仍保留。新增 `--full-pinyin-feedback-tests <fresh-fixture-root>` 覆盖此过程；证据位于 `next/_run/FullPinyin/youhua-issue/`。此项验证了现有学习机制，没有修正未学习时的基础模型误排，也未向实际运行目录人工写入学习或置顶记录。

已完成的证据均在 `next/_run/FullPinyin/` 或测试日志中：

- x64、ARM64 KenLM 查询 DLL、Native AOT Core、TSF、原生 Overlay、Dialog 构建。
- 5,246 项独立 oracle/cache 比较，字音状态、对数底、BOS/EOS 测试。
- 关闭新拼写功能时，ARM64 8,019 条冻结集：Top-50 文本顺序、读音/分段路径完全一致，分数最大误差 0。`tools/FullPinyinEval/verify_shared.py` 可独立复核。
- 默认开启简拼与别名时，同一 8,019 条正常全拼输入也保持全部 Top-50 列表、语义读音路径和分数一致：Top-1 4,321、Top-10 6,464、Top-50 7,054；新增退化 0。证据 `expanded-release-comparison.json`，可用 `tools/FullPinyinEval/compare_spelling.py` 复核；这不是简拼、错拼语料的准确率结论，也不含英文/短语候选通道。
- 全拼专项：前缀菜单、锁定/解锁、大小写、光标编辑、末音节补全、精确模式简拼拒绝、新模式全简拼混输与拼写别名、分隔、标点、取消/旧代际结果、提交回执、用户词增删、候选令牌与重试去重。
- 新增专项覆盖错拼/模糊音开关、小鹤双拼、虎码搜索约束、音节编辑、短语与置顶持久化、忘记学习后重启/重新学习、英文大小写/混合词、URL/邮箱、金额/计算器按键、繁体候选与提交、以词定字。
- `--full-pinyin-real-tests <fixture-root>` 使用隔离标记目录、实际模型、英文和虎码资源，检查简拼首选、英文前缀接中文、混合词、辅助码、繁体上屏与通过设置切换小鹤方案后的实际按键提交；不创建 IPC 或注册 TSF。另有两个架构的 AOT 成品加载探针，21 例 Top-10 输出一致。
- ARM64 共享解码器的 12 句小样本逐键检查：376 次追加、376 次退格。默认简拼/别名模式追加中位数 2.25 ms、P95 58.49 ms、最大 256.68 ms；退格 P95 0.24 ms。纯精确模式追加 P95 12.20 ms。简拼搜索确有额外代价；这组结果包括首轮索引建立，不含模型加载、Core 调度、英文菜单和 GUI，不代表真实应用端到端延迟。原始记录与样本见 `validation-expanded/latency-*.jsonl`。
- 现有 UI 快照并发、候选设置/提交时限、TSF 管道故障、原生 Overlay 模型/渲染检查。
- 现有学习专项 25,717 项检查、整句专项 938,714 项检查（7,388 个快照）通过。全量 Core 测试仍在 `SentencePersonalizationPromotesOnlyMatureOrdinaryEvidence.second_learning_partial_contribution` 失败；隔离构建的修改前 HEAD 在同一断言失败。本次没有更改该既有断言或声称全量全绿。

尚需真实应用验收：记事本、浏览器文本框、聊天窗口、Office/WPS（含 32 位与 ARM64/x64 混合宿主）、焦点/密码框保护、中文目录、升级保留用户词、前缀锁定后的光标与鼠标提交。特别检查后台候选更新、连续点击、断线重连时不漏字、不重复上屏。当前隔离测试不代表这些应用已经验收。

正式合入日常发布前还需要一周实际试用、用户纠正前后对照和键入延迟/内存记录。商用品对比结论限于已冻结语料与当前口径；不能将其等同于全面超越商业输入法。


## 2026-09-22：2,481.68 MB 五阶重排已接入 ARM64 测试版

按用户指定采用原始三阶搜索 + **完整、未额外裁剪的 Q8 五阶**重排，
不是 466 MB 方案的 0.625 混合权重，也不是五阶直接 Beam 搜索。

- 三阶 `joint_3gram.klm`：474,049,472 bytes，原冻结 SHA 保持不变。
- 五阶 `joint5-q8.klm`：2,007,626,060 bytes，SHA-256
  `125c9231049fe4aadb35c90d8b4aeb81d73d7ee165f5d9c3762781a110c8dabc`。
- 两模型合计 2,481,675,532 bytes（十进制 2,481.68 MB），不是进程驻留内存。
- `schema.json` 可选 `rerank_model: resources/joint5-q8.klm`，
  `reranker: joint-fivegram-top50-v1` 是说明字段；省略模型字段保持原三阶流程。
- Beam 200 仍由三阶搜索，取 Top50 后补回合法的学习/置顶候选，一起用五阶
  完整状态、BOS/EOS、自然对数重新评分。显式保留每字 +2、用户词奖励和拼写惩罚。
  学习加分、置顶及英文工具菜单仍在其后应用。确认的前缀进入模型一次。
- 原生三阶入口仍拒绝五阶；独立五阶入口检查阶数。模型加载或评分异常整批回退到
  三阶。取消不会禁用模型。重排与解码使用同一 worker/generation，不引入迟到的第二次排序。
- 模型按 schema 生命周期只读映射，无 LLM、无闲时卸载。全拼/小鹤通过硬链接共享
  同一模型文件；页面由操作系统管理。

验证证据位于 `next/_run/FullPinyin/fivegram-20260922/`：

- `frozen-verification.json`：8019 条、400950 个候选与此前 Q8 五阶实验逐项一致，
  最大分数误差 0；首选 4890/8019 = 60.98%，原三阶 4321/8019 = 53.88%。
- 同次逐行回放，托管重排 P50 0.4742 ms、P95 0.8534 ms；这不包括三阶搜索、
  模型加载、UI/TSF，不能当作实际按键延迟。
- `tests.log`：全拼/扩展功能，以及新增重排奖励保留、锁定前缀、取消、失败回退测试通过。
- `real-tests.log`：真实模型简拼、英文混输、辅助码、繁体、小鹤切换通过。
- `feedback.log`：五阶仍会把“要优化很久”排第二，一次成功上屏回执纠正后成为首选，
  重启后保留。该测试只写隔离 fixture。
- `fallback-probe.json`：缺失可选五阶文件时保持三阶正常输出。
- `aot-probe.json`、`installed-probe.json`：ARM64 AOT 实际加载五阶，评分无回退；
  “要优化好久”已是首选，“要优化很久”尚非首选（无学习状态）。

已更新 `C:\Users\yc\Desktop\虎爪全拼内测-r3` 的 Core、jointkenlm.dll 和两个
schema descriptor；新增模型硬链接。配置、用户词/学习文件保留，TSF/Overlay/Dialog
未替换。`deployment.json` 记录指纹；`installed-runtime.json` 记录新 Core/Overlay 路径
及管道响应。旧文件备份于测试版目录 `backup-before-fivegram-20260922-014718`。
原 `release_arm64/` 未改动。以上不代替真实应用输入验收。

回退时先正常退出该测试版 Core，将备份里的 Core、jointkenlm.dll、两个 schema.json
按原路径恢复，再启动 Core；无需删除配置、学习记录或模型。

构建仍用 `next\build_full_pinyin.bat ARM64`。重新打包时，在原命令上附加
`--rerank-model /mnt/c/Archive/tigerclaw_sentence_ml/experiments/joint-5gram-beam-20260921/models/joint5-q8.klm`，
打包器校验冻结五阶 SHA，并对资源复制结果再次验 hash。


## 2026-09-22 拼音选词消耗边界修复

用户反馈选词后只消耗一个编码。回归复现：默认简拼开启时，`nihaoma` 的“你”
菜单项错误地使用简拼 `n` 的 End=1，而实际保留整句路径为全拼 `ni` 的 End=2。
根因是 PinyinSession.RebuildMenu 仅按 token 匹配词前缀，未匹配逐音节原始编码边界；
简拼边错误继承全拼整句分数。现同时验证 token 与逐音节 raw end，既允许跨词典分段
组合前缀词，也保留真实简拼、别名和双拼边界。无 RawEnds 的旧全拼边/末音节补全
按原始位置恢复边界。

新增回归覆盖 nihaoma、ni'hao'ma、nihm、nhm、关闭简拼的分隔符、nihcma 双拼、
nihaom 尾音节补全，验证选词锁定及后续完整提交；完整 full-pinyin-tests 通过。
真实 3gram+完整 Q8 5gram Core 集成测试通过，包括分别选“你”/“你好”再提交“你好吗”。
ARM64 Native AOT probe 23 项运行成功，五阶加载/评分无错误。
证据：`next/_run/FullPinyin/prefix-boundary-20260922/`。

已仅替换桌面 `虎爪全拼内测-r3/TigerClaw.Core.exe` 并重启，IPC 确认当前虎爪全拼、
Core/Overlay 路径正确；配置、schema、用户文件哈希保持一致，模型未变。
备份：`backup-before-prefix-fix-20260922-111241`。
Core SHA256：`5B4EF60110C252F58F2E7F4DEB9883CBC1CD8CEDA674604FD37549F9D46D0C2A`。
未更改 release_arm64；自动化 Core 测试不等同于真实应用 TSF 上屏验收。


## 2026-09-22 用户指定的全拼候选布局

普通菜单先显示 1～2 条整句，再显示原始编码前缀匹配的字词：Unicode 字符数降序，
同长度按词典 Entry.Frequency 降序。整句首选的保留候选池 softmax 分数权重（含学习奖励）
达到 0.90 时只显示一条，否则显示两条；不足两条时自然取现有条数。
这是界面启发式，不是校准后的实际正确率，也不用于提前上屏。
固定短语、用户手动置顶、偏好英文原有优先级仍生效；F2 仍展示完整整句菜单。

字词直接遍历拼写索引，独立于整句 Top50，不再只从保留整句的分段抽取。
继承解码器的全拼优先决策，维持辅助码首字约束；逐音节 RawEnds 与分隔符由拼写索引生成。
同词拼写优先较低代价/较长消耗，避免全拼 ni 出现只消耗 n 的简拼候选。
英文/混输前缀按字词长度及自身频率合并排序，防止中文候选过多将其挤出 200 项菜单预算。
末音节补全保留真实解码分段；合成整句仅来自整句区，不作为普通词条混入排序。

回归：PinyinMenuTests 验证置信度分支、整个候选池质量、长词优先/同长度词频、
Beam 之外同音词选词续输、辅助码、强制分隔符、英文前缀在拥挤菜单中的保留。
完整 full-pinyin-tests、真实五阶 Core 功能测试、五阶 TSF 回执模拟学习/重启持久化、
ARM64 AOT 23 项 probe 均通过；真实应用 TSF 上屏仍与离线验证分开。
证据：next/_run/FullPinyin/candidate-menu-20260922/。

已只更新桌面 虎爪全拼内测-r3 的 Core 并重启，运行路径及当前虎爪全拼 IPC 验证通过。
备份 backup-before-candidate-menu-20260922-114350；配置、schema、用户文件校验保留，
未修改模型或 release_arm64。Core SHA256：
1D3EB7898AD1A340ED1423E5B8A17B7C450473260F1A4A49096143C6F9C3CCEC。


## 2026-09-22 12:09 合法前缀边界与英文匹配方向

用户截图 changyongzi 显示差/查和英文 chang/chan，要求前缀必须合法切分，
随后明确英文应当是当前完整输入编码与候选码完整匹配，或当前输入为候选码前缀。

中文菜单现计算可切分后缀的布尔表，匹配词的每个音节边界均须合法。
不允许裸辅音/简拼额外制造 cha|ng、chan|g 这类完整 chang 内部边界；
解码首选已经确认的简拼/感叹词边界保留，完整拼写不以残留 g/ng 待补全为由拆开。
未完成末音节继续允许合法补全，xian 与 xi/an 等合法歧义同时保留。
保持前缀字词独立于 Beam 文本、长词优先及同长度词频排序。

英文规则覆盖先前英文前缀锁定行为：现在 Windowsma 不再给 Windows，
changyongzi 不给 chang/chan；wind 可补全 Windows，Windows 可完整匹配，
windowsbanben 仍可匹配混输词 Windows版本。英文候选消耗整个 live input，
不再从输入中截取一个较短英文词。固定短语、拼音锁定、URL/email 不受此改动影响。

完整 full-pinyin-tests 和真实五阶 Core 测试通过，覆盖截图案例、常用→字续输、
合法拼音歧义、简拼/双拼/别名、英文匹配/补全/反向前缀拒绝。
ARM64 AOT probe 五阶加载与评分正常；安装运行路径/当前方案 IPC 验证通过。
证据：next/_run/FullPinyin/legal-prefix-20260922/。
仅更新桌面 虎爪全拼内测-r3/TigerClaw.Core.exe，用户数据及模型保留。
备份 backup-before-legal-prefix-20260922-120939；Core SHA256：
340811CB44FCB3AD34B4E21DA70D30A55D547570910F13BF759E0BE4A8451A8F。
真实应用键入体验由用户继续验收，离线测试不等同真实 TSF 上屏。


## 2026-09-22 12:19 默认改为万象词库

用户明确要求默认使用万象词库，取代此前“回到原词库”的旧决定。
全拼/小鹤共享默认资源：万象 Base v18.0.8 主词典所有 import_tables，
2,202,473 条（单字 49,353，多字词 2,153,120）。保留原读音及最大重复词频；
字音 token 直接取原词表逐字音节，不自动猜多音字。原 65,120 条词库仍可回退。
词表 SHA256 与原万象替换实验完全相同；这不意味着旧准确率或原词库冻结结果仍适用。

资源：C:\Archive\tigerclaw_sentence_ml\runtime\full-pinyin-wanxiang-20260922。
wanxiang-pinyin.txt SHA256 f5a5215792bf2046f394621b0f37cda79015e9d8281165b11f50c1eb6c5d6b5f；
wanxiang-tokens.json SHA256 4b15b39d7d4435d1e4e81673c7b621459d9df8d323fae2e4f7d8631f9e834a98。
来源清单/排除明细/转换政策：third_party/wanxiang-pinyin/；源词表、README 随资源归档。
当前换词库，不替换三阶和五阶模型，不使用万象的 Lua 或语法模型。

next/Schemas 两个方案默认资源名改为 resources/wanxiang-pinyin.txt 与
resources/wanxiang-tokens.json，并注明 lexicon_provider。打包默认 --dictionary wanxiang，
无需传 --tokens/--table；--dictionary original 可打旧词库实验包（勿混用新旧 token map）。
PinyinUserWords.Merge 在用户词为空时复用不可变 Baseline，避免复制整个大词库。

验证：导出器 5 项测试；完整 full-pinyin-tests；真实 220 万词库 Core 测试，
含合法前缀、英文完整匹配/补全、辅助码、繁体、小鹤；ARM64 AOT probe 均通过。
AOT 实测资源加载 14.67 秒，首次拼写索引/nh 查询 6.57 秒（冷初始化），
后续不同样例解码约 1～453ms，不能概括为所有逐键延迟。词库增大也提高内存开销。
此前 65k 词库基准不可套用本版本，未重跑全量准确率。
证据：next/_run/FullPinyin/wanxiang-default-20260922/。

已更新桌面 虎爪全拼内测-r3 的 Core 与两个 schema；新资源使用独立文件名，
原 pinyin.txt/tokens.json 原位保留。用户配置/学习未变，模型未变，release_arm64 未触碰。
备份 backup-before-wanxiang-20260922-121920。已核验两方案的安装资源哈希及运行 IPC。
Core SHA256 A945292503E5F397B7814CE820DD23EAEB870A052F4B619BCAAA7150CF8EB7F0。

当前打包命令（先按文档构建最新 Core/前端）：
```bash
python3 tools/package_full_pinyin.py --arch ARM64 --name arm64-wanxiang-20260922 \
  --model /mnt/c/Archive/tigerclaw_sentence_ml/model-pinyin/models/joint_3gram.klm \
  --rerank-model /mnt/c/Archive/tigerclaw_sentence_ml/experiments/joint-5gram-beam-20260921/models/joint5-q8.klm
```


## 2026-09-22：单字纠正学习隔离

单字使用 `full-pinyin-character-v1` 独立类别及竞争索引，词句仍使用
`full-pinyin-v1`。两类事件沿用同一个 schema-local TCL1 回执日志，以保留
成功提交回执、撤销 ID 和管理入口；这里是数据类别/索引隔离，并未拆成两个文件。
旧日志中的单字事件在完整回放和增量索引中自动归入单字类别，不改写原记录。
单字与词句的竞争衰减也在各自类别中独立进行。

仅当未锁定前缀、整个输入编码消费完毕、选中单字且不是未完成音节补全时，
才记录单字纠正。整句中选单字前缀或选锁定前缀后的单字不记录单字学习。
单字学习仅以完整 raw code 精确查询，并且仅给对应完整单字候选加分；不会
参与词句前缀匹配、整句加分或延续搜索保留。词句前缀纠正仍然有效。
“忘记学习”统一处理新类别和旧单字事件的原始撤销 ID。

验证包含单字选择/成功回执/生效/忘记、单字前缀不学习、旧事件完整及增量
回放一致、词句前缀学习保留。真实万象词库＋三阶/五阶模型 fixture 注入旧
`shi → 是` 事件，验证 `shiyixia` 首选仍为“试一下”。证据目录：
`next/_run/FullPinyin/character-learning-20260922/`。隔离 Core 测试不等同于
真实应用 TSF 输入验收。

全拼基础/扩展回归、真实模型回归、25,717 项学习检查通过，ARM64 AOT 已构建。
2026-09-22 12:41 已更新桌面 r3 Core，备份为
`backup-before-character-learning-20260922-124059`；Core SHA256 为
`22379C21AEA6AD89EBCECB2B83C2C8C5333FE9B23CD6B9D373844B0A28F3FE42`。
部署核对 7 个配置/方案/学习文件未改变，随后 Core/Overlay 和 IPC 正常。

2026-09-22 逐键响应诊断见 [LATENCY_20260922](../tools/FullPinyinEval/LATENCY_20260922.md)：
本机 ARM64 热态 230 键搜索占累计计算约 95%，P95 总计算 135.8 ms；
五阶重排 P95 0.57 ms。此轮仅分析，未部署性能改动。


## 全拼性能优化（2026-09-22）

后续性能实现与验收见 [PINYIN_PERFORMANCE_20260922](PINYIN_PERFORMANCE_20260922.md)。
采用紧凑词库、流式 token、共享模型、整数/批量评分、最新代工作器及一次后台菜单
构建；保持现有词库、三阶/五阶及候选行为。旧桥接 DLL 自动回退标量查询；完整
性能需要同时更新 Core 和 `jointkenlm.dll`，无需更换模型、TSF 或日常 Overlay。
