import ctypes as C,json,time,math,statistics
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-rerank-20260921'
lib=C.CDLL(str(O/'libhigherorder.so'))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_free.argtypes=[C.c_void_p]
ids=sorted(json.loads(x)['id'] for x in (R/'experiments/joint-pinyin-20260921/cases.jsonl').open() if json.loads(x)['split']=='test')[:100]
selected={}
for line in (R/'experiments/joint-pinyin-20260921/joint.jsonl').open():
 r=json.loads(line)
 if r['id'] in ids:selected[r['id']]=[' '.join(t for s in c['segments'] for t in s['tokens']).encode() for c in r['candidates']]
assert len(selected)==100
models=[lib.ho_load(str(p).encode()) for p in (R/'model-pinyin/models/joint_3gram.klm',R/'joint_5gram/joint_5gram.klm')];assert all(models)
o=C.c_uint();samples=[[],[]]
for repeat in range(4):
 for key in ids:
  for ix in ([0,1] if repeat%2==0 else [1,0]):
   start=time.perf_counter()
   for tokens in selected[key]:assert math.isfinite(lib.ho_score(models[ix],tokens,C.byref(o)))
   ms=(time.perf_counter()-start)*1000
   if repeat:samples[ix].append(ms)
report={'scope':'100 fixed hash-selected test sentences; frozen Top50; pre-encoded tokens; 1 warmup + 3 measured rounds, alternating model order; excludes first-pass decoding, model load, UI and encoding', 'sentences':ids,'results':{}}
for order,s in zip((3,5),samples):
 v=sorted(s);report['results'][str(order)]={'n':len(v),'p50Ms':statistics.median(v),'p95Ms':v[round(.95*(len(v)-1))],'p99Ms':v[round(.99*(len(v)-1))],'maxMs':max(v)}
for m in models:lib.ho_free(m)
(O/'warm-bench.json').write_text(json.dumps(report,indent=2));print(report['results'])
