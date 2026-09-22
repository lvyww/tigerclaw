import json,os,subprocess,sys,time,hashlib
from pathlib import Path
R=Path('/mnt/c/Archive/tigerclaw_sentence_ml');O=R/'experiments/joint-5gram-beam-20260921';B=R/'experiments/joint-pinyin-20260921'
repo=Path(__file__).resolve().parents[3]
name=sys.argv[1];model=Path(sys.argv[2]);jobs=int(sys.argv[3]) if len(sys.argv)>3 else 4
# Exact local copy avoids WSL cross-filesystem 4-KiB hash I/O in each worker.
canonical=model
if model.stat().st_size>1024**3:
 cache=Path('/home/yc/tmp/tiger5-model-cache');cache.mkdir(exist_ok=True)
 dest=cache/model.name;stamp=cache/(model.name+'.json');before=model.stat()
 info=json.loads(stamp.read_text()) if stamp.exists() else {}
 if not dest.exists() or info.get('source')!=str(model) or info.get('mtimeNs')!=before.st_mtime_ns or info.get('bytes')!=before.st_size:
  h=hashlib.sha256();size=0
  with model.open('rb') as f,dest.with_suffix('.copying').open('wb') as out:
   for block in iter(lambda:f.read(8*1024*1024),b''):out.write(block);h.update(block);size+=len(block)
  assert size==before.st_size and model.stat().st_mtime_ns==before.st_mtime_ns
  dest.with_suffix('.copying').replace(dest)
  info={'source':str(model),'mtimeNs':before.st_mtime_ns,'bytes':size,'sha256':h.hexdigest()};stamp.write_text(json.dumps(info))
 assert dest.stat().st_size==info['bytes']
 (O/(name+'-model-copy.json')).write_text(json.dumps(info,indent=2))
 model=dest
cases=B/'cases.jsonl'
if len(sys.argv)>4:cases=Path(sys.argv[4])
env=os.environ.copy();env['LD_LIBRARY_PATH']=str(O/'bin')
base=[str(B/'dotnet/dotnet'),str(O/'bin/Joint.dll'),'decode','joint',str(model),str(repo/'release/拼音反查码表/拼音.txt'),str(B/'tokens.json'),str(cases)]
procs=[]
for i in range(jobs):
 out=O/'parts'/f'{name}-{i}.jsonl';assert not out.exists()
 log=(O/'logs'/f'{name}-{i}.log').open('w')
 procs.append((subprocess.Popen(base+[str(out),str(i),str(jobs)],env=env,stdout=log,stderr=subprocess.STDOUT),log,out))
failed=[]
for p,log,out in procs:
 rc=p.wait();log.close()
 if rc:failed.append((str(out),rc))
assert not failed,failed
rows=[]
for p,log,out in procs:rows.extend(json.loads(x) for x in out.open())
if model!=canonical:
 for _,_,out in procs:assert json.loads(Path(str(out)+'.manifest.json').read_text())['model'].lower()==info['sha256']
expected=[json.loads(x) for x in cases.open() if json.loads(x)['split']=='test']
assert len(rows)==len(expected) and len({r['id'] for r in rows})==len(rows)
lookup={r['id']:r for r in rows}
with (O/f'{name}.jsonl').open('x') as f:
 for c in expected:
  r=lookup[c['id']];assert (r['text'],r['code'])==(c['text'],c['code']);f.write(json.dumps(r,ensure_ascii=False)+'\n')
print(name,'complete',len(rows),flush=True)
