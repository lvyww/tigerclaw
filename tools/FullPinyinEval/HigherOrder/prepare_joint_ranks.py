"""One shared character ranking for suffix-closed pruning of the .7/.3 mixture."""
import argparse
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('old',type=Path);p.add_argument('new',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
def unigrams(path):
 out={};active=False
 with path.open() as f:
  for line in f:
   if line.startswith('\\2-grams:'):break
   if line.startswith('\\1-grams:'):active=True;continue
   if active and line.strip():
    fields=line.split();out[fields[1]]=10**float(fields[0])
 return out
old,new=unigrams(a.old),unigrams(a.new)
words=(old.keys()|new.keys())-{'<s>','</s>','<unk>'}
rank=sorted(words,key=lambda t:(-(.7*old.get(t,0)+.3*new.get(t,0)),t))
a.output.write_text(''.join(f'{t}\t{i+1}\n' for i,t in enumerate(rank)))
print(len(rank),'ordinary characters; absent component vocabulary uses zero mass')
