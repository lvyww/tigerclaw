import json
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments');O=R/'joint-5gram-beam-20260921'
rerank={}
for r in map(json.loads,(R/'joint-5gram-rerank-20260921/results.jsonl').open()):
 c=r['candidates'][0];rerank[r['id']]={'target':r['text'],'code':r['code'],'top':c['text'],'score':c['score5'],'correct':r['text']==c['text'] and r['consumed']==len(r['code'])}
counts={'rows':0,'sameTop':0,'higherScoreWithDirectBeam':0,'lowerScoreWithDirectBeam':0,'equalScoreDifferentTop':0,'directRescue':0,'directRegression':0,'regressionFromHigherScoringWrongCandidate':0,'regressionFromBeamLoss':0};examples=[]
for r in map(json.loads,(O/'full5.jsonl').open()):
 old=rerank[r['id']];assert old['code']==r['code'] and old['target']==r['text']
 c=r['candidates'][0];newCorrect=c['text']==r['text'] and r['consumed']==len(r['code']);counts['rows']+=1
 counts['directRescue']+=newCorrect and not old['correct'];counts['directRegression']+=old['correct'] and not newCorrect
 if c['text']==old['top']:counts['sameTop']+=1
 elif c['score']>old['score']+1e-7:counts['higherScoreWithDirectBeam']+=1
 elif c['score']<old['score']-1e-7:counts['lowerScoreWithDirectBeam']+=1
 else:counts['equalScoreDifferentTop']+=1
 if old['correct'] and not newCorrect:
  if c['score']>old['score']+1e-7:counts['regressionFromHigherScoringWrongCandidate']+=1
  elif c['score']<old['score']-1e-7:counts['regressionFromBeamLoss']+=1
 if c['text']!=old['top']:examples.append({'id':r['id'],'target':r['text'],'rerank':old['top'],'beam':c['text'],'rerankScore':old['score'],'beamScore':c['score']})
(O/'search-vs-rerank.json').write_text(json.dumps({'counts':counts,'examples':examples},ensure_ascii=False,indent=2));print(counts)
