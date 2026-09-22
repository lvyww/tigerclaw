import ctypes as C,json,math,time,subprocess
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';B=R/'experiments/joint-pinyin-20260921';repo=Path(__file__).resolve().parents[3]
start=time.monotonic()
while not (O/'logs/compact-resume.log').exists() or 'all compact models complete' not in (O/'logs/compact-resume.log').read_text():
 if time.monotonic()-start>14400:raise TimeoutError('compact models')
 time.sleep(5)
selected=json.loads((O/'dev-selection.json').read_text())['selected']
paths={'q8':O/'models/joint5-q8.klm','pruned':O/f'models/joint5-context{selected}-q8.klm'}
for name,model in paths.items():subprocess.run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/validate_beam.py'),str(model),str(O/f'{name}.jsonl')],check=True)
lib=C.CDLL(str(R/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_free.argtypes=[C.c_void_p]
models={k:lib.ho_load(str(v).encode()) for k,v in paths.items()};assert all(models.values())
files={k:(O/f'rerank-{k}.jsonl').open('x') for k in models};o=C.c_uint();count=0
for line in (B/'joint.jsonl').open():
 r=json.loads(line);tokens=[' '.join(t for s in c['segments'] for t in s['tokens']).encode() for c in r['candidates']]
 for name,m in models.items():
  scored=[]
  for i,(c,ts) in enumerate(zip(r['candidates'],tokens,strict=True)):
   v=lib.ho_score(m,ts,C.byref(o))*math.log(10)+2*len(c['text']);assert math.isfinite(v)
   scored.append({'text':c['text'],'score':v,'frequency':c['frequency'],'oldRank':i+1})
  scored.sort(key=lambda c:(-c['score'],-c['frequency'],c['text'].encode('utf-16-be')))
  out={k:r[k] for k in ('id','text','code','consumed')};out['candidates']=scored
  out['rank']=next((i for i,c in enumerate(scored,1) if c['text']==r['text']),0);out['ms']=0
  files[name].write(json.dumps(out,ensure_ascii=False)+'\n')
 count+=1
 if count%1000==0:print('rescored',count,flush=True)
assert count==8019
for f in files.values():f.close()
for m in models.values():lib.ho_free(m)
(O/'compact-rescore.done').write_text('8019 pairs per model; native score checks passed\n');print('compact rescore complete',flush=True)
