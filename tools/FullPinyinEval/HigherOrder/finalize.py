import json,hashlib,shutil,subprocess
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';repo=Path(__file__).resolve().parents[3]
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for b in iter(lambda:f.read(8*1024*1024),b''):h.update(b)
 return h.hexdigest()
assert (O/'latency-report.json').exists() and (O/'compact-rescore.done').exists()
for name in ('full5-0','q8','pruned'):
 v=json.loads((O/(name+'-score-verification.json')).read_text())
 assert v['order']==5 and v['candidates']==5000 and v['maxScoreDelta']<1e-8
assert 'PASS 400 locked-prefix candidates' in (O/'logs/incremental-pruned-tests.log').read_text()
for name in ('q8','pruned'):
 (O/f'rerank-{name}.jsonl.manifest.json').write_text(json.dumps({'kind':'frozen trigram Top50 rescoring','rows':8019,'timingMeasured':False,'msField':'Compatibility placeholder 0; not an elapsed-time measurement. Use latency-report.json for measured performance.'},indent=2))

subprocess.run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_report.py')],check=True,stdout=(O/'logs/report-final.log').open('w'))
subprocess.run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/feedback_models.py')],check=True,stdout=(O/'logs/feedback.log').open('w'))
report=json.loads((O/'report.json').read_text());lat=json.loads((O/'latency-report.json').read_text());selection=json.loads((O/'dev-selection.json').read_text())
paths={'3gram':R/'model-pinyin/models/joint_3gram.klm','full5':R/'joint_5gram/joint_5gram.klm','q8':O/'models/joint5-q8.klm','pruned':O/f"models/joint5-context{selection['selected']}-q8.klm"}
labels={'3gram':'3-gram Beam200','full5':'完整 5-gram Beam200','q8':'5-gram Q8 Beam200','pruned':f"上下文{selection['selected']}剪枝＋Q8 Beam200",'rerank-q8':'3-gram Top50 → 5-gram Q8 重排','rerank-pruned':'3-gram Top50 → 剪枝 Q8 重排'}
s='# 五阶直接搜索、量化与上下文剪枝对照\n\n本轮所有正式指标来自同一 8,019 条冻结 test；不使用 LLM、学习或模糊音。首选正确要求完整消费输入。没有部署或更改安装版。\n\n| 方案 | 首选正确 | 原文命中率 | CER↓ | Top-50 召回 | 评分模型体积 |\n|---|---:|---:|---:|---:|---:|\n'
for name,info in report['models'].items():
 r=info['test'];key=name.removeprefix('rerank-');p=paths[key]
 size=f'{p.stat().st_size/2**30:.3f} GiB' if name!='3gram' else f'{p.stat().st_size/2**20:.2f} MiB'
 s+=f"| {labels[name]} | {r['correct']}/{r['n']} | {r['accuracy']:.3f}% | {r['cer']:.3f}% | {r['recall']['50']} | {size} |\n"
s+='\n重排方案另需原三阶模型（452.09 MiB），且不恢复其 Beam 剪掉的候选。上一轮未量化五阶重排为 4,899/8,019（61.092%），模型 4.588 GiB。\n\n## 成对变化\n\n'
for key,r in report['comparisons'].items():s+=f"- {key}：救回 {r['rescued']}、退化 {r['regressed']}，净增 {r['net']}；首选变化 {r['changed']}。\n"
s+='\n## 相同微信采集子集（1,050 条）\n\n'
for name,info in report['models'].items():
 r=info['paired1050'];s+=f"- {labels[name]}：{r['correct']}/1050（{r['accuracy']:.3f}%）。\n"
s+='\n以上沿用旧采集 ID，没有重新运行微信、搜狗等黑盒测试。原文匹配不等于语义合理性，合理同音表达也会算不匹配。\n\n## 开发集选档\n\n'
for limit,r in selection['stats'].items():s+=f"- 上下文字音前 {limit}：dev 首选 {r['correct']}/{r['n']}，二进制 {r['bytes']/2**30:.3f} GiB。\n"
s+=f"\n预先固定按开发集首选最多、同分选小模型，选中 {selection['selected']}。不是按测试集结果挑档。这里是上下文整体回退剪枝，不是计数/相对熵逐条剪枝；算法和归一化条件见 BEAM_EXPERIMENT.md。\n\n## 逐键性能\n\n固定 30 条 test、各模型单进程依次运行，全部批量工作退出后测量；同一实验解码器，完整拼写、Beam200。单位 ms：\n\n| 方案 | 追加 P50 | 追加 P95 | 追加 P99 | 回删 P95 |\n|---|---:|---:|---:|---:|\n"
for name,r in lat['models'].items():
 a=r['append'];b=r['backspace'];s+=f"| {labels[name]} | {a['p50Ms']:.3f} | {a['p95Ms']:.3f} | {a['p99Ms']:.3f} | {b['p95Ms']:.3f} |\n"
s+='\n纯重排评分（同一 30 句，每句 50 候选，预编码、预热一次再测三轮）：\n\n'
for name,r in lat['warmRerank'].items():s+=f"- {name}：P50 {r['p50Ms']:.3f} ms，P95 {r['p95Ms']:.3f} ms。\n"
s+='\n纯重排数据不能与完整五阶按键耗时直接相除；不包含搜索、候选生成、模型加载和 UI。评测工具的 SHA256 元数据校验改为 8 MiB 缓冲读取后统一重测四种配置，变化不涉及解码逻辑，旧计时保留在 latency-initial/。完整逐键也只是 Linux ARM64 离线原型，字符串历史适配未优化，不代表已安装 Windows 版按键延迟或私有内存占用。\n\n## 正确性与边界\n\n- 5,246 项原解码检查和 Joint 专项通过；100 句三元 Top50 文本、读音路径、分数完全复现历史结果。\n- 五阶、量化、剪枝后的每个模型，100 句/5,000 候选对照独立原生完整状态评分，误差见各 score-verification.json。\n- 原完整五阶和胜出剪枝量化版各 983 项追加/回删/重输与完整解码 Top50 一致；剪枝量化版另有 400 个锁定前缀候选对照独立原生评分通过。\n- 合成模型验证非零回退权重、概率归一化、完整词表等价及截断输入拒绝。最终 ARPA 逐阶数量完整；初次直接挂载盘读取的失败产物没有计入任何评测。\n- 训练与测试互斥、两个原始模型训练语料一致性尚未独立验证。\n- 五阶仅保留前四个字音，不是任意距离整句语义模型。\n- 搜索与重排差异见 search-vs-rerank.json：完整搜索比未量化重排救回 28、退化 33；其中 31 个退化来自更高分的错误候选，2 个来自较优路径被 Beam 丢弃。\n- 没有修改共享解码器、已安装 Core、用户学习记录或任何原模型文件。\n'
if (O/'RECOMMENDATION.md').exists():s+='\n'+(O/'RECOMMENDATION.md').read_text()
(O/'REPORT.md').write_text(s)
archive=O/'tools';archive.mkdir(exist_ok=True)
for p in Path(__file__).parent.iterdir():
 if p.is_file():shutil.copy2(p,archive/p.name)
shutil.copy2(Path(__file__).with_name('BEAM_EXPERIMENT.md'),O/'BEAM_EXPERIMENT.md')
shutil.copy2('/home/yc/tmp/tiger5-context/input-copy.json',O/'arpa-input-copy.json')
files=list((O/'bin').glob('*'))+list((O/'source').glob('*.cs'))+list(archive.glob('*'))+list((O/'models').glob('*.klm'))+[O/'check100.jsonl',O/'dev-eval.jsonl',O/'bench30.jsonl',paths['3gram'],paths['full5'],R/'experiments/joint-pinyin-20260921/tokens.json',R/'experiments/joint-pinyin-20260921/cases.jsonl',repo/'release/拼音反查码表/拼音.txt']
files+=list((O/'bench-bin').glob('*'))+list((O/'bench-source').glob('*.cs'))+list((O/'incremental-bin').glob('*'))+list((O/'incremental-source').glob('*.cs'))+list(O.glob('*.jsonl'))
manifest={str(p):{'bytes':p.stat().st_size,'sha256':sha(p)} for p in files if p.is_file()}
(O/'manifest.json').write_text(json.dumps(manifest,indent=2))
print('FINAL REPORT',O/'REPORT.md')
