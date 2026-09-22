"""Historical Tiger shape-code candidate feasibility probe; no deployment or LLM.
Tune a single fusion coefficient on the old 50k set; evaluate once on the
record/text-disjoint confirmation 50k. Joint readings are marginalized by max
whole-sentence score, never selected from target text or target pronunciation.
"""
import ctypes as C
from functools import lru_cache
from itertools import product
import hashlib,json,math,time
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml')
OUT=R/'experiments/tiger-shape-fivegram-20260922'
OUT.mkdir(exist_ok=False)
LIB=R/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'
M3=R/'model-pinyin/models/joint_3gram.klm'
M5=R/'experiments/joint-5gram-beam-20260921/models/joint5-q8.klm'
TOKENS=R/'experiments/joint-pinyin-20260921/tokens.json'
lib=C.CDLL(str(LIB))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_free.argtypes=[C.c_void_p]
models=[lib.ho_load(str(p).encode()) for p in (M3,M5)];assert all(models)
readings={}
for row in json.loads(TOKENS.read_text()):
 for t in row['tokens']:readings.setdefault(t.rsplit('/',1)[0],set()).add(t)
readings={k:sorted(v) for k,v in readings.items()}
scored=0;max_combinations=0
@lru_cache(maxsize=250000)
def scores(text):
 global scored,max_combinations
 options=[readings.get(c,[c]) for c in text]
 combinations=math.prod(map(len,options));max_combinations=max(max_combinations,combinations)
 assert combinations<=100000,('unbounded reading combinations',text,combinations)
 best=[-math.inf,-math.inf];oovbest=999
 for ts in product(*options):
  buf=' '.join(ts).encode()
  for i,m in enumerate(models):
   o=C.c_uint();v=lib.ho_score(m,buf,C.byref(o))*math.log(10)+2*len(text)
   assert math.isfinite(v)
   best[i]=max(best[i],v);oovbest=min(oovbest,o.value)
  scored+=1
 return best,oovbest,combinations

def predict(row,alpha,order):
 pool=row['pool'][:5]
 if not pool:return ''
 if alpha==0:return row['base_top']
 def key(pair):
  i,c=pair;score=(1-alpha)*c['score']+alpha*row['joint_scores'][i][order]
  # Preserve the existing shape-code eligibility/rank precedence.
  return (-score,c['rank'],c['text'].encode('utf-16-be')) if row['prefer_score'] else (c['rank'],-score,c['text'].encode('utf-16-be'))
 return min(enumerate(pool),key=key)[1]['text']

def load(name,directory):
 rows=[];start=time.monotonic()
 with (OUT/f'{name}-scores.jsonl').open('x') as sink:
  for worker in (0,1):
   for line in (directory/f'rows-{worker}.jsonl').open():
    row=json.loads(line)
    if 'prefer_score' not in row:
     # Older rows omit the flag. Verify score-first reproduces their recorded
     # original production Qwen result before using that comparator.
     pool=row['pool'][:5]
     if pool:
      assert max(pool,key=lambda c:c['score'])['text']==row['base_top']
      if len(pool)>1:
       assert max(enumerate(pool),key=lambda ic:ic[1]['score']+row['weight']*row['qwen_scores'][ic[0]])[1]['text']==row['qwen_top']
     row['prefer_score']=True
    values=[scores(c['text']) for c in row['pool'][:5]]
    row['joint_scores']=[v[0] for v in values];row['joint_oov']=[v[1] for v in values]
    sink.write(json.dumps(row,ensure_ascii=False)+'\n');rows.append(row)
    if len(rows)%10000==0:print(name,len(rows),round(time.monotonic()-start,1),'s',flush=True)
 assert len(rows)==50000 and len({r['id'] for r in rows})==50000
 return rows

def summary(rows,alpha,order):
 base=correct=rescue=regress=oov=0;bylength={};examples=[]
 for r in rows:
  before=r['base_top'];after=predict(r,alpha,order);a=before==r['text'];b=after==r['text']
  base+=a;correct+=b;rescue+=b and not a;regress+=a and not b;oov+=any(r['joint_oov'])
  d=bylength.setdefault(r['length'],{'n':0,'base':0,'new':0});d['n']+=1;d['base']+=a;d['new']+=b
  if a!=b and len(examples)<20:examples.append({k:r[k] for k in ('id','text','code')}|{'before':before,'after':after,'rescued':b})
 n=len(rows);net=rescue-regress;se=math.sqrt((rescue+regress)/n-(net/n)**2)/math.sqrt(n)
 return dict(n=n,alpha=alpha,order=order+3 if order==0 else 5,base=base,correct=correct,rescued=rescue,regressed=regress,
             gain_pp=100*net/n,paired_95_ci_pp=[100*(net/n-1.96*se),100*(net/n+1.96*se)],
             rows_with_candidate_oov=oov,bylength=bylength,examples=examples)

dev=load('development',R/'baseline/qwen-length-arm64-20260908')
grid=[i/20 for i in range(21)]
selected=[];sweep={}
for order in (0,1):
 values=[sum(predict(r,a,order)==r['text'] for r in dev) for a in grid]
 best=max(range(len(grid)),key=lambda i:(values[i],-grid[i]));selected.append(grid[best]);sweep[str(order)]={'alphas':grid,'correct':values}
(OUT/'development-selection.json').write_text(json.dumps({'selected':selected,'sweep':sweep},indent=2))
print('frozen dev selection',selected,flush=True)
test=load('confirmation',R/'baseline/qwen-confirmation-20260908')
assert not ({r['text'] for r in dev}&{r['text'] for r in test}), 'target-text overlap'
results={'method':'Historical frozen Tiger shape-code Top5; original rank precedence; max over joint pronunciations; ln LM +2/Unicode character; no Qwen',
 'development': [summary(dev,a,i) for i,a in enumerate(selected)],
 'confirmation': [summary(test,a,i) for i,a in enumerate(selected)],
 'confirmation_pure': [summary(test,1,i) for i in (0,1)],
 'candidate_sequences_scored':scored,'maximum_reading_combinations':max_combinations,
 'limitations':['2026-09-08 frozen shape-code pools and baseline, not latest 2026-09-20 model/search.',
 '2..6-character context-free snippets, not long sentence or real typing acceptance.',
 'Fivegram/trigram training corpus identity and exclusion of these holdouts are not independently established.',
 'Max pronunciation is a feasibility adapter; production shape-code should train a matching character fivegram.',
 'Pure joint models also change tokenization/training; difference versus original shape baseline is not solely n-gram order.']}
(OUT/'report.json').write_text(json.dumps(results,ensure_ascii=False,indent=2)+'\n')
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  while b:=f.read(1024*1024):h.update(b)
 return h.hexdigest()
paths=[LIB,M3,M5,TOKENS,Path(__file__)]+[R/f'baseline/{d}/rows-{w}.jsonl' for d in ['qwen-length-arm64-20260908','qwen-confirmation-20260908'] for w in (0,1)]
(OUT/'manifest.json').write_text(json.dumps({str(p):{'bytes':p.stat().st_size,'sha256':sha(p)} for p in paths},indent=2))
for m in models:lib.ho_free(m)
print(json.dumps({k:v for k,v in results.items() if k.startswith('confirmation')},ensure_ascii=False,indent=2),flush=True)
