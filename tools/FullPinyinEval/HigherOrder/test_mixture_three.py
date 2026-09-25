"""Three-source probability oracle, normalization, resume and thread parity."""
import argparse,json,math,subprocess,tempfile,itertools
from pathlib import Path
from test_mixture_budget import toy,write_arpa,score,check_mass,REC

def read_model(path):
 vocab=(path/'vocab.txt').read_text().splitlines();rows={};loss={}
 for n in range(1,6):
  keys=[]
  for record in REC.iter_unpack((path/f'{n}.bin').read_bytes()):
   key=tuple(vocab[i] for i in record[:n]);assert key not in rows
   rows[key]=record[6:8];loss[key]=record[8];keys.append(record[:n])
  assert keys==sorted(keys), 'records must sort by stable numeric IDs, not spelling'
 return vocab,rows,loss

def main():
 p=argparse.ArgumentParser();p.add_argument('pack');p.add_argument('budget');p.add_argument('output',type=Path);p.add_argument('--native-lib',type=Path);p.add_argument('--reference-budget',type=Path);a=p.parse_args()
 def run(*args):subprocess.run(list(map(str,args)),check=True,stdout=subprocess.DEVNULL)
 with tempfile.TemporaryDirectory(prefix='mixture-three-') as tmp:
  w=Path(tmp);sources=[toy(['a','b'],52),toy(['b','c'],61),toy(['a','aa'],91)]
  # Deliberately append the third model's new vocabulary, preserving existing IDs.
  base=sorted({k[0] for r in sources[:2] for k in r if len(k)==1})
  vocab=base+sorted({k[0] for k in sources[2] if len(k)==1}-set(base));(w/'vocab.txt').write_text('\n'.join(vocab)+'\n')
  for name,rows in zip('abc',sources):
   write_arpa(w/f'{name}.arpa',rows);run(a.pack,'pack',w/'vocab.txt',w/f'{name}.arpa',w/name,2,3)
  packed=[read_model(w/name)[1] for name in 'abc']
  def ext(r,k):
   known={k[0] for k in r if len(k)==1};key=tuple(x if x in known else '<unk>' for x in k)
   return score(r,key)-(math.log10(1+len(set(vocab)-known)) if key[-1]=='<unk>' else 0)
  for name,threads in [('full',2),('single',1)]:run(a.budget,'mix3',w/'a',w/'b',w/'c',w/name,threads)
  run(a.budget,'mix3',w/'a',w/'b',w/'c',w/'resume',2,2,4)
  run(a.budget,'mix3',w/'a',w/'b',w/'c',w/'resume',2,5)
  for n in range(1,6):assert (w/'full'/f'{n}.bin').read_bytes()==(w/'single'/f'{n}.bin').read_bytes()==(w/'resume'/f'{n}.bin').read_bytes()
  if a.reference_budget:
   run(a.reference_budget,'mix3',w/'a',w/'b',w/'c',w/'reference',2)
   for n in range(1,6):assert (w/'full'/f'{n}.bin').read_bytes()==(w/'reference'/f'{n}.bin').read_bytes()
  _,rows,_=read_model(w/'full');assert set(rows)==set.union(*(set(r) for r in packed));check_mass(vocab,rows)
  probes=0
  for k,(value,_) in rows.items():
   if k==('<s>',):continue
   expected=math.log10(sum(weight*10**ext(r,k) for weight,r in zip((.5,.25,.25),packed)))
   assert abs(value-expected)<2e-6,(k,value,expected);probes+=1
  run(a.budget,'prune',w/'full',w/'pruned',3,4,4,3)
  _,pruned,_=read_model(w/'pruned');check_mass(vocab,pruned)
  assert all(len(k)==1 or k[:-1] in pruned for k in pruned)
  native_probes=0
  if a.native_lib:
   descriptor=w/'dynamic.txt';descriptor.write_text('mixture3\n'+''.join(str(w/name)+'\n' for name in 'abc'))
   script=w/'probe.lua';script.write_text("local m=assert(package.loadlib(arg[1],'luaopen_shape5'))(); for line in io.lines() do local s=m.load(arg[2]); local p; for t in line:gmatch('[^\\t]+') do p,s=m.step(s,t) end; print(string.format('%.17g',p/math.log(10))) end")
   sequences=[seq for n in range(1,6) for seq in itertools.product(['a','b','aa'],repeat=n)]
   result=subprocess.run(['lua',str(script),str(a.native_lib),str(descriptor)],input=''.join('\t'.join(seq)+'\n' for seq in sequences),capture_output=True,text=True,check=True)
   values=list(map(float,result.stdout.splitlines()));assert len(values)==len(sequences)
   for seq,value in zip(sequences,values):
    key=('<s>',*seq)[-5:];expected=math.log10(sum(weight*10**ext(r,key) for weight,r in zip((.5,.25,.25),packed)))
    assert abs(value-expected)<1e-12,(key,value,expected);native_probes+=1
  a.output.write_text(json.dumps(dict(passed=True,probability_oracle_probes=probes,native_dynamic_probes=native_probes,reference_bytes_identical=bool(a.reference_budget),thread_and_resume_bytes_identical=True,pruned_normalization_and_prefix_closure=True),indent=2))
  print(a.output.read_text())
if __name__=='__main__':main()
