import os,json,time,subprocess,statistics
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';B=R/'experiments/joint-pinyin-20260921';repo=Path(__file__).resolve().parents[3]
start=time.monotonic()
while not (O/'compact-rescore.done').exists() or not (O/'logs/incremental-pruned-tests.log').exists() or 'incremental/full Top50 comparisons' not in (O/'logs/incremental-pruned-tests.log').read_text():
 if time.monotonic()-start>14400:raise TimeoutError('compact pipeline')
 time.sleep(5)
assert (O/'full5.jsonl').exists()
rows=[json.loads(x) for x in (O/'check100.jsonl').open()][:30]
(O/'bench30.jsonl').write_text(''.join(json.dumps(x,ensure_ascii=False)+'\n' for x in rows))
selected=json.loads((O/'dev-selection.json').read_text())['selected']
models={'3gram':R/'model-pinyin/models/joint_3gram.klm','full5':R/'joint_5gram/joint_5gram.klm','q8':O/'models/joint5-q8.klm','pruned':O/f'models/joint5-context{selected}-q8.klm'}
env=os.environ.copy();env['LD_LIBRARY_PATH']=str(O/'bin')
report={'scope':'Same isolated decoder; 30 fixed test sentences; single process/model, sequential after all build and decode jobs. Includes incremental decoding/caches, excludes model load and GUI; not installed ARM64 application latency. Prototype uses packed string histories.','models':{}}
for name,model in models.items():
 args=[str(B/'dotnet/dotnet'),str(O/'bench-bin/Joint.dll'),'bench','joint',str(model),str(repo/'release/拼音反查码表/拼音.txt'),str(B/'tokens.json'),str(O/'bench30.jsonl'),str(O/f'latency-{name}.jsonl')]
 with (O/f'logs/latency-{name}.log').open('w') as log:subprocess.run(args,env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
 data=[json.loads(x) for x in (O/f'latency-{name}.jsonl').open()];stat={}
 for direction in ('append','backspace'):
  values=sorted(x['ms'] for x in data if x['direction']==direction)
  stat[direction]={'n':len(values),'p50Ms':statistics.median(values),'p95Ms':values[round((len(values)-1)*.95)],'p99Ms':values[round((len(values)-1)*.99)],'maxMs':max(values)}
 report['models'][name]=stat;print(name,stat,flush=True)
# Batch rerank cost on exactly the same 30 candidate sets, after key benchmarks.
import ctypes as C,math
lib=C.CDLL(str(R/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_free.argtypes=[C.c_void_p]
ids={r['id'] for r in rows}
sets=[[' '.join(t for seg in c['segments'] for t in seg['tokens']).encode() for c in r['candidates']] for r in map(json.loads,(O/'verify3.jsonl').open()) if r['id'] in ids]
assert len(sets)==30
report['warmRerank']={}
for name,path in models.items():
 m=lib.ho_load(str(path).encode());assert m;values=[];o=C.c_uint()
 for repeat in range(4):
  for candidates in sets:
   start=time.perf_counter()
   for tokens in candidates:assert math.isfinite(lib.ho_score(m,tokens,C.byref(o)))
   if repeat:values.append((time.perf_counter()-start)*1000)
 lib.ho_free(m);values.sort()
 report['warmRerank'][name]={'n':len(values),'p50Ms':statistics.median(values),'p95Ms':values[round((len(values)-1)*.95)],'p99Ms':values[round((len(values)-1)*.99)]}
(O/'latency-report.json').write_text(json.dumps(report,indent=2))
print('benchmark complete',flush=True)
