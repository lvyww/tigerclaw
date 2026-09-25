"""CANCELED by user: historical fixed component-budget experiment.
Use run_rime_joint_compression.py for the unified Rime experiment.
"""
from pathlib import Path
import subprocess,json,hashlib
HERE=Path(__file__).resolve().parent
WORK=Path('/home/yc/tmp/shape-mix-500')
ROOT=Path('/mnt/c/Archive/char5-corpus4_0-20260922')
OUT=ROOT/'probability-mixture-500mb'
OLD=Path('/home/yc/tmp/tiger-char5-500/char5-context128-q8.klm')
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def main():
 records=[]
 for threshold in [80,96]:
  arpa=WORK/f'new{threshold}.arpa';model=WORK/f'new{threshold}-q8.klm'
  cmd=['/home/yc/tools/tigerclaw-brightmart/build/bin/build_binary','-T',str(WORK),'-S','3G','-q','8','-b','8','-a','64','trie',str(arpa),str(model)]
  with (OUT/f'build-{threshold}.log').open('w') as f:subprocess.run(cmd,stdout=f,stderr=subprocess.STDOUT,check=True)
  counts=[]
  with arpa.open() as f:
   for line in f:
    if line.startswith('ngram '):counts.append(int(line.split('=')[1]))
    if line.startswith('\\1-grams:'):break
  r=dict(model=str(model),bytes=model.stat().st_size,sha256=sha(model),ngram_counts=counts,thresholds=[18660,threshold,32,32],command=cmd)
  records.append(r);(OUT/'builds.json').write_text(json.dumps(records,indent=2)+'\n');print(r,flush=True)
 valid=[r for r in records if r['bytes']+OLD.stat().st_size<=500000000]
 assert valid,'No size-compliant component; do not evaluate an over-budget model'
 selected=max(valid,key=lambda r:r['bytes'])
 selected.update(old_model=str(OLD),old_sha256=sha(OLD),old_bytes=OLD.stat().st_size,total_bytes=selected['bytes']+OLD.stat().st_size,selection='largest of two predeclared models within total 500 MB; no accuracy selection',old_weight=.7)
 (OUT/'selected.json').write_text(json.dumps(selected,indent=2)+'\n')
 print('SELECTED',selected,flush=True)
 with (OUT/'evaluation.log').open('w') as f:
  subprocess.run(['python3',str(HERE/'evaluate_corpus4_compressed.py'),'--selected',str(OUT/'selected.json'),'--baseline',str(ROOT/'evaluation-shape-20k-20260923'),'--output',str(OUT/'evaluation'),'--method','mix_old70_total500','--extra-baseline',str(ROOT/'probability-mixture-20260923/mix_old60/predictions.jsonl'),'--mix-old-model',str(OLD),'--mix-old-weight','0.7','--mix-native','/home/yc/tmp/tiger-shape-direct5/shape5_mix.so'],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('DONE',flush=True)
if __name__=='__main__':
 raise SystemExit('Canceled fixed 420+80 MB experiment; use the unified Rime pipeline.')
