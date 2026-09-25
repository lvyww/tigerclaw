"""Resumable offline .50 Corpus4 / .25 Articles / .25 selected-source experiment.
Preparation creates one shared vocabulary and packs sources in the work directory.
No production files are modified. Descendants use at most 12 logical CPUs.
"""
from pathlib import Path
import argparse,concurrent.futures,hashlib,json,os,shutil,subprocess,time
H=Path(__file__).resolve().parent
BUILDER=Path('/home/yc/tools/tigerclaw-brightmart/build/bin/build_binary')
CON=Path('/home/yc/tmp/shape-mix-rime/converter/build_tcs_knm03_preserving')
QUANT=Path('/home/yc/tmp/mainline-count-prune-20260924/requantize')
TARGET=[7959327,69562625,10273459,8415769]
FIXTURE=Path('/home/yc/tmp/tiger-shape-direct5/fixture')
BASE=Path('/mnt/c/Archive/char5-corpus4_0-20260922')
SETS={'old10k':Path('/home/yc/tmp/mohu-v5-old10k-20260924/cases.tsv'),'articles':BASE/'articles-comparison-20260923/cases.tsv','thucnews':BASE/'thucnews-comparison-20260923/cases.tsv'}
def sha(p):
 with Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def dump(p,x):Path(p).write_text(json.dumps(x,ensure_ascii=False,indent=2))
def main():
 p=argparse.ArgumentParser();p.add_argument('work',type=Path);args=p.parse_args();w=args.work.resolve()
 os.sched_setaffinity(0,sorted(os.sched_getaffinity(0))[:12]);os.environ.update(OMP_NUM_THREADS='12',OPENBLAS_NUM_THREADS='1')
 # Lock prevents concurrent controllers from writing identical output/checkpoints.
 import fcntl
 lock=(w/'controller.lock').open('w');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
 dump(w/'controller-pid.json',dict(pid=os.getpid(),started=time.time()))
 assert json.loads((w/'toy-three.json').read_text())['passed']
 assert json.loads((w/'toy-two-regression.json').read_text())['passed']
 assert (w/'prepare-complete.json').exists()
 from build_articles_model import header
 mainline=Path('/mnt/c/Archive/tigerclaw_sentence_ml/runtime/sentence-fivegram-mobile.bin')
 assert header(mainline)['counts']==[21230,*TARGET]
 assert sha(mainline)=='5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361'
 def run(name,cmd):
  marker=w/(name+'.done.json')
  if marker.exists():return
  dump(w/'status.json',dict(stage=name,time=time.time(),command=list(map(str,cmd))))
  print(name,flush=True);start=time.time()
  with (w/(name+'.log')).open('w') as f:subprocess.run(list(map(str,cmd)),stdout=f,stderr=subprocess.STDOUT,check=True)
  dump(marker,dict(seconds=time.time()-start,command=list(map(str,cmd)),finished=time.time()))
 def index(d,n):
  marker=d/f'index{n}.complete.json'
  if marker.exists():return
  name=d.name+f'-index{n}';temp=d/f'index{n}-export.tmp.arpa';out=d/('lower4.klm' if n==4 else 'full5.klm')
  assert all(x['byte_identical'] for x in json.loads((w/'stream-index-parity.json').read_text()).values())
  run(name+'-stream-build',['python3',H/'build_streamed_index.py',w/'budget',BUILDER,d,out.with_suffix('.building.klm'),n])
  if out.with_suffix('.building.klm').exists():os.replace(out.with_suffix('.building.klm'),out)
  dump(marker,dict(bytes=out.stat().st_size,sha256=sha(out)))
  if temp.exists():temp.unlink()
 def identity(name,dirs):
  dest=w/(name+'-identity.json')
  if dest.exists():return dest
  files={}
  for d in dirs:
   for file in ['vocab.txt',*[f'{n}.bin' for n in range(1,6)],'lower4.klm','full5.klm']:
    p=d/file
    if p.exists():files[str(p)]=dict(bytes=p.stat().st_size,sha256=sha(p))
  dump(dest,dict(files=files));return dest
 def evaluate(stage,model,model_id=None,worker_counts=None):
  worker_counts=worker_counts or {d:4 for d in SETS}
  marker=w/(stage+'-evaluation.done.json')
  if marker.exists():return
  dump(w/'status.json',dict(stage='evaluation',variant=stage,time=time.time(),workers=sum(worker_counts.values())))
  def one(item):
   d,cases=item;dest=w/'eval'/stage/d
   if (dest/'summary.json').exists():return
   cmd=['python3',H/'evaluate_external_shape.py','--cases',cases,'--fixture',FIXTURE,'--model',model,'--output',dest,'--workers',worker_counts[d]]
   cmd+=['--native-lib',w/'budget.so','--model-identity',model_id] if model_id else ['--tcs-reader-root',w/'reader']
   with (w/(stage+'-'+d+'.log')).open('w') as f:subprocess.run(list(map(str,cmd)),stdout=f,stderr=subprocess.STDOUT,check=True)
  with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:list(pool.map(one,SETS.items()))
  dump(marker,dict(finished=time.time(),results={d:json.loads((w/'eval'/stage/d/'summary.json').read_text())['combined'] for d in SETS}))
 index(w/'nonnews',4);index(w/'nonnews',5)
 generation=w/'nonnews-generation';generation.mkdir(exist_ok=True)
 for file in ['vocab.txt',*[f'{n}.bin' for n in range(1,6)],'complete.txt','lower4.klm']:
  if not (generation/file).exists():os.link(w/'nonnews'/file,generation/file)
 sources=[w/name for name in ('corpus4','articles','nonnews')]
 run('audit-sources',['python3',H/'audit_accelerated_sources.py',w/'budget',w/'sources-audit.json',*sources])
 source_id=identity('sources',sources)
 dynamic=w/'dynamic.txt';dynamic.write_text('mixture3\n'+''.join(str(d)+'\n' for d in sources))
 evaluate('dynamic',dynamic,source_id)
 full=w/'full'
 if not (full/'4.ready').exists():run('materialize-low',[w/'budget','mix3',*[w/(n+'-generation') for n in ('corpus4','articles','nonnews')],full,12,2,4])
 index(full,4)
 if not (full/'5.ready').exists():run('materialize-high',[w/'budget','mix3',*[w/(n+'-generation') for n in ('corpus4','articles','nonnews')],full,12,5])
 assert json.loads((w/'prefix-stream-real-parity.json').read_text())['bytes_identical']
 run('audit-full',['python3',H/'audit_mixture_budget.py',full,w/'budget',w/'full-audit.json','--sources',*sources,'--weights',.5,.25,.25])
 pruned=w/'pruned';run('prune',[w/'budget','prune',full,pruned,*TARGET])
 counts=list(map(int,(pruned/'complete.txt').read_text().split()));assert counts==[len((w/'vocab.txt').read_text().splitlines()),*TARGET],counts
 run('audit-pruned',['python3',H/'audit_mixture_budget.py',pruned,w/'budget',w/'pruned-audit.json'])
 run('export',[w/'budget','export',pruned,w/'model.arpa'])
 run('convert',[CON,w/'model.arpa',w/'model-q16.bin',w/'convert-temp','--preserve-all'])
 run('quantize',[QUANT,w/'model-q16.bin',w/'model-q8.bin'])
 from build_articles_model import header
 assert header(w/'model-q8.bin')['counts']==counts
 reader=w/'reader/lua';reader.mkdir(parents=True,exist_ok=True);shutil.copyfile(H/'TcsQ8/tiger_sentence_fivegram.lua',reader/'tiger_sentence_fivegram.lua')
 run('validate-q8',['python3',H/'TcsQ8/validate.py',w/'model-q16.bin',w/'model-q8.bin',w/'reader',w/'q8-validation'])
 run('audit-quantization',['python3',H/'audit_mixture_budget.py',pruned,w/'budget',w/'quantization-audit.json','--q16',w/'model-q16.bin','--q8',w/'model-q8.bin'])
 dump(w/'selected.json',dict(weights=[.5,.25,.25],counts=counts,bytes=(w/'model-q8.bin').stat().st_size,sha256=sha(w/'model-q8.bin')))
 desc=w/'pruned.txt';desc.write_text('single\n'+str(pruned)+'\n')
 dump(w/'overlap.json',dict(full_index_processes=2,evaluation_workers={'old10k':3,'articles':4,'thucnews':3},max_compute_workers=12))
 with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
  future=pool.submit(index,full,5)
  evaluate('pruned',desc,identity('pruned',[pruned]),{'old10k':3,'articles':4,'thucnews':3})
  evaluate('q8',w/'model-q8.bin',worker_counts={'old10k':3,'articles':4,'thucnews':3})
  future.result()
 run('audit-full-fast5',['python3',H/'audit_mixture_budget.py',full,w/'budget',w/'full-fast5-audit.json','--backoff-context-probes','--sources',*sources,'--weights',.5,.25,.25])
 desc=w/'full.txt';desc.write_text('single\n'+str(full)+'\n');evaluate('full',desc,identity('full',[full]))
 dump(w/'complete.json',dict(finished=time.time(),stages=['dynamic','full','pruned','q8']))
 print('EXPERIMENT COMPLETE',flush=True)
if __name__=='__main__':main()
