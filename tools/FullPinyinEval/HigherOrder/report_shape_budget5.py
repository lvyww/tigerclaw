"""Report frozen accuracy results and size-only fivegram selection."""
from pathlib import Path
import json,csv,shutil
root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/brightmart-char5-500mb-20260922')
out=root/'evaluation';s=json.loads((out/'summary.json').read_text());model=json.loads((root/'selected.json').read_text())
labels={'installed_fused3':'虎娘同源融合三阶','baseline':'m5 三阶搜索','fivegram_top5':'m5 搜索＋完整五阶重排','fusion_half_top5':'m5 搜索＋三五阶各半重排','direct_fivegram':'完整五阶直接搜索','budget_fivegram':'500 MB 内五阶直接搜索'}
lines=['# 虎整句五阶压缩至 500 MB：原两万句复测','','2026-09-22。纯汉字形码模型，不注音；离线准确率测试，无 LLM、学习、提前上屏或延迟对比。未部署。','',f"交付模型 `{model['delivery_model']}`，{model['bytes']:,} 字节 = {model['bytes']/1e6:.2f} MB = {model['bytes']/2**20:.2f} MiB。",f"相对完整模型 2,032,382,402 字节，文件缩小 {(1-model['bytes']/2032382402)*100:.2f}%。SHA256 `{model['sha256']}`。",'',
'## 首选命中','','| 方案 | 旧集 / 10000 | 新集 / 10000 | 合计 / 20000 | 命中率 |','|---|---:|---:|---:|---:|']
for name,label in labels.items():lines.append(f"| {label} | {s['old']['methods'][name]['correct']} | {s['fresh']['methods'][name]['correct']} | {s['combined']['methods'][name]['correct']} | {s['combined']['methods'][name]['accuracy']*100:.3f}% |")
lines+=['','## 压缩版相对各基线','','| 对照 | 救回 | 退步 | 净增 |','|---|---:|---:|---:|']
for name,v in s['combined']['comparisons'].items():lines.append(f"| {labels[name]} | {v['rescued']} | {v['regressed']} | {v['net']:+d} |")
lines+=['','## 正确答案入池','','| 候选池 | m5 三阶 | 压缩五阶 | 新入池 | 掉出池 |','|---|---:|---:|---:|---:|']
for k,v in s['combined']['pool'].items():lines.append(f"| Top{k} | {v['trigram']} | {v['fivegram']} | {len(v['gained'])} | {len(v['lost'])} |")
lines+=['','## 按预定顺序构建的尺寸','','| 版本 | MB（十进制） | 是否满足上限 |','|---|---:|---|']
for b in json.loads((root/'builds.json').read_text()):lines.append(f"| {b['name']} | {b['bytes']/1e6:.2f} | {'是，进入两万句评测' if b['under500MB'] else '否，未评测准确率'} |")
lines+=['','## 压缩与评测口径','',f"完整保留一至三阶的概率记录。四、五阶只保留历史全部属于 unigram 概率前 {model['context_vocabulary']} 字（另含特殊符号）的上下文分布。被删除分布的回退权重改为 1，保留上下文对后缀封闭。随后以 KenLM {model['quantization_bits']} bit 概率/回退量化和 trie 压缩。词表不删字，低频上下文自动回退。",'',
'这是按词汇范围选择上下文的剪枝，不是熵剪枝，也不保证是同体积下的最优方案。先按 PLAN.md 预定次序仅凭文件大小选模型，再评测；没有用这两万句的命中率调参。builds.json 记录所有尺寸尝试。','',
'五阶完整状态进入每次 Beam 扩展和 EOS，属于直接搜索。沿用上一轮冻结码表、Beam 200/48、最多20候选及原排名先验。三阶模型仍留作原孤立字惩罚的观察二元组来源，不参与搜索语言概率。因此 500 MB 限制只指交付五阶文件，不是当前评测程序总模型大小或内存占用。','',
'剪枝工具93项检查通过。压缩模型独立整句/逐字符评分交叉校验及增量、回删、锁定测试见 evaluation/validation.json、incremental-tests.log。关闭适配器的339候选完全一致对照沿用上一轮未修改适配器/解码器的验证，不冒充本轮重测。','',
'整批20000条原样保留，包括空候选。4814条目标与该五阶训练文本整行相同，本集不能视为独立留出集；剩余目标也未排除子串或来源重合。各模型语料差异与阶数收益无法由本次比较拆开。实际 Windows 输入、提前上屏、学习和生命周期尚未验收。','',
'## 产物','','evaluation/ 保存合并 CSV、逐句 JSONL、全部进步/退步 JSON 与 CSV、候选分片、分组统计、验证及代码/模型哈希。原完整模型和安装目录均未修改。']
report='\n'.join(lines)+'\n';Path(__file__).with_name('BUDGET5_SHAPE_20K.md').write_text(report);(root/'REPORT.md').write_text(report)
rows=[json.loads(l) for l in (out/'predictions-merged.jsonl').read_text().splitlines()]
with (out/'merged-comparison.csv').open('w',encoding='utf-8-sig',newline='') as f:
 w=csv.writer(f);w.writerow(['id','dataset','code','target',*labels,'trigram_rank','full5_rank','budget5_rank'])
 for r in rows:w.writerow([r['id'],r['source'],r['code'],r['target'],*(r['predictions'][k] for k in labels),r['rank'],r['direct_fivegram_rank'],r['budget_fivegram_rank']])
for baseline in s['combined']['comparisons']:
 changes=json.loads((out/(baseline+'-changes.json')).read_text())
 with (out/(baseline+'-changes.csv')).open('w',encoding='utf-8-sig',newline='') as f:
  fields=['id','source','code','target','before','after','rescued','regressed'];w=csv.DictWriter(f,fieldnames=fields);w.writeheader();w.writerows(changes)
for name in ('build_shape500.py','evaluate_shape_budget5.py','report_shape_budget5.py','context_prune.cpp'):shutil.copy2(Path(__file__).with_name(name),root/name)
print(report)
