"""Rerun installed Tigirl's equivalent fused trigram on the frozen 20k cases.
Merge with completed char5 reranking predictions, without latency measurements.
"""
from pathlib import Path
import concurrent.futures as F,csv,hashlib,json,shutil,subprocess
R=Path(__file__).resolve().parents[3]
A=Path('/mnt/c/Archive/tigerclaw_sentence_ml')
O=A/'experiments/brightmart-char5-shape-20k-20260922'
W=O/'installed-fused3';W.mkdir(exist_ok=True)
prior=R/'next/_run/original-size-sweep-20260920'
meta=json.loads((prior/'fusion_release-metadata.json').read_text())
def digest(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def dump(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n')
model=A/'experiments/merged-214-20260919/sentence-ngram-mobile.bin'
assert digest(model)==meta['sha256']
installed=Path('/mnt/c/Program Files/Tigirl/versions/8df11f2a1e941942/Models/sentence-ngram-v2.bin')
assert digest(installed)=='de4e925d01cdadf2a7e0a6b7dfff4e9b953cd220c2398eb1b81ab6e5714537db'
cache=Path('/home/yc/tmp/tiger-fused3-shape-20k');cache.mkdir(exist_ok=True)
cached=cache/'sentence-ngram-mobile.bin'
if not cached.exists():shutil.copy2(model,cached)
assert digest(cached)==meta['sha256']
fixture=cache/'fixture';fixture.mkdir(exist_ok=True)
source=R/'next/_run/ngram-214-materialize-20260919/pruned214'
for name,h in meta['input_hashes'].items():
 assert digest(source/name)==h,name
 dst=fixture/name;dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source/name,dst)
(fixture/'models').mkdir(exist_ok=True);link=fixture/'models/sentence-ngram-mobile.bin'
if not link.exists():link.symlink_to(cached)
lines=(O/'cases.tsv').read_text().splitlines(keepends=True)
assert len(lines)==20000 and digest(O/'cases.tsv')==meta['cases_sha256']
exporter=Path(__file__).with_name('export_shape_pool.lua')
def run(i):
 cases=W/f'cases-{i}.tsv';cases.write_text(''.join(lines[i::8]))
 with (W/f'worker-{i}.log').open('w') as log:subprocess.run(['lua',str(exporter),str(fixture),str(cases),str(W/f'pool-{i}.tsv')],stdout=log,stderr=subprocess.STDOUT,check=True)
 print('completed',i,flush=True)
with F.ThreadPoolExecutor(max_workers=8) as executor:list(executor.map(run,range(8)))
groups={}
for i in range(8):
 for x in csv.DictReader((W/f'pool-{i}.tsv').open(),delimiter='\t'):groups.setdefault(x['id'],[]).append(x)
history={r['id']:r for r in csv.DictReader((prior/'fusion_release.tsv').open(),delimiter='\t')}
rows=[json.loads(l) for l in (O/'predictions.jsonl').read_text().splitlines()]
assert len(groups)==len(history)==len(rows)==20000
for r in rows:
 pool=groups[r['id']];p=pool[0];h=history[r['id']]
 assert [p[k] for k in ('id','source','code','target')]==[r[k] for k in ('id','source','code','target')]
 rank=next((int(c['index']) for c in pool if c['text']==r['target']),0)
 assert p['text']==h['top1'] and rank==int(h['rank']),r['id']
 r['predictions']['installed_fused3']=p['text'];r['installed_fused3_rank']=rank
with (W/'predictions-merged.jsonl').open('w') as f:
 for r in rows:f.write(json.dumps(r,ensure_ascii=False)+'\n')
summary={};changes={}
for dataset in ('old','fresh','combined'):
 selected=[r for r in rows if dataset=='combined' or r['source']==dataset];d={'n':len(selected),'methods':{}}
 for name in rows[0]['predictions']:
  rescued=sum(r['predictions'][name]==r['target'] and r['predictions']['installed_fused3']!=r['target'] for r in selected)
  regressed=sum(r['predictions'][name]!=r['target'] and r['predictions']['installed_fused3']==r['target'] for r in selected)
  correct=sum(r['predictions'][name]==r['target'] for r in selected)
  d['methods'][name]=dict(correct=correct,accuracy=correct/len(selected),rescued_vs_installed_fused3=rescued,regressed_vs_installed_fused3=regressed,net_vs_installed_fused3=rescued-regressed)
 summary[dataset]=d
for name in rows[0]['predictions']:
 changes[name]=[dict(id=r['id'],source=r['source'],target=r['target'],code=r['code'],before=r['predictions']['installed_fused3'],after=r['predictions'][name],rescued=r['predictions'][name]==r['target'],regressed=r['predictions']['installed_fused3']==r['target']) for r in rows if (r['predictions'][name]==r['target'])!=(r['predictions']['installed_fused3']==r['target'])]
 dump(W/(name+'-changes.json'),changes[name])
dump(W/'summary-merged.json',summary)
dump(W/'manifest.json',dict(replayed_cases=20000,historical_top1_and_rank_reproduced=True,model=meta,installed_model_sha256=digest(installed),cases_sha256=digest(O/'cases.tsv'),exporter_sha256=digest(exporter),scope='Frozen decoder, installed model equivalent parameters, no user learning or early commit; char5 results retain original full_m5 search pools; no latency evaluation'))
print(json.dumps(summary,ensure_ascii=False,indent=2),flush=True)
