"""Frozen Top-50 rescoring, NOT first-pass higher-order Beam decoding."""
import ctypes as C
import hashlib
import json
import math
from pathlib import Path
import shutil
import time

ROOT=Path('/mnt/c/Archive/tigerclaw_sentence_ml')
OUT=ROOT/'experiments/joint-5gram-rerank-20260921'
BASE=ROOT/'experiments/joint-pinyin-20260921/joint.jsonl'
PAIR=ROOT/'experiments/joint-qwen-wetype-1050-20260921/cases.jsonl'
M3=ROOT/'model-pinyin/models/joint_3gram.klm'
M5=ROOT/'joint_5gram/joint_5gram.klm'
def dump(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n')
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for b in iter(lambda:f.read(8*1024*1024),b''):h.update(b)
 return h.hexdigest()
def distance(a,b):
 d=list(range(len(b)+1))
 for i,x in enumerate(a,1):
  n=[i]
  for j,y in enumerate(b,1):n.append(min(n[-1]+1,d[j]+1,d[j-1]+(x!=y)))
  d=n
 return d[-1]
def percentile(v,p):
 v=sorted(v);return v[round((len(v)-1)*p)]
lib=C.CDLL(str(OUT/'libhigherorder.so'))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_order.argtypes=[C.c_void_p];lib.ho_order.restype=C.c_uint
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_error.restype=C.c_char_p
lib.ho_free.argtypes=[C.c_void_p]
def load(p,order):
 m=lib.ho_load(str(p).encode())
 assert m,lib.ho_error()
 assert lib.ho_order(m)==order
 return m
def score(m,tokens):
 o=C.c_uint();v=lib.ho_score(m,' '.join(tokens).encode(),C.byref(o))*math.log(10)+2*len(tokens)
 assert math.isfinite(v),lib.ho_error()
 return v,o.value
pair={r['id']:r for r in map(json.loads,PAIR.open())}
assert len(pair)==1050
# Independent native state scoring must reproduce frozen trigram candidate scores.
m3=load(M3,3);m5=load(M5,5)
rows=[];max_delta=0;count=0;seen=set();times=[];oov=[0,0]
with (OUT/'results.jsonl').open('x') as output,BASE.open() as source:
 for line in source:
  r=json.loads(line);assert r['id'] not in seen;seen.add(r['id'])
  if r['id'] in pair:assert (r['code'],r['text'])==(pair[r['id']]['code'],pair[r['id']]['text'])
  candidates=[];elapsed=0
  for rank,c in enumerate(r['candidates'],1):
   tokens=[t for s in c['segments'] for t in s['tokens']]
   assert ''.join(t.rsplit('/',1)[0] for t in tokens)==c['text']
   v3,u3=score(m3,tokens);max_delta=max(max_delta,abs(v3-c['score']))
   assert abs(v3-c['score'])<1e-5,(r['id'],rank,v3,c['score'])
   start=time.perf_counter();v5,u5=score(m5,tokens);elapsed+=(time.perf_counter()-start)*1000
   oov[0]+=u3;oov[1]+=u5;count+=1
   candidates.append({'text':c['text'],'oldRank':rank,'score3':v3,'score5':v5,'frequency':c['frequency']})
  candidates.sort(key=lambda c:(-c['score5'],-c['frequency'],c['text'].encode('utf-16-be')))
  old=r['candidates'][0]['text'] if candidates else ''
  new=candidates[0]['text'] if candidates else ''
  rank5=next((i for i,c in enumerate(candidates,1) if c['text']==r['text']),0)
  result={k:r[k] for k in ('id','text','code','consumed')}
  result.update(old=old,new=new,rank3=r['rank'],rank5=rank5,scoreMs=elapsed,candidates=candidates)
  output.write(json.dumps(result,ensure_ascii=False)+'\n')
  rows.append({k:v for k,v in result.items() if k!='candidates'});times.append(elapsed)
  if len(rows)%200==0:output.flush();print('scored',len(rows), 'candidates',count,flush=True)
assert len(rows)==8019 and set(pair)<=seen
lib.ho_free(m3);lib.ho_free(m5)
def summary(rs):
 n=len(rs);ok=lambda r,k:r[k]==r['text'] and r['consumed']==len(r['code'])
 a=sum(ok(r,'old') for r in rs);b=sum(ok(r,'new') for r in rs)
 return dict(n=n,correct3=a,correct5=b,accuracy3=100*a/n,accuracy5=100*b/n,
 rescued=sum(not ok(r,'old') and ok(r,'new') for r in rs),regressed=sum(ok(r,'old') and not ok(r,'new') for r in rs),
 changed=sum(r['old']!=r['new'] for r in rs),
 cer3=100*sum(distance(r['text'],r['old']) for r in rs)/sum(len(r['text']) for r in rs),
 cer5=100*sum(distance(r['text'],r['new']) for r in rs)/sum(len(r['text']) for r in rs),
 recall={str(k):{str(o):sum(0<r['rank'+str(o)]<=k and r['consumed']==len(r['code']) for r in rs) for o in (3,5)} for k in (5,10,50)})
report={'method':'Frozen trigram Beam200 Top50 token-path rescoring; 5gram BOS/EOS, ln score +2 per character; no fusion, no LLM; not 5gram Beam search',
 'full':summary(rows),'paired1050':summary([r for r in rows if r['id'] in pair]),
 'verification':{'candidateCount':count,'maxTrigramScoreDifference':max_delta,'candidateTokenOovOccurrences3And5':oov},
 'timing':{'scope':'Sequential first traversal, 5gram native scoring + token encoding only; excludes decoding, model loading and JSON; includes page faults; not UI latency', 'p50Ms':percentile(times,.5),'p95Ms':percentile(times,.95),'p99Ms':percentile(times,.99)},
 'limitations':['Training/test disjointness and same training corpus/preprocessing have not been independently verified.','Frozen candidates deduplicate text; alternative pronunciations and candidates pruned by trigram are not recovered.','Exact target matching is not semantic acceptability.']}
dump(OUT/'report.json',report)
dump(OUT/'changes.json',[r for r in rows if r['old']!=r['new']])
print(json.dumps(report,ensure_ascii=False,indent=2),flush=True)
manifest={str(p):{'bytes':p.stat().st_size,'sha256':sha(p)} for p in (BASE,PAIR,M3,M5,OUT/'libhigherorder.so',Path(__file__),Path(__file__).with_name('score.cpp'))}
dump(OUT/'manifest.json',manifest)
for name in ('run.py','score.cpp'):shutil.copy2(Path(__file__).with_name(name),OUT/name)
