"""Exhaustive raw/accelerated scoring and byte parity, including sparse ARPA gaps."""
import argparse
import itertools
import json
from pathlib import Path
import subprocess
import tempfile
from test_mixture_budget import toy,write_arpa


def main():
    p=argparse.ArgumentParser()
    for x in ('pack','raw','fast','builder','output'):p.add_argument(x,type=Path)
    p.add_argument('--fivegram',action='store_true')
    a=p.parse_args()
    def run(*args):subprocess.run([str(x) for x in args],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    def accelerate(directory):
        run(a.fast,'export4',directory,directory/'lower4.arpa')
        run(a.builder,'trie',directory/'lower4.arpa',directory/'lower4.klm')
        if a.fivegram:
            run(a.fast,'export5',directory,directory/'full5.arpa')
            run(a.builder,'trie',directory/'full5.arpa',directory/'full5.klm')
    with tempfile.TemporaryDirectory(prefix='budget-fast4-test-') as tmp:
        d=Path(tmp)
        for name,words,seed in [('a',['a','b'],52),('b',['b','c'],61)]:write_arpa(d/(name+'.arpa'),toy(words,seed))
        run(a.pack,'vocab',d/'a.arpa',d/'b.arpa',d/'vocab.txt')
        for name in ('a','b'):
            run(a.pack,'pack',d/'vocab.txt',d/(name+'.arpa'),d/name,1,3)
            accelerate(d/name)
        vocab=(d/'vocab.txt').read_text().splitlines()
        req=''.join('\t'.join(k)+'\n' for n in range(1,6) for k in itertools.product(vocab,repeat=n))
        maximum=0
        for name in ('a','b'):
            outputs=[]
            for tool in (a.raw,a.fast):
                x=subprocess.run([str(tool),'score',str(d/name)],input=req,text=True,capture_output=True,check=True)
                outputs.append(list(map(float,x.stdout.splitlines())))
            errors=[abs(x-y) for x,y in zip(*outputs)];maximum=max(maximum,max(errors))
            assert max(errors)==0,(name,max(errors))
        for alpha in (.1,.25):
            for label,tool in [('raw',a.raw),('fast',a.fast)]:run(tool,'mix',d/'a',d/'b',d/(label+str(alpha)),alpha,2)
            original=[(d/('raw'+str(alpha))/f'{n}.bin').read_bytes() for n in range(1,6)]
            fast=d/('fast'+str(alpha))
            assert original==[(fast/f'{n}.bin').read_bytes() for n in range(1,6)]
            accelerate(fast)
            run(a.fast,'mix',d/'a',d/'b',fast,alpha,2,5)
            assert original==[(fast/f'{n}.bin').read_bytes() for n in range(1,6)]
        result=dict(source_scalar_cases_each=len(errors),max_abs_log10_error=maximum,
                    materialized_and_resumed_bytes_identical=True,weights=[.1,.25],fivegram=a.fivegram)
        a.output.write_text(json.dumps(result,indent=2));print(a.output.read_text())


if __name__=='__main__':main()
