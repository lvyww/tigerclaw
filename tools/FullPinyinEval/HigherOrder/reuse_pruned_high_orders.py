"""Reuse identical high-order context pruning while varying lower-order limits."""
import argparse
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('full',type=Path);p.add_argument('pruned',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
def header(f):
 counts={}
 for line in f:
  if line.startswith(b'ngram '):n,c=line.split(b'=');counts[int(n[6:])]=int(c)
  if line.strip()==b'\\1-grams:':return counts
 raise ValueError('missing unigrams')
def until(f,marker,out=None):
 tail=b''
 while True:
  chunk=f.read(8*1024*1024)
  if not chunk:raise ValueError('missing section')
  data=tail+chunk;i=data.find(marker)
  if i>=0:
   if out:out.write(data[:i])
   f.seek(-(len(data)-i-len(marker)),1)
   return
  keep=len(marker)-1
  if out:out.write(data[:-keep])
  tail=data[-keep:]
with a.full.open('rb') as full,a.pruned.open('rb') as pruned,a.output.open('xb') as out:
 before=header(full);after=header(pruned);assert before[1]==after[1]
 counts={n:before[n] if n<=3 else after[n] for n in range(1,6)}
 out.write(b'\\data\\\n'+b''.join(f'ngram {n}={counts[n]}\n'.encode() for n in range(1,6))+b'\n\\1-grams:\n')
 until(full,b'\n\\4-grams:\n',out)
 out.write(b'\n\\4-grams:\n')
 until(pruned,b'\n\\4-grams:\n')
 import shutil
 shutil.copyfileobj(pruned,out,8*1024*1024)
print(counts)
