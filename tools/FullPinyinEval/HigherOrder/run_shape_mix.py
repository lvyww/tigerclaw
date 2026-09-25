"""Fixed, offline mixture experiment; never train or deploy."""
import json, os, shutil, subprocess, hashlib
from pathlib import Path
HERE=Path(__file__).resolve().parent
ROOT=Path('/mnt/c/Archive/char5-corpus4_0-20260922')
OUT=ROOT/'probability-mixture-20260923'
LOCAL=Path('/home/yc/tmp/tiger-shape-direct5')
OLD=Path('/home/yc/tmp/tiger-char5-500/char5-context128-q8.klm')
NEW=Path('/home/yc/tmp/corpus4-char5-eval/char5-q8.klm')
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def main():
 OUT.mkdir(exist_ok=True)
 assert sha(OLD)=='580ed90ced0ac72e453e0d647635cec2d3879e2e47ae1a231b96f49b2d34eafa'
 assert sha(NEW)=='6114583494db90093d7738fe0f268a104dd82d08b0f7b4b80ef95c153787effc'
 (OUT/'plan.json').write_text(json.dumps(dict(weights=[.9,.8,.7],old=str(OLD),new=str(NEW),search='per-token probability interpolation',retrain=False,deploy=False),indent=2))
 selected=OUT/'selected.json'
 selected.write_text(json.dumps(dict(model=str(NEW),bytes=NEW.stat().st_size,sha256=sha(NEW))))
 shutil.copyfile(LOCAL/'shape5.so',LOCAL/'shape5_reference_new.so')
 env=dict(os.environ,SHAPE5_OLD_MODEL=str(OLD),SHAPE5_MODEL=str(NEW),SHAPE5_LIB=str(LOCAL/'shape5_mix.so'))
 with (OUT/'adapter-validation.log').open('w') as log:
  for w in [0,1,.9,.8,.7]:
   env['SHAPE5_OLD_WEIGHT']=str(w)
   subprocess.run(['lua',str(HERE/'test_shape5_mix.lua'),str(LOCAL/'shape5.so'),str(LOCAL/'shape5_reference_new.so'),str(LOCAL/'shape5_mix.so'),str(LOCAL/'score-texts.txt')],env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
  cases=(ROOT/'evaluation-shape-20k-20260923/cases.tsv').read_text().splitlines()
  sample=OUT/'endpoint-cases.tsv';sample.write_text('\n'.join(cases[:25]+cases[10000:10025])+'\n')
  for w,model in [(1,OLD),(0,NEW)]:
   env['SHAPE5_OLD_WEIGHT']=str(w)
   paths=[]
   for kind in ['reference','mix']:
    e=env.copy()
    if kind=='reference':e.update(SHAPE5_LIB=str(LOCAL/'shape5.so'),SHAPE5_MODEL=str(model))
    pool=OUT/f'endpoint-{w}-{kind}.tsv';paths.append(pool)
    subprocess.run(['lua',str(HERE/'export_shape_pool.lua'),str(LOCAL/'fixture'),str(sample),str(pool)],env=e,stdout=log,stderr=subprocess.STDOUT,check=True)
   assert paths[0].read_bytes()==paths[1].read_bytes(),'endpoint candidate mismatch'
   log.write(f'PASS endpoint {w}: all candidates and scores identical on 50 cases\n');log.flush()
 print('Adapter and endpoint validation passed',flush=True)
 extra=ROOT/'compressed-loworder/evaluation/predictions.jsonl'
 for w in [.9,.8,.7]:
  name=f'mix_old{round(w*100)}';dest=OUT/name
  print('START '+name,flush=True)
  with (OUT/f'{name}.log').open('w') as log:
   subprocess.run(['python3',str(HERE/'evaluate_corpus4_compressed.py'),'--selected',str(selected),'--baseline',str(ROOT/'evaluation-shape-20k-20260923'),'--output',str(dest),'--method',name,'--extra-baseline',str(extra),'--max-model-bytes','10000000000','--mix-old-model',str(OLD),'--mix-old-weight',str(w),'--mix-native',str(LOCAL/'shape5_mix.so')],stdout=log,stderr=subprocess.STDOUT,check=True)
  extra=dest/'predictions.jsonl'
  print('DONE '+name,flush=True)
if __name__=='__main__':main()
