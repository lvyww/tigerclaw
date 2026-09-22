import itertools,math,subprocess,tempfile
from pathlib import Path
root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/joint-5gram-beam-20260921')
def read(p):
 d={};n=0
 for line in p.read_text().splitlines():
  if line.startswith('\\') and '-grams:' in line:n=int(line[1]);continue
  if not n or not line or line.startswith('\\'):continue
  f=line.split()
  if not f:continue
  d[tuple(f[1:n+1])]=(float(f[0]),float(f[n+1]) if len(f)>n+1 else 0.)
 return d
def prob(d,h,t):
 key=h+(t,)
 if key in d:return 10**d[key][0]
 if not h:return 10**d[('<unk>',)][0]
 return 10**d.get(h,(0,0))[1]*prob(d,h[1:],t)
with tempfile.TemporaryDirectory(prefix='tiger-context-prune-') as td:
 p=Path(td);orders={1:[('-20','<unk>'),('0','<s>'),('-1','</s>'),(str(math.log10(.5)),'a'),(str(math.log10(.4)),'b')]}
 for n in range(2,6):
  orders[n]=[]
  for h in itertools.product(('a','b'),repeat=n-1):
   weights=(.5,.4,.1) if n<4 else (((.65,.25,.1) if h[0]=='a' else (.35,.55,.1)) if n==4 else ((.7,.2,.1) if h[0]=='a' else (.2,.7,.1)))
   for t,w in list(zip(('a','b','</s>'),weights))[:1 if n==5 else 3]:orders[n].append((str(math.log10(w)),' '.join(h+(t,))))
 text='\\data\\\n'+''.join(f'ngram {n}={len(v)}\n' for n,v in orders.items())
 for n,v in orders.items():text+=f'\n\\{n}-grams:\n'+''.join(f'{score}\t{tokens}'+ (('\t'+str(math.log10((1-(.7 if tokens.split()[0]=='a' else .2))/(1-(.65 if tokens.split()[1]=='a' else .35))))) if n==4 and '</s>' not in tokens else ('\t0' if n<5 else ''))+'\n' for score,tokens in v)
 (p/'in.arpa').write_text(text+'\n\\end\\\n')
 subprocess.run([str(root/'bin/context_prune'),str(p/'in.arpa'),str(p/'small.arpa'),str(p/'all.arpa'),'1,2'],check=True)
 original=read(p/'in.arpa');small=read(p/'small.arpa');all_=read(p/'all.arpa');assert original==all_
 checked=0
 for n in range(5):
  for h in itertools.product(('a','b'),repeat=n):
   for d in (original,small,all_):assert abs(sum(prob(d,h,t) for t in ('a','b','</s>'))-1)<1e-9;checked+=1
   if n>=3:
    for t in ('a','b','</s>'):
     effective=h
     while len(effective)>=3 and any(x!='a' for x in effective):effective=effective[1:]
     expected=prob(original,effective,t)
     assert abs(prob(small,h,t)-expected)<1e-9
 subprocess.run([str(root/'bin/build_binary'),'trie',str(p/'small.arpa'),str(p/'small.klm')],check=True,stdout=subprocess.DEVNULL)
 (p/'broken.arpa').write_text((p/'in.arpa').read_text().split('\\4-grams:')[0])
 bad=subprocess.run([str(root/'bin/context_prune'),str(p/'broken.arpa'),str(p/'bad1.arpa'),str(p/'bad2.arpa'),'1,2'],capture_output=True)
 assert bad.returncode!=0
 print('PASS normalization/context fallback, full-vocabulary identity, truncated-input rejection:',checked)
