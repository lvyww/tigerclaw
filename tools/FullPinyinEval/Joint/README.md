# 联合字音模型离线对照

只用于实验，未接入或部署 Windows Core / Rime。读取用户提供的
`model-pinyin/models/README_模型读取说明.md`，使用正式 `joint_3gram.klm`，
同时测试同批 `char_3gram.klm` 与原 full-kn-m5-v2。保留原始拼音反查词库，
不使用万象词库、不运行 Qwen、不重标注冻结测试输入。

结果目录：`C:\Archive\tigerclaw_sentence_ml\experiments\joint-pinyin-20260921`。
`REPORT.md` / `report.json` 为最终结果，`parts/` 保存所有独立进程的候选与日志。

## 读取和搜索

- C++ `kenlm_bridge.cpp` 使用 `LoadVirtual` 自动加载 trie；模型和状态布局由
  KenLM 自己管理，C# 不猜测状态的字节布局。自然对数分数为 KenLM log10 × ln(10)。
- 三元模型状态由最后两个词元确定；初始双标记在适配层转换成一次 KenLM 句首状态，
  每个完整候选末尾只计一次 EOS。模型只加载一次，评分缓存上限 262,144 个三元查询。
- `prepare.py` 用原词库单字读音对齐词条；缺口才使用模型已观察读音，歧义直接报错。
  本次只有“道行、南无、咱们”三条需要后者，全部 65,120 条候选记录保留。
  原始编码 nue/lue 对齐模型 nve/lve，nv/lv 不改为 nu/lu。
- 联合词元与显示字符分开；每个词条仍逐字扩展，奖励仍是每字 +2。
  Beam 以 `(文本, 前二词元, 前一词元)` 合并，只有最终展示列表按文本去重。
  因此同字不同读音的低分前缀仍可在后续上下文胜出。回删复用相应历史 lattice。
- OOV 显式映射到模型 `<unk>`，不删除原词库候选。`tokens.manifest.json` 列出
  全部未见词元，最终报告列出包含 OOV 的首选。

## 验证

沿用原解码器的 5,246 项穷举／缓存对照和边界测试，另加同字不同读音状态、
最终去重、追加／回删、BOS/EOS、log 底数、OOV 测试。
100 条独立原生 KenLM 逐状态查询与 C# 三元历史适配器比较分数和 OOV。
全部测试句再跑 m5，与冻结 Windows 结果逐条检查 Top-50 顺序和分数，防止
平台切换或公共解码器改动混入模型效果。

`Joint.csproj` 是独立 net10.0 控制台，复用原 Decoder、Tests 及只读 m5 读取器源码。
接口声明与 Core 相同，原项目通过 `Compile Remove="Joint/**/*.cs"` 隔离此入口。
本次使用 Windows SDK 构建托管 DLL，WSL ARM64 .NET 10.0.11 运行；原生库也是
Linux ARM64，不能用于 Windows。m5、char、joint 均使用同一平台与新托管程序集。

典型入口（`ROOT` 为新实验目录，`DATA` 为冻结原实验目录）：

```bash
python3 tools/FullPinyinEval/Joint/prepare.py "$TABLE" "$MODELS/joint_3gram.arpa" "$ROOT"
# 构建 Joint.csproj，并在 ROOT/bin 提供对应架构的 libjointkenlm.so / libkenlm.so。
LD_LIBRARY_PATH="$ROOT/bin" "$DOTNET" "$ROOT/bin/Joint.dll" test "$MODELS/joint_3gram.klm"
python3 tools/FullPinyinEval/Joint/run.py "$ROOT" --dotnet "$DOTNET" --table "$TABLE" --m5 "$M5" --models "$MODELS" --baseline "$DATA" --jobs 12
python3 tools/FullPinyinEval/Joint/report.py "$ROOT" "$DATA"
```

首轮所有模型固定 Beam 200；三个模型的单进程逐键测试在批量解码全部退出后依次运行。
`run.py --beam 2000 --kinds joint` 可在新目录单独测试联合模型的 Beam 2000；
入口 `decode` / `bench` 也接受末尾的 `--beam N`，未传时仍为 200。
`beam_report.py 旧实验目录 新实验目录` 校验两次联合模型的词库、模型、输入与
字音映射指纹，比较准确率、救回/退化和相同逐键操作的延迟。
`run.py` 不覆盖已有候选结果，复现实验应选新目录。未证明测试语料与训练语料互斥，
注音仍有粗标注误差；不把本结果等同于实际前端打字验收。

## 固定 Beam 200 的 Qwen 配对测试

2026-09-21 停止 WeType 持续采集后，取全部已完成快测及必要慢速复核的
1,050 条共同样本（原冻结 test 分区，编码不超过 60 字母）。复用经输入与模型
指纹验证的原词库联合模型 Beam 200 候选，为相同的 Top-10 重新运行 Qwen3
0.6B Q8。固定 alpha=0.45（沿用旧 m5 开发集参数，未针对联合模型/本轮测试调参）。

- 无重排：634/1,050，60.381%，字符错误率 6.871%。
- 带重排：745/1,050，70.952%，字符错误率 4.631%。
- 救回 146 句、退化 35 句，净增加 111 句。

`paired_qwen.py 实验目录` 使用冻结的独立 Qwen 客户端和 FullPinyinProbe，4 个
评分进程，每个 4 线程；已有结果按指纹校验后可续跑，失败不伪装为正常回退。
`--report-only` 只重算报告。报告包含成对案例与并发条件下的整请求耗时。

结果目录：`C:\Archive\tigerclaw_sentence_ml\experiments\joint-qwen-wetype-1050-20260921`。
其中 WeType 快速一次输入与“快测错误再慢测”的综合通过率分别列出；后者有
两种输入节奏的额外机会，不视为统一速度下的一次首选准确率。当前用户学习和
云端设置未隔离。本实验不修改生产 Core 的 Beam、候选上限或重排策略。
