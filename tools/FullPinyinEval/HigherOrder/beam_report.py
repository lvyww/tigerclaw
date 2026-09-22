import json,math,hashlib
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';B=R/'experiments/joint-pinyin-20260921'
def edit(a,b):
 v=list(range(len(b)+1))
 for i,x in enumerate(a,1):
  w=[i]
  for j,y in enumerate(b,1):w.append(min(w[-1]+1,v[j]+1,v[j-1]+(x!=y)))
  v=w
 return v[-1]
expected={r['id']:r for r in map(json.loads,(B/'cases.jsonl').open()) if r['split']=='test'}
paired={r['id'] for r in map(json.loads,(R/'experiments/joint-qwen-wetype-1050-20260921/cases.jsonl').open())}
def load(path):
 rows={}
 for r in map(json.loads,path.open()):
  assert r['id'] not in rows and r['id'] in expected
  c=expected[r['id']];assert (c['text'],c['code'])==(r['text'],r['code'])
  top=r['candidates'][0]['text'] if r['candidates'] else ''
  rank=next((i for i,x in enumerate(r['candidates'],1) if x['text']==r['text']),0)
  rows[r['id']]={'target':r['text'],'code':r['code'],'top':top,'rank':rank,'complete':r['consumed']==len(r['code']),'ms':r['ms']}
 assert set(rows)==set(expected)
 return rows
def summary(rows):
 n=len(rows);return {'n':n,'correct':sum(r['complete'] and r['rank']==1 for r in rows.values()),'accuracy':100*sum(r['complete'] and r['rank']==1 for r in rows.values())/n,'cer':100*sum(edit(r['target'],r['top']) for r in rows.values())/sum(len(r['target']) for r in rows.values()),'recall':{str(k):sum(r['complete'] and 0<r['rank']<=k for r in rows.values()) for k in [5,10,50]}}
def compare(a,b):
 rescue=[];regress=[];changes=[]
 for key,x in a.items():
  y=b[key];good=lambda r:r['complete'] and r['rank']==1
  item={'id':key,'target':x['target'],'code':x['code'],'before':x['top'],'after':y['top']}
  if not good(x) and good(y):rescue.append(item)
  if good(x) and not good(y):regress.append(item)
  if x['top']!=y['top']:changes.append(item)
 return {'rescued':len(rescue),'regressed':len(regress),'changed':len(changes),'net':len(rescue)-len(regress)}, {'rescued':rescue,'regressed':regress,'changed':changes}
models={'3gram':load(B/'joint.jsonl')}
for name in ('full5','q8','pruned','rerank-q8','rerank-pruned'):
 if (O/f'{name}.jsonl').exists():models[name]=load(O/f'{name}.jsonl')
report={'scope':'Direct Beam200 for full5/q8/pruned; frozen trigram Top50 rescoring for rerank-*; exact spelling; no LLM or learning; same inputs/table/tokens/reward and tie breaks','models':{k:{'test':summary(v),'paired1050':summary({i:r for i,r in v.items() if i in paired})} for k,v in models.items()},'comparisons':{}}
for a,b in [('3gram','full5'),('full5','q8'),('q8','pruned'),('3gram','pruned'),('3gram','rerank-q8'),('3gram','rerank-pruned')]:
 if a in models and b in models:
  stat,detail=compare(models[a],models[b]);report['comparisons'][a+'->'+b]=stat
  (O/f'changes-{a}-{b}.json').write_text(json.dumps(detail,ensure_ascii=False,indent=2))
(O/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2));print(json.dumps(report,ensure_ascii=False,indent=2))
