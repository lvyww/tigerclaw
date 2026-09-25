"""Exhaustively verify common suffix-closed context pruning commutes with mixing."""
import argparse,itertools,json,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--pruner',type=Path,required=True);a=p.parse_args();w=a.work
ranks=w/'toy-ranks.tsv';ranks.write_text('甲\t1\n乙\t2\n')
for i in [0,1]:
 with (w/f'toy-prune{i}.log').open('w') as f:subprocess.run([str(a.pruner),str(w/f'toy{i}.arpa'),str(w/f'toy{i}-cut.arpa'),str(w/f'toy{i}-all.arpa'),'65535:1:1:1,65535:2:2:2',str(ranks)],stdout=f,stderr=subprocess.STDOUT,check=True)
def read(p):
 d={};n=0
 for line in p.read_text().splitlines():
  if '-grams:' in line:n=int(line[1]);continue
  if not line.strip() or line.startswith('\\') or not n:continue
  f=line.split();d[tuple(f[1:n+1])]=(10**float(f[0]),10**float(f[n+1]) if len(f)>n+1 else 1.)
 return d
def prob(d,h,t):
 key=h+(t,)
 if key in d:return d[key][0]
 if not h:return 0.
 return d.get(h,(0.,1.))[1]*prob(d,h[1:],t)
original=[read(w/f'toy{i}.arpa') for i in [0,1]];cut=[read(w/f'toy{i}-cut.arpa') for i in [0,1]]
count=0;maximum=0.
for length in range(5):
 for h in itertools.product(['甲','乙','<s>'],repeat=length):
  shortened=h
  while len(shortened)>1 and '乙' in shortened:shortened=shortened[1:]
  for t in ['甲','乙','<unk>','</s>']:
   expected=sum(weight*prob(d,shortened,t) for d,weight in zip(original,[.7,.3]))
   got=sum(weight*prob(d,h,t) for d,weight in zip(cut,[.7,.3]))
   maximum=max(maximum,abs(expected-got));count+=1
assert maximum<1e-8,maximum
r=dict(checks=count,max_error=maximum,status='pass');(w/'joint-context-test.json').write_text(json.dumps(r,indent=2)+'\n');print(r)
