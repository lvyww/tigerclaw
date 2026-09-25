"""Use remaining file budget without consulting sentence accuracy."""
import concurrent.futures,json,subprocess
from pathlib import Path
from run_rime_joint_compression import W,OUT,sha,canonicalize,repair_bos,run

def main():
 plan=json.loads((OUT/'plan.json').read_text())
 plan['size_only_extension']={'reason':'640 candidate is 353898569 bytes; spend remaining budget preserving more trigrams','candidates':[960,1024],'accuracy_consulted':False}
 (OUT/'plan.json').write_text(json.dumps(plan,indent=2)+'\n')
 def prune(side):
  source=Path('/home/yc/tmp/tiger-char5-500/context128.arpa') if side=='old' else W/'new-all3-high64.arpa'
  run([W/'joint_context_prune',source,W/f'{side}960.arpa',W/f'{side}1024.arpa','65535:960:64:64,65535:1024:64:64',W/'joint-ranks.tsv'],OUT/f'{side}-expanded-prune.log')
 with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:list(pool.map(prune,['old','new']))
 records=json.loads((OUT/'builds.json').read_text())
 for k in [1024,960]:
  print('CANONICALIZE',k,flush=True)
  for side in ['old','new']:canonicalize(W/f'{side}{k}.arpa',W/f'{side}{k}-canonical.arpa')
  print('MERGE',k,flush=True)
  arpa=W/f'merged{k}.arpa'
  run([W/'interpolate-fixed-v3','-order','5','-lm',f'{W}/old{k}-canonical.arpa,{W}/new{k}-canonical.arpa','-write-lm',arpa],OUT/f'merge-{k}.log')
  check=subprocess.run(['rg','-n','-i',r'(^|\s)(nan|[-+]?inf)(\s|$)',str(arpa)],capture_output=True,text=True)
  assert check.returncode==1,check.stdout[:4000]
  ready=W/f'merged{k}-rime.arpa';repair_bos(arpa,ready)
  print('CONVERT',k,flush=True)
  model=W/f'merged{k}-tcs3.bin'
  run([W/'converter/build_tcs_knm03_preserving',ready,model,W/f'merged{k}-temp','--preserve-all'],OUT/f'convert-{k}.log')
  r=dict(model=str(model),bytes=model.stat().st_size,sha256=sha(model),history_limit=k,arpa=str(ready),arpa_sha256=sha(ready))
  records.append(r);(OUT/'builds.json').write_text(json.dumps(records,indent=2)+'\n');print(r,flush=True)
  if r['bytes']<=500000000:
   (OUT/'selected.json').write_text(json.dumps(r,indent=2)+'\n');print('SELECTED',flush=True);return
 raise RuntimeError('Expanded candidates exceed budget; initial 640 candidate remains available')
if __name__=='__main__':main()
