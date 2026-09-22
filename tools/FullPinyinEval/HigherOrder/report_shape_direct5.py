from pathlib import Path
import json,csv,shutil
root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/brightmart-char5-shape-20k-20260922')
out=root/'direct5';s=json.loads((out/'summary.json').read_text())
labels={'installed_fused3':'虎娘同源融合三阶','baseline':'m5 三阶搜索','fivegram_top5':'m5 搜索＋五阶重排 Top5','fusion_half_top5':'m5 搜索＋三五阶各半重排 Top5','direct_fivegram':'五阶直接搜索'}
lines=['# 虎整句五阶直接搜索：原两万句评测','','2026-09-22；离线实验，无 LLM、无学习、无提前上屏，不做延迟对比、不部署。','',
'## 首选命中','','| 方案 | 旧集 / 10000 | 新集 / 10000 | 合计 / 20000 | 命中率 |','|---|---:|---:|---:|---:|']
for name,label in labels.items():lines.append(f"| {label} | {s['old']['methods'][name]['correct']} | {s['fresh']['methods'][name]['correct']} | {s['combined']['methods'][name]['correct']} | {s['combined']['methods'][name]['accuracy']*100:.3f}% |")
lines+=['','## 五阶直接搜索相对其他方案','','| 对照方案 | 救回 | 退步 | 净增 |','|---|---:|---:|---:|']
for name,v in s['combined']['comparisons'].items():lines.append(f"| {labels[name]} | {v['rescued']} | {v['regressed']} | {v['net']:+d} |")
lines+=['','## 正确答案入池','','| 候选池 | m5 三阶搜索 | 五阶直接搜索 | 新入池 | 掉出池 |','|---|---:|---:|---:|---:|']
for k,v in s['combined']['pool'].items():lines.append(f"| Top{k} | {v['trigram']} | {v['fivegram']} | {len(v['gained'])} | {len(v['lost'])} |")
lines+=['','## 方法与验证','','在原冻结 Lua 解码器的隔离副本中接入 KenLM：每条 Beam 路径保存完整不透明五阶','状态，每扩展一个字立即以五阶概率评分，EOS 也使用该路径的五阶状态。不是末尾重排。','路径去重仍按完整输出文本，具有相同文本的路径共享相同语言模型历史；不按最后两个','字合并。缓存以完整状态＋目标字为键，并保持有界容量。锁定前缀重建相同模型状态。','',
'保持原 Beam：编码位置不超过24时宽200，之后宽48，最多20条输出。码表、选重、','字数奖励、主码奖励、补充词表、词汇奖励等规则不变。三阶只保留为原孤立字惩罚','所需的观察二元组来源，不参与搜索语言概率；这使本次只改变搜索语言模型。','这是五阶搜索原型，尚非独立只带一个模型文件的可安装产品。','',
'验证：关闭适配器时20条样本的339个候选及分数逐项复现原三阶；五阶逐字符累计','与独立整句评分20条一致，最大误差2.85e-14；增量/回删/锁定测试666项通过。','整批20000条原样保留，空候选亦计入分母。原运行目录与安装模型没有修改。','',
'模型：brightmart-char5-20260922/char5-q8.klm；SHA256','`f13b5b6b61ef1440d3363510730b8fe122d727570cefbcc8a372bd42cadb1f70`。','输入SHA256 `d46d90a24bacf8c43d5c9713eb519062a23441b31e1c5fed45beb031e192372a`。','',
'沿用此前数据边界：4814条目标与新模型训练文本整行相同，不能当独立留出集。','不同训练语料/预处理与模型阶数的收益无法由本次比较拆开。实际应用输入、提前上屏','置信度、学习和资源生命周期未在本次原型中做产品验收。','',
'## 产物','','同实验目录 direct5/ 下保存分片候选、predictions-merged.jsonl、summary.json、','相对各基线的changes.json、验证与模型/代码哈希、patched Lua和适配器源码。',
'复现入口：shape5_lua.cpp、patch_shape5.py、test_shape5.lua、evaluate_shape_direct5.py。']
text='\n'.join(lines)+'\n';Path(__file__).with_name('DIRECT5_SHAPE_20K.md').write_text(text);(out/'REPORT.md').write_text(text)
rows=[json.loads(l) for l in (out/'predictions-merged.jsonl').read_text().splitlines()]
with (out/'merged-comparison.csv').open('w',encoding='utf-8-sig',newline='') as f:
 writer=csv.writer(f);writer.writerow(['id','dataset','code','target',*labels,'m5_rank','direct5_rank'])
 for r in rows:writer.writerow([r['id'],r['source'],r['code'],r['target'],*(r['predictions'][k] for k in labels),r['rank'],r['direct_fivegram_rank']])
shutil.copy2(__file__,out/Path(__file__).name)
print(text)
