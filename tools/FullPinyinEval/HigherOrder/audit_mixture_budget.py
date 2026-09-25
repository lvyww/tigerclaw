"""Independent mmap scoring and conditional-mass checks for compact float models."""
import argparse
import bisect
import json
import math
import mmap
from pathlib import Path
import random
import struct
import subprocess
import sys

REC = struct.Struct('<5HHfff')


class Model:
    def __init__(self, path):
        self.path = Path(path)
        self.vocab = (self.path/'vocab.txt').read_text().splitlines()
        self.ids = {s:i for i,s in enumerate(self.vocab)}
        self.unk = self.ids['<unk>']
        self.data = {}
        self.files = []
        self.counts = {}
        self.starts = {}
        for n in range(1,6):
            f = (self.path/f'{n}.bin').open('rb')
            self.files.append(f)
            size = (self.path/f'{n}.bin').stat().st_size
            assert size%24 == 0
            self.data[n] = mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ) if size else b''
            self.counts[n] = size//24
            starts = []
            lo = 0
            for word in range(len(self.vocab)):
                hi = size//24
                while lo<hi:
                    mid=(lo+hi)//2
                    if struct.unpack_from('<H', self.data[n],mid*24)[0]<word:
                        lo=mid+1
                    else:
                        hi=mid
                starts.append(lo)
            starts.append(size//24)
            self.starts[n]=starts
        self.known={self.record(1,i)[0][0] for i in range(self.counts[1])}
        self.slots=1+len(self.vocab)-len(self.known)

    def record(self,n,i):
        r=REC.unpack_from(self.data[n],i*24)
        return tuple(r[:n]),r[6],r[7],r[8]

    def find(self,k):
        n=len(k);lo,hi=self.starts[n][k[0]:k[0]+2]
        while lo<hi:
            mid=(lo+hi)//2
            if self.record(n,mid)[0]<k:
                lo=mid+1
            else:
                hi=mid
        if lo<self.counts[n]:
            r=self.record(n,lo)
            if r[0]==k:
                return r

    def score(self,k):
        r=self.find(k)
        if r:
            return r[1]
        assert len(k)>1,k
        h=self.find(k[:-1])
        return (h[2] if h else 0)+self.score(k[1:])

    def extended(self,k):
        mapped=tuple(x if x in self.known else self.unk for x in k)
        return self.score(mapped)-(math.log10(self.slots) if mapped[-1]==self.unk else 0)


def main():
    p=argparse.ArgumentParser()
    p.add_argument('model',type=Path);p.add_argument('executable',type=Path);p.add_argument('output',type=Path)
    p.add_argument('--sources',nargs='+',type=Path);p.add_argument('--alpha',type=float)
    p.add_argument('--weights',nargs='+',type=float)
    p.add_argument('--q16',type=Path);p.add_argument('--q8',type=Path)
    p.add_argument('--backoff-context-probes',action='store_true')
    a=p.parse_args();m=Model(a.model);rng=random.Random(20260924)
    samples=[]
    for n in range(1,6):
        if not m.counts[n]:continue
        for i in {0,m.counts[n]-1,*[rng.randrange(m.counts[n]) for _ in range(128)]}:
            k,pr,bo,loss=m.record(n,i)
            assert math.isfinite(pr) and math.isfinite(bo) and not math.isnan(loss)
            if n>1:assert m.find(k[:-1]),('missing prefix',k)
            samples.append(k)
    samples += [tuple(rng.randrange(len(m.vocab)) for _ in range(n)) for n in range(1,6) for _ in range(64)]
    if a.backoff_context_probes:
        samples += [m.record(4,rng.randrange(m.counts[4]))[0]+(rng.randrange(len(m.vocab)),) for _ in range(128)]
    requests=''.join('\t'.join(m.vocab[x] for x in k)+'\n' for k in samples)
    got=subprocess.run([str(a.executable),'score',str(a.model)],input=requests,text=True,capture_output=True,check=True)
    native=list(map(float,got.stdout.splitlines()));assert len(native)==len(samples)
    maximum=max(abs(x-m.extended(k)) for x,k in zip(native,samples))
    assert maximum<1e-10,maximum
    mass_errors={}
    for text in ('','游戏','你好','𠀀'):
        history=tuple(m.ids.get(x,m.unk) for x in text)
        mass=sum(10**m.score(history+(i,)) for i,w in enumerate(m.vocab) if w!='<s>')
        mass_errors[text]=mass-1
        assert abs(mass-1)<3e-5,(text,mass)
    report=dict(counts=m.counts,scalar_samples=len(samples),max_native_oracle_log10_error=maximum,conditional_mass_errors=mass_errors)
    if a.q16 or a.q8:
        sys.path.insert(0,str(Path(__file__).parent/'TcsQ8'))
        from validate import Model as Quantized
        report['quantization']={}
        for path in (a.q16,a.q8):
            if path is None:continue
            q=Quantized(path);ids={s:i for i,s in enumerate(q.tokens)}
            assert set(ids)==set(m.vocab)
            errors=[]
            for k in samples:
                mapped=[ids[m.vocab[i]] for i in k]
                actual=q.score(mapped[:-1],mapped[-1])/math.log(10)
                errors.append(abs(actual-m.score(k)))
            bound=max(x[1] for x in q.quant)/2+4*max(x[3] for x in q.quant)/2+3e-5
            assert max(errors)<=bound,(path,max(errors),bound)
            report['quantization'][path.name]=dict(samples=len(errors),max_abs_log10_error=max(errors),bound=bound)
    if a.sources:
        sources=[Model(x) for x in a.sources]
        assert all(source.vocab==m.vocab for source in sources)
        weights=a.weights if a.weights else [1-a.alpha,a.alpha]
        assert len(weights)==len(sources) and all(x>0 for x in weights) and abs(sum(weights)-1)<1e-12
        errors=[];observed=[]
        for k in samples:
            values=[source.extended(k) for source in sources]
            z=max(values);expected=z+math.log10(sum(weight*10**(value-z) for weight,value in zip(weights,values)))
            delta=m.score(k)-expected;errors.append(delta)
            if m.find(k):observed.append(delta)
        assert max(map(abs,observed))<3e-5
        report['materialization']=dict(samples=len(errors),observed_samples=len(observed),
            observed_max_abs_log10_error=max(map(abs,observed)),
            all_max_abs_log10_error=max(map(abs,errors)),all_mean_abs_log10_error=sum(map(abs,errors))/len(errors),
            note='Union support probabilities agree; omitted events use approximate normalized backoff.')
    a.output.write_text(json.dumps(report,ensure_ascii=False,indent=2));print(a.output.read_text())


if __name__=='__main__':main()
