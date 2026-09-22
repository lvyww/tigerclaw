"""Size-only selection of a character fivegram <=500,000,000 bytes."""
from pathlib import Path
import json,subprocess,hashlib,shutil
O=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/brightmart-char5-500mb-20260922')
W=Path('/home/yc/tmp/tiger-char5-500');B=Path('/home/yc/tools/tigerclaw-brightmart/build/bin/build_binary')
assert 'limit 256 ' in (O/'prune.log').read_text(),'pruning must complete first'
records=[]
for context,bits in [(256,8),(256,6),(128,8),(128,6)]:
 name=f'char5-context{context}-q{bits}';model=W/(name+'.klm');arpa=W/f'context{context}.arpa'
 assert not model.exists(),model
 cmd=[str(B),'-T',str(W),'-S','3G','-q',str(bits),'-b',str(bits),'-a','64','trie',str(arpa),str(model)]
 print('building',name,flush=True)
 with (O/(name+'-build.log')).open('w') as f:subprocess.run(cmd,stdout=f,stderr=subprocess.STDOUT,check=True)
 with model.open('rb') as f:h=hashlib.file_digest(f,'sha256').hexdigest()
 record=dict(name=name,context_vocabulary=context,quantization_bits=bits,bytes=model.stat().st_size,sha256=h,command=cmd,under500MB=model.stat().st_size<=500_000_000)
 records.append(record);(O/'builds.json').write_text(json.dumps(records,indent=2)+'\n');print(record,flush=True)
 if record['under500MB']:
  dst=O/(name+'.klm');shutil.copyfile(model,dst)
  with dst.open('rb') as f:assert hashlib.file_digest(f,'sha256').hexdigest()==h
  record.update(local_model=str(model),delivery_model=str(dst),selection='predeclared size-only order; no benchmark tuning',budget_bytes=500_000_000,budget_scope='fivegram file only; excludes trigram isolation-prior file in offline harness')
  (O/'selected.json').write_text(json.dumps(record,indent=2)+'\n');break
else:raise RuntimeError('No initial candidate meets budget; need smaller context limit')
