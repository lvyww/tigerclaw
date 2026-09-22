# 离线全拼整句实验

新会话交接见 [HANDOFF_20260921.md](HANDOFF_20260921.md)：联合字音模型、微信/万象/白霜同集对比、文件位置与未提交状态。

以拼音反查表的单字和多字词条构建拼音 trie，联合搜索切分与文字路径，使用现有
full-kn-m5-v2 字级三元模型排序。不同切分产生的相同文字只保留最高分路径，得到
前 10 个不同句子后交给 Qwen3 0.6B Q8，按 `(1-alpha)*ngram + alpha*qwen` 融合。
保留前 50 名用于召回诊断；词频只用于 n-gram 同分时的稳定排序。

2026-09-21 实测：开发集 1,981 条选中 Beam 200、alpha 0.45；独立保留的 8,019 条
测试片段（4–30 字，平均 17.18 字）中，n-gram 整句首选 48.959%，融合后 62.838%，
救回 1,318 条、退化 205 条。Top-10 召回 76.169%；仅单字表为 75.745%。字符编辑
错误率从 7.436% 降至 4.875%。这是文本完全匹配测试，下面的注音/训练集边界仍适用。

Snapdragon X2 Elite Extreme 上，100 条样本的单进程逐键追加 p95 / p99 为
8.49 / 17.45 ms，回删为 0.26 / 0.43 ms。Qwen 测试集使用 4 进程、每个 4 线程，
每次整句请求 p50 / p95 为 1.14 / 2.08 秒；这些不同测量不应混作应用按键延迟。
完整报告、案例和冻结输入位于
`C:\Archive\tigerclaw_sentence_ml\experiments\full-pinyin-20260921\REPORT.md`。

这是独立控制台实验，不会启动生产 Core、注册输入法、连接生产管道或修改日用目录。
尚未接入虎爪、Rime 或 Fcitx5。没有重新训练模型，也不使用形码的选重、最优码奖励、
提前上屏、自学习与词先验规则。字符奖励为每字 `+2`，含 BOS/EOS 模型分数。

## 万象对照

2026-09-21 使用官方万象 Base v18.0.8 和简体 LTS 语法模型，跑同一批 8,019 条测试句。
整句首选 48.722%、字符编辑错误率 7.952%；本原型纯 n-gram 为 48.959% / 7.436%，
加入 Qwen 后为 62.838% / 4.875%。这不是日常输入的完整验收，也没有证明训练集独立。
完整产物：`C:\Archive\tigerclaw_sentence_ml\experiments\wanxiang-20260921\REPORT.md`。

`rime_compare.cpp` 使用真实 librime API、独立用户目录，始终不提交文本。
全量准确率用 `set_input`；100 条逐键输入验证完整候选列表相同。`bench` 对与原型完全相同
的 100 句测追加／回删。万象计时包括 Lua/候选页面，原型仅计 C# 解码器且运行系统不同，
不可当作同一软件层级的速度横评。万象逐键追加／回删 p95 分别 33.19 / 33.14 ms。

探针需要 librime 1.16.1 API，以及 librime-lua。`RIME_EVAL_OCTAGRAM` 指向独立编译的
octagram 插件（本次 commit `57d18b9f58e5284bd891d559f6bdd16cf60341e9`）。不能省略
该环境变量后仍把结果标成有语法模型：本机系统 librime 原先并没有提供这个组件。
模型加载已经用 `strace` 的 open/mmap 记录和关闭模型的对照验证。

实验目录保留官方 ZIP、完整隔离配置、模型、编译后词库、TSV、结果、插件、探针与 SHA256。
`user/wanxiang.custom.yaml` 仅关闭主词库学习，并在最后追加只改候选注释的
`lua/eval_boundary.lua`，用于记录候选消费范围；词库、默认全拼转写及模型参数保持官方值。
万象通常只生成一个完整句子，其余菜单项是部分词语，不能直接与十条完整句子的召回比较。

```bash
g++ -std=c++17 -O2 tools/FullPinyinEval/rime_compare.cpp -lrime -ldl -o /tmp/rime-compare
# ROOT 指向独立实验目录，绝不能指向日用 Rime 用户目录。
RIME_EVAL_OCTAGRAM="$ROOT/librime-octagram.so" /tmp/rime-compare "$ROOT/user" "$ROOT/shared" "$ROOT/test.tsv" "$ROOT/test.jsonl" decode
RIME_EVAL_OCTAGRAM="$ROOT/librime-octagram.so" /tmp/rime-compare "$ROOT/user" "$ROOT/shared" "$ROOT/bench.tsv" "$ROOT/latency.jsonl" bench
python3 tools/FullPinyinEval/compare_wanxiang.py "$TIGER_EXPERIMENT" "$ROOT"
```

## 万象词库替换实验

万象词库替换实验位于
`C:\Archive\tigerclaw_sentence_ml\experiments\full-pinyin-wanxiang-lexicon-20260921`。
`export_wanxiang.py` 按主词库 import_tables 导出全拼边，保留原频次，仅去声调、
规范 ü 和 nve/lve，并依现有加载器规则取重复项最大值、负频次归零。
导出 manifest 逐项记录无拼音编码、非汉字正文及异常权重；词库源文件不修改。
`test_export_wanxiang.py` 验证音调转换、ü、不合法编码、去重及异常记录处理。

`lexicon_ablation.py decode` 使用冻结的旧版二进制、m5 模型和输入，固定 Beam 200，
4 进程各 3 个解码线程；`bench` 在解码结束后单进程测相同的 100 句。
`analyze_lexicon.py` 验证哈希、完整 ID、参考字串、候选去重和名次，生成成对救回／退化案例。
本次按用户要求只运行 decode / bench，不运行 Qwen 阶段。原词库中的 Qwen 结果不能
当作更换词库后的重排效果。

## 白霜官方方案对照

2026-09-21 对官方 `gaboolic/rime-frost` 主线快照
`7d0d56540eacc2c54754618a1bbe68dc7af873f4` 测试，与已完成 WeType 采集的
1,050 条相同冻结测试句配对。保留默认词库、拼写、过滤链及自带 7,339,052 字节的
`zh-moqi.gram` 和组词惩罚参数，未另加大模型。

白霜首选正确 482/1,050（45.905%），字符错误率 10.818%。同批联合模型 Beam 200
为 60.381% / 6.871%，加 Qwen Top-10（alpha=0.45）为 70.952% / 4.631%。
这是当前默认配置在本测试集的表现，不等于日用体验或对其它配置的结论。

`rime_compare.cpp` 可通过 `RIME_EVAL_SCHEMA=rime_frost` 指定 schema，未设置时仍
默认 `wanxiang`。全量使用 `keys` 模式逐键输入，100 条另以 `decode`/set_input
核对完整候选列表；首选正确同时要求消费全部输入。独立用户目录，不提交、不学习。
`compare_frost.py 白霜实验目录 配对实验目录 --wanxiang 万象实验目录` 验证输入/模型指纹并生成对比与案例。
万象复用全量实验中相同 ID 和编码的 1,050 条：573 条正确（54.571%），字符错误率 9.029%；抽取结果保存为 `wanxiang-paired.jsonl`。

完整结果：`C:\Archive\tigerclaw_sentence_ml\experiments\frost-1050-20260921`。

## 输入和边界

- 接受完整拼音 `a-z`，大小写归一；`v` 表示 `ü`，`略/虐` 遵循表内 `lue/nue`。
- 不支持简拼、模糊音、声调数字、自动纠错或声调符号。合法的单字母音节仍可参与组句。
- `'` 强制切分边界，例如 `xi'an`；此前的完整音节和词组不受影响。
- 最后一个音节未完成时，返回完整前缀候选和 `tail` 原始编码，不把该尾码扩展成简拼。
- 追加和回删复用已完成位置的 lattice；任意替换和带分隔符输入完整重算。
- Beam 是近似搜索。Top-50 诊断是该 Beam 的结果，不是全空间穷举召回。

## 构建与验证

Windows .NET 10；原生程序使用主仓库已固定的 llama.cpp 与 Windows CMake 工具链。
输出示例中的 `C:\Archive\...\full-pinyin-20260921` 是实验目录，可替换。

```powershell
dotnet build tools/FullPinyinEval/FullPinyinEval.csproj -c Release --output C:\Archive\full-pinyin\bin
cmake --build next/_native_build/sentence-ARM64-ClangCL --config Release --target TigerClaw.Sentence.FullPinyinProbe --parallel 8
C:\Archive\full-pinyin\bin\TigerClaw.Core.Tests.exe test
C:\Archive\full-pinyin\bin\TigerClaw.Core.Tests.exe probe <FullPinyinProbe.exe> <sentence-qwen-q8.gguf>
python tools/FullPinyinEval/test_analysis.py
```

控制台程序集沿用 `TigerClaw.Core.Tests` 友元名称来复用主线只读模型读取器，不是原有
Core 回归测试入口。原生 `FullPinyinProbe` 是 `EXCLUDE_FROM_ALL` 目标，只接受独立随机
管道；离线客户端启动并清理自己创建的子进程。该目标上限为 10、上下文 4096、批量
2048、线程数 4。生产目标仍是 Top-5 / 512 / 512，生产取消测试额外验证第六候选被拒绝。

回归包括独立穷举评分、分词歧义、重复路径、单字母音节、Unicode 扩展字、非法输入、
不完整尾码、追加/回删/重输与完整重算比较。Qwen 探针检查 1/5/10 候选、顺序、
超限、断连及新进程恢复。离线批量评分遇故障会报错并停止，不能把故障伪装成有效
Qwen 准确率；n-gram 原始结果独立保存，可用于无 Qwen 的对照。

## 冻结语料与运行

WSL 驱动程序调用 Windows 实验可执行文件。依赖仅注音阶段的 `pypinyin==0.55.0`。

```bash
python3 -m venv /tmp/full-pinyin-env
/tmp/full-pinyin-env/bin/pip install pypinyin==0.55.0
/tmp/full-pinyin-env/bin/python tools/FullPinyinEval/prepare.py \
  /mnt/c/Users/yc/Downloads/corpus_50kfresh_10000_seed42.txt \
  release/拼音反查码表/拼音.txt /mnt/c/Archive/full-pinyin/data \
  --corrections tools/FullPinyinEval/annotation_corrections.json

python3 tools/FullPinyinEval/run.py /mnt/c/Archive/full-pinyin \
  --exe /mnt/c/Archive/full-pinyin/bin/TigerClaw.Core.Tests.exe \
  --table release/拼音反查码表/拼音.txt \
  --model /mnt/c/Archive/tigerclaw_sentence_ml/runtime/rime/sentence-ngram-mobile.bin \
  --host next/_native_build/sentence-ARM64-ClangCL/Release/TigerClaw.Sentence.FullPinyinProbe.exe \
  --gguf release_arm64/sentence/Models/sentence-qwen-q8.gguf --jobs 12
```

`prepare.py` 不读取模型或解码结果。文本 SHA256 前 64 位模 5 为 0 的约 20% 为开发集，
其余为测试集。重复、空行、非汉字与注音失败逐项记录，不按正确率剔除。表覆盖检查
独立于 Beam；未覆盖的可注音句仍进入准确率分母，另外报告覆盖子集。

此次冻结前逐条审阅了 100 条标注（哈希抽样，60 条含常见多音字、另 40 条），以及
最初 20 条样例。`annotation_corrections.json` 记录“很长时间”“咯”“乐呵呵地”的
读音修正，以及“放血”采用 `xue` 的规范读法选择。其余条目保留自动注音。
这不代表全部一万条都已审校；开发/测试集也未证明与模型训练语料相互独立。

自动流程：

1. 开发集比较 Beam 200/500/1000/2000，选择 Top-10 召回距最优不超过 0.1 个百分点
   的最小宽度。
2. 开发集 Top-10 送 Qwen，扫描 alpha 0..1、步长 0.05，首选准确率相同选较小值。
3. 固定参数后评测测试集，另做只启用单字表的 n-gram 消融。
4. 无其他实验工作进程时，对哈希顺序前 100 句逐键输入、逐键回删计时。

解码分为 `--jobs` 个独立进程，避免模型视图和托管堆争用；Qwen 默认 3 进程、
每个 4 线程，可通过 `--qwen-workers` 调整进程数。模型页由操作系统共享。输入、模型、程序与分片的 SHA256 保存到
manifest；换程序、模型、输入或解码分片数应使用新实验目录，不能混接结果。
未完成的 Qwen 阶段必须按原进程数恢复；完整阶段可复用，后续阶段可以采用不同的
进程数。原生 scorer 始终为每进程 4 线程，耗时需结合当时的并行度解读。
程序和原生 host 应在整个评测期间保持不变。

各步骤写 JSONL，可用同一命令续跑；已成功的记录不重复评分。故障记录只有成功重试
才算完成，聚合会拒绝漏例和重复记录。若需中止，分别创建实验目录的 `STOP` 与
`parts/STOP` 文件；当前句完成后退出。恢复前移走两者。损坏或截断的 JSONL 明确报错，
不静默丢弃记录。

## 输出和单独试句

`REPORT.md` 是简表，`report.json` 包含 Beam/alpha 扫描、Top-1/5/10/50、融合准确率、
字符编辑错误率、救回/退化、前十未召回句子、覆盖率以及延迟分位数。
`dev-*.jsonl`、`test-*.jsonl` 保留候选和切分，
`qwen-*.jsonl` 的分数顺序与对应 n-gram 前十完全一致，`test-fused.jsonl` 保存融合后排序，
`latency.jsonl` 保留逐键数据。
各 worker 日志末尾记录峰值工作集；分进程工作集包含共享页，不能直接求和当作独占内存。
`memory.jsonl` 还记录 Windows 实验进程的工作集峰值和采样私有字节；汇总中的最高值
可能来自较宽 Beam 的开发扫描，不单指最终选定配置。

可以手工创建一行 JSONL，用任意唯一 `id`，例如：

```json
{"id":"manual-1","text":"你好世界","code":"nihaoshijie","split":"test","source":"manual"}
```

然后运行：

```text
TigerClaw.Core.Tests.exe decode TABLE MODEL CASES OUTPUT BEAM 1 words all
TigerClaw.Core.Tests.exe qwen OUTPUT SCORES FullPinyinProbe.exe QWEN_GGUF 0 1
python tools/FullPinyinEval/fuse.py OUTPUT RANKED ALPHA SCORES
```

`text` 仅用于评分目标，不输入解码器；实用试句可将它设为空串。ALPHA 使用开发集选择值。
手工试句可为 `fuse.py` 加 `--allow-fallback`，在缺少 Qwen 结果时保留 n-gram 排序并标记
`fallback: true`；正式评测不启用该选项。当前工具是离线原型，计时不等于输入法
宿主内的端到端响应，Qwen 全句批量耗时也不等于未来增量推理耗时。
