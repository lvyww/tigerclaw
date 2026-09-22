"""Finish compact builds, select pruning context limit on dev, evaluate test."""
import subprocess,os,time,json
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';repo=Path(__file__).resolve().parents[3]
def wait_log(path,word):
 start=time.monotonic()
 while word not in (path.read_text() if path.exists() else ''):
  if time.monotonic()-start>10800:raise TimeoutError(path)
  time.sleep(5)
def run(args,log):
 with (O/'logs'/log).open('w') as f:subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,check=True)
# Avoid multiple 3-GiB sort jobs competing for the same files and benchmark window.
wait_log(O/'logs/quant8.log','SUCCESS')
print('quant8 build done',flush=True)
# Five-order quantization is evaluated without development-set tuning.
q=subprocess.Popen(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_run.py'),'q8',str(O/'models/joint5-q8.klm'),'4'],stdout=(O/'logs/q8-run.log').open('w'),stderr=subprocess.STDOUT)
wait_log(O/'logs/context-prune.log','limit 2048 ')
print('context pruning complete',flush=True)
for limit in (1024,2048):
 model=O/f'models/joint5-context{limit}-q8.klm'
 run([str(O/'bin/build_binary'),'-T','/home/yc/tmp/tiger5-quant','-S','3G','-q','8','-b','8','-a','64','trie',f'/home/yc/tmp/tiger5-context/top{limit}.arpa',str(model)],f'context{limit}-quant.log')
 print('built',limit,model.stat().st_size,flush=True)
 run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_run.py'),f'dev{limit}',str(model),'4',str(O/'dev-eval.jsonl')],f'dev{limit}-run.log')
 print('dev complete',limit,flush=True)
assert q.wait()==0
stats={}
for limit in (1024,2048):
 rows=[json.loads(x) for x in (O/f'dev{limit}.jsonl').open()]
 stats[limit]={'n':len(rows),'correct':sum(r['rank']==1 and r['consumed']==len(r['code']) for r in rows),'bytes':(O/f'models/joint5-context{limit}-q8.klm').stat().st_size}
# Predeclared rule: higher dev exact Top1, then smaller binary.
best=max(stats,key=lambda k:(stats[k]['correct'],-stats[k]['bytes']))
(O/'dev-selection.json').write_text(json.dumps({'rule':'highest dev Top1; tie smaller binary','stats':stats,'selected':best},indent=2))
print('selected',best,stats,flush=True)
run(['python3',str(repo/'tools/FullPinyinEval/HigherOrder/beam_run.py'),'pruned',str(O/f'models/joint5-context{best}-q8.klm'),'4'], 'pruned-run.log')
print('all compact models complete',flush=True)
