import subprocess,time,json
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';repo=Path(__file__).resolve().parents[3]
def wait_text(p,needle):
 start=time.monotonic()
 while not p.exists() or needle not in p.read_text():
  if time.monotonic()-start>10800:raise TimeoutError(p)
  time.sleep(5)
def run(args,name):
 with (O/'logs'/name).open('w') as f:subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,check=True)
wait_text(Path('/home/yc/tmp/tiger5-context/input-copy.json'),'sha256')
run(['g++','-O3','-std=c++20',str(repo/'tools/FullPinyinEval/HigherOrder/context_prune.cpp'),'-o',str(O/'bin/context_prune')],'prune-build.log')
run([str(O/'bin/context_prune'),'/home/yc/tmp/tiger5-context/input.arpa','/home/yc/tmp/tiger5-context/top1024.arpa','/home/yc/tmp/tiger5-context/top2048.arpa','1024,2048'],'context-prune-local.log')
print('full context prune complete',flush=True)
for limit in (1024,2048):
 model=O/f'models/joint5-context{limit}-q8.klm'
 if model.exists():model.rename(model.with_suffix('.failed-klm'))
 run([str(O/'bin/build_binary'),'-T','/home/yc/tmp/tiger5-quant','-S','3G','-q','8','-b','8','-a','64','trie',f'/home/yc/tmp/tiger5-context/top{limit}.arpa',str(model)],f'context{limit}-quant-final.log')
 print('built',limit,model.stat().st_size,flush=True)
 run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_run.py'),f'dev{limit}',str(model),'4',str(O/'dev-eval.jsonl')],f'dev{limit}-run.log')
 print('dev complete',limit,flush=True)
stats={}
for limit in (1024,2048):
 rows=[json.loads(x) for x in (O/f'dev{limit}.jsonl').open()]
 assert len(rows)==1981
 stats[limit]={'n':len(rows),'correct':sum(r['rank']==1 and r['consumed']==len(r['code']) for r in rows),'bytes':(O/f'models/joint5-context{limit}-q8.klm').stat().st_size}
best=max(stats,key=lambda k:(stats[k]['correct'],-stats[k]['bytes']))
(O/'dev-selection.json').write_text(json.dumps({'rule':'highest dev Top1; tie smaller binary','stats':stats,'selected':best},indent=2))
print('selected',best,stats,flush=True)
run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_run.py'),'pruned',str(O/f'models/joint5-context{best}-q8.klm'),'4'], 'pruned-run.log')
wait_text(O/'logs/q8-run.log','q8 complete')
print('all compact models complete',flush=True)
