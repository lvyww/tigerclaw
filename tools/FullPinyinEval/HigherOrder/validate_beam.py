"""Independent full-state KenLM score and preserved input/token-path checks."""
import json,ctypes as C,math,sys
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921'
lib=C.CDLL(str(R/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'))
lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
lib.ho_order.argtypes=[C.c_void_p];lib.ho_order.restype=C.c_uint
lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
lib.ho_free.argtypes=[C.c_void_p]
model=Path(sys.argv[1]);path=Path(sys.argv[2]);m=lib.ho_load(str(model).encode());assert m
maxdiff=0;count=0;o=C.c_uint();seen=set()
for line in path.open():
 r=json.loads(line);assert r['id'] not in seen;seen.add(r['id'])
 assert len({c['text'] for c in r['candidates']})==len(r['candidates'])
 for c in r['candidates']:
  tokens=[t for s in c['segments'] for t in s['tokens']]
  assert ''.join(t.rsplit('/',1)[0] for t in tokens)==c['text']
  expected=lib.ho_score(m,' '.join(tokens).encode(),C.byref(o))*math.log(10)+2*len(tokens)
  maxdiff=max(maxdiff,abs(expected-c['score']));assert abs(expected-c['score'])<1e-5,(r['id'],expected,c['score'])
  count+=1
 if len(seen)>=100:break
report={'order':lib.ho_order(m),'model':str(model),'source':str(path),'rows':len(seen),'candidates':count,'maxScoreDelta':maxdiff}
lib.ho_free(m);(O/(path.stem+'-score-verification.json')).write_text(json.dumps(report,indent=2));print(report)
