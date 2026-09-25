"""Joint-context 70:30 compression, static fusion, TCSKNM03 conversion.

Requires the separately validated fixed-weight MITLM and preserving converter.
No installation, learning, timing comparison, or accuracy-based size selection.
"""
import hashlib,json,subprocess,shutil,time,os
from pathlib import Path
W=Path('/home/yc/tmp/shape-mix-rime')
OUT=Path('/mnt/c/Archive/char5-corpus4_0-20260922/rime-joint-compression')

def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

def canonicalize(src,dst):
 # MITLM shares internal BOS/EOS ID, requires EOS first and BOS probability -99.
 with src.open('rb') as f,dst.open('wb') as out:
  header=[]
  for line in f:
   if line.strip()==b'\\1-grams:':break
   if line.strip():header.append(line.strip())
  uni=[]
  for line in f:
   if line.strip()==b'\\2-grams:':break
   if line.strip():uni.append(line.split())
  assert uni
  uni.sort(key=lambda row:0 if row[1]==b'</s>' else 1 if row[1]==b'<s>' else 2)
  out.write(b'\n'.join(header)+b'\n\n\\1-grams:\n')
  for row in uni:
   if row[1]==b'<s>':row[0]=b'-99'
   if row[1]==b'</s>':row=row[:2]
   out.write(b'\t'.join(row)+b'\n')
  out.write(b'\n\\2-grams:\n');shutil.copyfileobj(f,out,8*1024*1024)

def repair_bos(src,dst):
 # BOS is a state marker, not a predicted word; avoid wasting quantizer range on -99.
 with src.open('rb') as f,dst.open('wb') as out:
  for line in f:
   if b'\t<s>\t' in line and len(line.split())==3:
    line=b'0\t'+line.split(b'\t',1)[1]
   out.write(line)
   if line.strip()==b'\\2-grams:':break
  shutil.copyfileobj(f,out,8*1024*1024)

def run(cmd,log):
 with log.open('w') as f:subprocess.run(list(map(str,cmd)),stdout=f,stderr=subprocess.STDOUT,check=True)

def main():
 OUT.mkdir(exist_ok=True)
 plan=dict(old_weight=.7,new_weight=.3,history_limits_candidates=[[65535,512,64,64],[65535,640,64,64]],ranking='shared union unigram ranking using .7*old+.3*new, absent vocabulary mass zero',budget_bytes=500000000,selection='largest initial shared history threshold fitting size, before accuracy evaluation',method='same suffix-closed context retention for both components; linear static merge; reestimated backoff; TCSKNM03 preserve-all',format_control_sha256='4e6d79b957a55edf35cd9e2e66c62bd0bbe598581b7dc088b462122a713172a7')
 (OUT/'plan.json').write_text(json.dumps(plan,indent=2)+'\n')
 while True:
  log=(W/'new-prune.log').read_text()
  if 'limit 65535:640:64:64' in log:break
  # Only wait while this task's source-pruning process is alive.
  ps=subprocess.check_output(['ps','-eo','args'],text=True)
  if not any(line.startswith(str(W/'joint_context_prune')+' ') and '/new512.arpa' in line for line in ps.splitlines()):raise RuntimeError('source pruning stopped before completion')
  time.sleep(10)
 assert 'limit 65535:640:64:64' in (W/'old-prune.log').read_text()
 records=[]
 for k in [640,512]:
  print('CANONICALIZE',k,flush=True)
  for side in ['old','new']:canonicalize(W/f'{side}{k}.arpa',W/f'{side}{k}-canonical.arpa')
  print('MERGE',k,flush=True)
  arpa=W/f'merged{k}.arpa'
  run([W/'interpolate-fixed-v3','-order','5','-lm',f'{W}/old{k}-canonical.arpa,{W}/new{k}-canonical.arpa','-write-lm',arpa],OUT/f'merge-{k}.log')
  # Reject invalid numerical output, not just a successful process exit.
  check=subprocess.run(['rg','-n','-i',r'(^|\s)(nan|[-+]?inf)(\s|$)',str(arpa)],capture_output=True,text=True)
  assert check.returncode==1,check.stdout[:4000]
  ready=W/f'merged{k}-rime.arpa';repair_bos(arpa,ready)
  print('CONVERT',k,flush=True)
  model=W/f'merged{k}-tcs3.bin'
  run([W/'converter/build_tcs_knm03_preserving',ready,model,W/f'merged{k}-temp','--preserve-all'],OUT/f'convert-{k}.log')
  record=dict(model=str(model),bytes=model.stat().st_size,sha256=sha(model),history_limit=k,arpa=str(ready),arpa_sha256=sha(ready))
  records.append(record);(OUT/'builds.json').write_text(json.dumps(records,indent=2)+'\n');print(record,flush=True)
  if record['bytes']<=500000000:
   (OUT/'selected.json').write_text(json.dumps(record,indent=2)+'\n');print('SELECTED',flush=True);return
 raise RuntimeError('Both initial candidates exceed size budget; no over-budget model selected')
if __name__=='__main__':main()
