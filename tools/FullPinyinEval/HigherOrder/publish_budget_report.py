"""Write the reviewable final artifact after verification, preserving all raw results."""
import json
import shutil
from pathlib import Path
from budget500 import O, E, B, R, REPO, sha, dump

report = json.loads((O/'report.json').read_text())
selection = report['selection']
assert selection['primary'] == 'fusion'
assert 'FINAL VALIDATION COMPLETE' in (O/'logs/final-validation.log').read_text()
stats = report['models']
labels = {'trigram': '原三阶', 'q8': '量化三阶 Q8', 'single': '保留低阶记录的剪枝五阶 Q6', 'fusion': '三阶 Q8＋小五阶 Q8 混合重排'}
sizes = {'trigram': 474049472, 'q8': 227091255, 'single': 491110631, 'fusion': 466340652}
lines = ['# 虎爪全拼：500 MB 预算实验（2026-09-22）', '',
         '**已达成模型文件合计 ≤500,000,000 字节；未达到原文命中率 ≥60% 的期望目标。**', '',
         '本轮开发集选中三阶 Q8 搜索＋小五阶 Q8 混合重排，模型合计 **466,340,652 字节（466.34 MB）**。',
         '交付目录 `delivery-fusion/` 含两份模型、MANIFEST 和接入说明。没有安装或更改生产解码器、用户记录。', '',
         '## 同一 8,019 条完整测试', '',
         '|方案|模型合计 MB|原文命中|命中率|CER↓|相同微信子集 1,050 条命中|',
         '|---|---:|---:|---:|---:|---:|']
for name in ('trigram', 'q8', 'single', 'fusion'):
    s = stats[name]
    lines.append(f"|{labels[name]}|{sizes[name]/1e6:.2f}|{s['correct']}|{s['accuracyPercent']:.3f}%|{s['cerPercent']:.3f}%|{s['paired1050Correct']}|")
lines += ['|上轮原三阶＋完整五阶 Q8 重排|2481.68|4890|60.980%|5.029%|714|', '',
          '全部模型不使用 LLM、学习或模糊音。MB 在本报告统一为十进制；文件大小不等于进程 RSS、安装包或整个应用大小。',
          '微信子集沿用旧采集 ID，没有重跑商业输入法。原文匹配不等于语义唯一正确；训练与测试互斥仍未独立证明。', '',
          '## 成对变化与取舍', '']
for name in ('q8', 'single', 'fusion'):
    s = stats[name]
    lines.append(f"- {labels[name]}相对原三阶：救回 {s['rescued']}、退化 {s['regressed']}、净增 {s['correct']-4321}；首选变化 {s['changed']}。")
lines += [f"- 混合方案比上轮 2.48 GB 方案少命中 {4890-stats['fusion']['correct']} 句；本轮不能宣称 500 MB 保住了完整五阶的效果。", '',
          '## 固定开发集筛选', '',
          '原开发集 1,981 条。按开发集首选命中最多、同分体积更小选择；五阶混合权重同分时取更小值。',
          '混合方案在运行测试集前固定 alpha=0.625。单模型候选筛选完成后写入 dev-selection.json，再运行其测试集。', '',
          '|候选|实际字节数|开发集命中|', '|---|---:|---:|']
for name, s in selection['singleCandidates'].items():
    lines.append(f"|{name}|{s['bytes']}|{s['correct']}|")
lines += ['|三阶 Q8|227091255|1078|', '|三阶 Q8＋wd1e8，纯五阶评分|466340652|1010|',
          '|三阶 Q8＋wd1e8，alpha=0.625|466340652|1151|', '',
          '完整权重网格见 fusion-dev-selection.json。超预算版本仅用于确定体积范围，没有进入测试集选优。', '',
          '## 方法与复现', '',
          '- 三阶：KenLM `-q 8 -b 8 -a 64 trie`，不剪枝。',
          '- 五阶：IRSTLM absolute weighted-difference pruning；这是加权差异法，不是精确相对熵剪枝，也不是上轮上下文词表裁剪。',
          '- 混合方案的五阶：二至五阶阈值均为 `1e-8`，再 Q8。最终分数 `0.375 * 三阶 + 0.625 * 五阶`。',
          '- 单五阶：阈值 `-1,-1,4e-9,4e-9`，保留全部一至三阶记录，只裁剪四五阶；概率和回退权重均 6-bit。',
          '- 三阶搜索 Beam200、Top50，BOS/EOS、自然对数、每字 +2、原词频和 UTF-16 同分顺序保持一致。',
          '- 新组合入口只存在于 combo-v2-source/ 和 combo-v2-bin/；生产 C# Core/桥接器未改。',
          '- IRSTLM 来源提交、工具补丁和初始约束见 PLAN.json、irstlm-build.patch；脚本快照在 tools/。', '',
          'IRSTLM 需要前缀连续的条目顺序；先对 ARPA 做不改概率文本的排序，逐阶计数核对，然后用 5,000 条路径对照原 KenLM。',
          '各次剪枝前最大 log10 分差约 1.06e-6。IRSTLM 重算回退权重；KenLM 可能补齐索引所需记录，因此最终文件可能比头部估算大。',
          '运行时不能直接把五阶覆盖到生产三阶模型路径，需要接入完整五阶评分及已选定的混合权重。', '',
          '## 逐键性能', '',
          '固定同一 30 句，所有批量构建和解码结束后，各方案单进程依次运行。单位 ms。',
          '包括解码、增量缓存和初始 JIT；混合方案还包括实际候选编码、五阶评分、加权与排序。排除模型加载、哈希、IPC 和 UI。',
          '这是 Linux ARM64 离线原型，不是 Windows 应用验收；组合延迟为直接测量，不是相加两个分位数。', '',
          '这是一次固定顺序的小样本计时；混合方案某个分位数低于三阶不构成其更快的证据。', '',
          '|方案|追加 P50|追加 P95|追加 P99|回删 P95|', '|---|---:|---:|---:|---:|']
for name in ('q8', 'single', 'fusion'):
    t = json.loads((O/(name+'-latency-summary.json')).read_text())['stats']
    lines.append(f"|{labels[name]}|{t['append']['p50Ms']:.3f}|{t['append']['p95Ms']:.3f}|{t['append']['p99Ms']:.3f}|{t['backspace']['p95Ms']:.3f}|")
lines += ['', '## 验证与反馈', '',
          '- 合成模型通过 62 项上下文归一化、无剪枝概率等价和实际剪枝检查。',
          '- 三阶 Q8：983 项增量/完整 Top50 对照、400 个锁定前缀独立评分通过。单五阶胜者也执行同样检查。',
          '- 各最终方案 100 句/5,000 候选对照独立完整状态评分；混合方案检查两个模型和权重后的分数。',
          '- 初版组合入口使用相同三阶进行重排时，100 句 Top50 路径/顺序与未重排一致，分差仅浮点舍入。',
          '- 最终混合方案与单五阶都把“要优化好久”“要优化很久”排第一。',
          '- “现在有话没地方说去”：混合方案仍排“现在优化没地方说去”；单五阶在这条反馈中正确。',
          '- `xyouhua` 含简拼，在本次严格全拼评测设置下不完整消费；不能将该反馈当作模型错词。',
          '- 反馈不参与开发集/测试集统计，详情见 feedback-report.json。', '',
          '失败记录保留但未纳入结果：初次不兼容 ARPA 顺序的加载、WSL Windows 盘大块读取 ENOMEM、一次哈希不一致的快速复制。',
          '后续使用核验过的 Linux 本地输入，复制目标逐字节哈希对照；错误副本以 failed 命名并排除。', '',
          '下一步应验证 Windows 端双模型接入、取消/过期结果保护和真实打字延迟；本轮交付只证明离线效果与模型体积。', '']
(O/'REPORT.md').write_text('\n'.join(lines))

names = ['budget500.py','build_budget_variant.py','evaluate_budget_dev.py','arpa_sort.cpp','prune500.cpp',
         'test_budget_pruner.py','BudgetReranker.cs','prepare_budget_combo.py','fuse_budget.py',
         'budget_verify.py','budget_feedback.py','finish_budget_validation.py','budget_report.py','publish_budget_report.py']
for name in names:
    shutil.copy2(Path(__file__).with_name(name), O/'tools'/name)
manifest = {}
paths = list((O/'tools').glob('*')) + list((O/'delivery-fusion').glob('*'))
paths += [O/'models/high4e9q6.klm', O/'REPORT.md', O/'report.json', O/'dev-selection.json',
          O/'fusion-dev-selection.json', O/'feedback-report.json', O/'PLAN.json',
          O/'selected-arpa-finite-audit.json', O/'combo-identity-check.json',
          O/'logs/pruner-tests.log', O/'logs/single-incremental.log', O/'logs/incremental-q8-final.log',
          O/'combo-v2-bin/Joint.dll', E/'bench-bin/Joint.dll', E/'incremental-bin/Joint.dll',
          E/'bin/libhigherorder.so', E/'bin/libjointkenlm.so', E/'bin/libkenlm.so', E/'bin/build_binary',
          B/'cases.jsonl', B/'tokens.json', REPO/'release/拼音反查码表/拼音.txt']
paths += [O/name for name in ('test3q8.jsonl','test-fusion.jsonl','test-single.jsonl',
                             'dev3q8.jsonl','dev-wd1e8-rerank.jsonl','dev-high4e9q6.jsonl')]
paths += [B/'joint.jsonl', R/'experiments/joint-qwen-wetype-1050-20260921/cases.jsonl']
paths += list((O/'combo-v2-source').glob('*.cs'))
paths += list(O.glob('*-score-check.json')) + list(O.glob('*-latency-summary.json'))
for p in paths:
    manifest[str(p)] = {'bytes': p.stat().st_size, 'sha256': sha(p)}
dump(O/'manifest.json', manifest)
print('REPORT AND MANIFEST COMPLETE', flush=True)
