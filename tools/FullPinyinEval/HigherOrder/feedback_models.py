import ctypes as C,json,math
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';repo=Path(__file__).resolve().parents[3]
paths={'full5':R/'joint_5gram/joint_5gram.klm','q8':O/'models/joint5-q8.klm'}
if (O/'dev-selection.json').exists():paths['pruned']=O/f"models/joint5-context{json.loads((O/'dev-selection.json').read_text())['selected']}-q8.klm"
lib=C.CDLL(str(R/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'));lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p;lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double;lib.ho_free.argtypes=[C.c_void_p]
rows=[json.loads(x) for x in (repo/'next/_run/FullPinyin/youhua-issue/exact-200.jsonl').open()];result={};o=C.c_uint()
for name,path in paths.items():
 m=lib.ho_load(str(path).encode());assert m;scored=[]
 for r in rows:
  candidates=[]
  for c in r['candidates']:
   ts=[t for s in c['segments'] for t in s['tokens']]
   score=lib.ho_score(m,' '.join(ts).encode(),C.byref(o))*math.log(10)+2*len(ts);assert math.isfinite(score)
   candidates.append({'text':c['text'],'score':score,'frequency':c['frequency']})
  candidates.sort(key=lambda c:(-c['score'],-c['frequency'],c['text'].encode('utf-16-be')))
  scored.append({'code':r['code'],'target':r['text'],'candidates':candidates})
 result[name]=scored;lib.ho_free(m)
(O/'feedback-models.json').write_text(json.dumps({'scope':'Rescoring original feedback Top50; not included in test statistics','models':result},ensure_ascii=False,indent=2))
for name,rs in result.items():print(name,[(r['code'],r['candidates'][0]['text']) for r in rs if r['candidates']])
