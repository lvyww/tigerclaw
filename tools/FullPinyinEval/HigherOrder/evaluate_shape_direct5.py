"""Full fivegram Beam scoring on frozen old/fresh shape cases. Accuracy only."""
from pathlib import Path
import concurrent.futures as F,csv,json,subprocess,os,hashlib,shutil
repo=Path(__file__).resolve().parents[3]
root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/brightmart-char5-shape-20k-20260922')
out=root/'direct5';local=Path('/home/yc/tmp/tiger-shape-direct5');fixture=local/'fixture'
validation=json.loads((out/'validation.json').read_text());assert validation['trigram_control_exact'] and validation['maximum_score_error']<1e-9
cases=(root/'cases.tsv').read_text().splitlines(keepends=True);assert len(cases)==20000
exporter=Path(__file__).with_name('export_shape_pool.lua')
env=dict(os.environ,SHAPE5_LIB=str(local/'shape5.so'),SHAPE5_MODEL=str(local/'char5-q8.klm'))
def run(i):
 casefile=out/f'cases-{i}.tsv';casefile.write_text(''.join(cases[i::8]))
 with (out/f'worker-{i}.log').open('w') as log:subprocess.run(['lua',str(exporter),str(fixture),str(casefile),str(out/f'pool-{i}.tsv')],env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
 print('complete',i,flush=True)
with F.ThreadPoolExecutor(max_workers=8) as executor:list(executor.map(run,range(8)))
groups={}
for i in range(8):
 for r in csv.DictReader((out/f'pool-{i}.tsv').open(),delimiter='\t'):groups.setdefault(r['id'],[]).append(r)
rows=[json.loads(l) for l in (root/'installed-fused3/predictions-merged.jsonl').read_text().splitlines()]
assert len(groups)==len(rows)==20000
for r in rows:
 pool=groups[r['id']];assert [pool[0][k] for k in ('source','code','target')]==[r[k] for k in ('source','code','target')]
 r['predictions']['direct_fivegram']=pool[0]['text'];r['direct_fivegram_rank']=next((int(c['index']) for c in pool if c['text']==r['target']),0)
with (out/'predictions-merged.jsonl').open('w') as f:
 for r in rows:f.write(json.dumps(r,ensure_ascii=False)+'\n')
summary={}
for dataset in ('old','fresh','combined'):
 subset=[r for r in rows if dataset=='combined' or r['source']==dataset]
 data={'n':len(subset),'methods':{},'pool':{}}
 for name in rows[0]['predictions']:
  data['methods'][name]={'correct':sum(r['predictions'][name]==r['target'] for r in subset)}
  data['methods'][name]['accuracy']=data['methods'][name]['correct']/len(subset)
 for k in (5,20):
  before=sum(0<r['rank']<=k for r in subset);after=sum(0<r['direct_fivegram_rank']<=k for r in subset)
  gained=[r['id'] for r in subset if not 0<r['rank']<=k and 0<r['direct_fivegram_rank']<=k]
  lost=[r['id'] for r in subset if 0<r['rank']<=k and not 0<r['direct_fivegram_rank']<=k]
  data['pool'][str(k)]=dict(trigram=before,fivegram=after,gained=gained,lost=lost)
 data['comparisons']={}
 for baseline in ('baseline','installed_fused3','fivegram_top5','fusion_half_top5'):
  rescued=sum(r['predictions']['direct_fivegram']==r['target'] and r['predictions'][baseline]!=r['target'] for r in subset)
  regressed=sum(r['predictions']['direct_fivegram']!=r['target'] and r['predictions'][baseline]==r['target'] for r in subset)
  data['comparisons'][baseline]=dict(rescued=rescued,regressed=regressed,net=rescued-regressed)
 summary[dataset]=data
for baseline in ('baseline','installed_fused3','fivegram_top5','fusion_half_top5'):
 changed=[dict(id=r['id'],source=r['source'],code=r['code'],target=r['target'],before=r['predictions'][baseline],after=r['predictions']['direct_fivegram'],rescued=r['predictions']['direct_fivegram']==r['target'],regressed=r['predictions'][baseline]==r['target']) for r in rows if (r['predictions'][baseline]==r['target'])!=(r['predictions']['direct_fivegram']==r['target'])]
 (out/(baseline+'-changes.json')).write_text(json.dumps(changed,ensure_ascii=False,indent=2)+'\n')
(out/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n')
def digest(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
manifest=dict(cases_sha256=digest(root/'cases.tsv'),model_sha256=validation['model_hash'],native_sha256=digest(local/'shape5.so'),patched_decoder_sha256=digest(fixture/'lua/tiger_sentence.lua'),baseline_decoder_sha256=digest(Path('/home/yc/tmp/tiger-char5-shape-20k/fixture/lua/tiger_sentence.lua')),validation=validation,method='Fivegram full opaque state propagated on every Beam path, per-character expansion and EOS. Same frozen Beam limits/eligibility/rank/word priors. Trigram retained only for original observed-bigram isolation prior, never for search LM probability.',latency_evaluated=False,llm=False,production_changed=False)
(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
for filename in ('shape5_lua.cpp','patch_shape5.py','test_shape5.lua','evaluate_shape_direct5.py'):shutil.copy2(Path(__file__).with_name(filename),out/filename)
shutil.copy2(fixture/'lua/tiger_sentence.lua',out/'tiger_sentence_fivegram.lua')
print(json.dumps(summary,ensure_ascii=False,indent=2),flush=True)
