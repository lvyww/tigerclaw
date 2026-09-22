"""Write the merged accuracy report from completed, checked predictions."""
from pathlib import Path
import csv,json,shutil
root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/brightmart-char5-shape-20k-20260922')
w=root/'installed-fused3';s=json.loads((w/'summary-merged.json').read_text())
names={'installed_fused3':'虎娘同源融合三阶（原模型＋mohu）','baseline':'full-kn-m5-v2 三阶','fivegram_top5':'m5 三阶搜索＋五阶评分重排 Top5','fusion_half_top5':'m5 三阶搜索＋三/五阶各半重排 Top5'}
lines=['# 两万句合并对比：加入本机虎娘同源融合三阶','','2026-09-22。旧集、新集各 10000 句，保持同一冻结解码器、码表和输入编码。','融合三阶重新解码全部两万句，逐条首选和目标排名与历史结果一致。无 LLM、','无学习、无提前上屏，不测延迟，不部署。','',
'## 首选命中','','| 方案 | 旧集正确 | 新集正确 | 合计正确 | 合计命中率 |','|---|---:|---:|---:|---:|']
for k,label in names.items():lines.append(f"| {label} | {s['old']['methods'][k]['correct']} | {s['fresh']['methods'][k]['correct']} | {s['combined']['methods'][k]['correct']} | {s['combined']['methods'][k]['accuracy']*100:.3f}% |")
lines+=['','## 相对虎娘同源融合三阶','','| 方案 | 旧集救回/退步 | 新集救回/退步 | 合计救回/退步 | 净增 |','|---|---:|---:|---:|---:|']
for k,label in names.items():
 if k=='installed_fused3':continue
 values=[]
 for ds in ('old','fresh','combined'):
  m=s[ds]['methods'][k];values.append(f"{m['rescued_vs_installed_fused3']} / {m['regressed_vs_installed_fused3']}")
 lines.append('| '+label+' | '+' | '.join(values)+f" | {s['combined']['methods'][k]['net_vs_installed_fused3']:+d} |")
lines+=['','五阶各半融合相对虎娘同源融合三阶净增 78 句（+0.390 个百分点），错误从','148 降至 70，减少 52.70%。旧集净增 66、新集净增 12。这里的救回/退步为','97/19，不能与以 m5 三阶为基线的 42/5 混用。','',
'虎娘同源融合三阶在新集比 m5 多对 6 条，在旧集少对 47 条；两者存在互有胜负。','“马云雷军入选大亨名单”在融合三阶中本来就正确，未计作对它的救回。','',
'## 模型与范围','','实际安装模型：`C:\\Program Files\\Tigirl\\versions\\8df11f2a1e941942\\Models\\sentence-ngram-v2.bin`，','272600424 字节，SHA256 `de4e925d01cdadf2a7e0a6b7dfff4e9b953cd220c2398eb1b81ab6e5714537db`。','已通过注册项、实际 QQ/Firefox 映射和文件哈希核实。此次评测读取其同源无损移动格式','`merged-214-20260919/sentence-ngram-mobile.bin`（SHA256','`23216acd8319885aa2431ffbf2231dab4677c5d4abb55a08a404450a15b865ca`）。','',
'为了比较模型，使用与此前评测相同的冻结 Rime 解码器，不是直接驱动虎娘已安装 DLL。','所有五阶行仍沿用前次 m5 三阶搜索得到的候选池；没有给融合三阶候选池另做五阶重排，','也没有测试五阶直接搜索。Top20 的命中数与相应 Top5 行相同。','',
'测试原句中有 4814 条与新五阶训练文本整行重合，历史集并非独立留出集；不同模型','训练数据也不同，不能把收益全部解释为阶数收益。详见前次 CHAR5_SHAPE_20K.md。','',
'## 证据','','`installed-fused3/` 下保存重跑候选、逐条合并结果、各方案相对融合三阶的救回/退步、','summary-merged.json、manifest.json。CSV 为 merged-comparison.csv。','复现入口：compare_shape_fused3.py；报告入口：report_shape_fused3.py。']
text='\n'.join(lines)+'\n'
Path(__file__).with_name('FUSED3_COMPARISON.md').write_text(text);(w/'REPORT.md').write_text(text)
rows=[json.loads(l) for l in (w/'predictions-merged.jsonl').read_text().splitlines()]
with (w/'merged-comparison.csv').open('w',encoding='utf-8-sig',newline='') as f:
 writer=csv.writer(f);writer.writerow(['id','dataset','code','target',*names])
 for r in rows:writer.writerow([r['id'],r['source'],r['code'],r['target'],*(r['predictions'][k] for k in names)])
for filename in ('compare_shape_fused3.py','report_shape_fused3.py'):shutil.copy2(Path(__file__).with_name(filename),w/filename)
print(text)
