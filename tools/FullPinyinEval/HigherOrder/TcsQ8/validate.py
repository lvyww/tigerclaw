"""Independent Python scoring oracle, sampled structure and quantization checks."""
import argparse, bisect, json, math, mmap, random, struct, subprocess
from pathlib import Path

class Model:
    def __init__(self,path):
        self.file=open(path,'rb'); self.data=mmap.mmap(self.file.fileno(),0,access=mmap.ACCESS_READ)
        self.version=self.u32(8);self.qbytes=1 if self.version==2 else 2
        assert self.version in (1,2) and self.data[:8]==b'TCSKNM03'
        assert self.u64(16)==len(self.data)
        self.bos=self.u16(58);self.unk=self.u16(56);self.tokens=[];self.uni=[];self.quant=[]
        for i in range(5):
            a,b,c,d=struct.unpack_from('<iIiI',self.data,160+16*i)
            self.quant.append((a/1e7,b/(1e9 if self.version==2 else 1e12),c/1e7,d/(1e9 if self.version==2 else 1e12)))
        p=self.u64(40)
        for _ in range(self.u32(28)):
            n=self.u16(p);p+=2;self.tokens.append(self.data[p:p+n].decode());p+=n
            self.uni.append((self.q(p),self.q(p+self.qbytes)));p+=2*self.qbytes
        assert p==self.u64(40)+self.u64(48)
        self.dirs={};self.index={}
        for order in range(2,6):
            self.dirs[order]=[struct.unpack_from('<QQQIIQ',self.data,self.u64(64+24*(order-2))+40*b) for b in range(256)]
    def u16(self,p):return struct.unpack_from('<H',self.data,p)[0]
    def u32(self,p):return struct.unpack_from('<I',self.data,p)[0]
    def u64(self,p):return struct.unpack_from('<Q',self.data,p)[0]
    def q(self,p):return self.data[p] if self.qbytes==1 else self.u16(p)
    def prob(self,order,q):
        a,b,_,_=self.quant[order-1];return a+b*q
    def bow(self,order,q):
        _,_,a,b=self.quant[order-1];return 0 if q==0 else a+b*(q-1)
    def block(self,order,p):
        ctx=struct.unpack_from('<'+'H'*(order-1),self.data,p);p+=2*(order-1)
        bow=self.q(p);p+=self.qbytes;n=self.u16(p);p+=2
        successors={self.u16(p+i*(2+self.qbytes)):self.q(p+i*(2+self.qbytes)+2) for i in range(n)}
        return ctx,bow,successors,p+n*(2+self.qbytes)
    def lookup(self,history,target):
        order=len(history)+1;b=history[0]%256;meta=self.dirs[order][b]
        key=(order,b)
        if key not in self.index:
            self.index[key]=[(struct.unpack_from('<'+'H'*(order-1),self.data,meta[2]+16*i),self.u64(meta[2]+16*i+8)) for i in range(meta[3])]
        ix=self.index[key];i=bisect.bisect_right(ix,(tuple(history),2**64))-1
        if i<0:return None,0
        p=ix[i][1];end=ix[i+1][1] if i+1<len(ix) else meta[2]
        while p<end:
            ctx,bow,succ,p=self.block(order,p)
            if ctx==tuple(history):return (self.prob(order,succ[target]) if target in succ else None),self.bow(order-1,bow)
            if ctx>tuple(history):break
        return None,0
    def score(self,h,t):
        total=0
        for n in range(min(4,len(h)),0,-1):
            p,b=self.lookup(h[-n:],t)
            if p is not None:return (total+p)*math.log(10)
            total+=b
        return (total+self.prob(1,self.uni[t][0]))*math.log(10)

LUA=r'''
package.path=arg[1]..'/lua/?.lua;'..package.path
local m=require('tiger_sentence_fivegram').load(arg[2])
local out=assert(io.open(arg[4],'w'))
for line in io.lines(arg[3]) do
 local fields={};for v in line:gmatch('[^\t]+')do fields[#fields+1]=v end
 local a,b,c,d,n=m.bos_id,0,0,0,1
 local score
 for i=1,#fields do score,a,b,c,d,n=m.step(a,b,c,d,n,fields[i]) end
 out:write(string.format('%.17g\n',score))
end
out:close();m.close()
'''

def main():
    p=argparse.ArgumentParser();p.add_argument('q16',type=Path);p.add_argument('q8',type=Path);p.add_argument('reader',type=Path);p.add_argument('out',type=Path);a=p.parse_args()
    a.out.mkdir(parents=True,exist_ok=True);old,new=Model(a.q16),Model(a.q8)
    assert old.tokens==new.tokens
    rng=random.Random(20260924);samples=[];checked=0;max_p=max_b=0
    for order in range(2,6):
        for bucket in rng.sample(range(256),32):
            om,nm=old.dirs[order][bucket],new.dirs[order][bucket]
            assert om[3:]==nm[3:]
            if not om[3]:continue
            for idx in {0,om[3]-1,rng.randrange(om[3])}:
                op=old.u64(om[2]+16*idx+8);np=new.u64(nm[2]+16*idx+8)
                for _ in range(min(3,om[4]-idx*old.u32(36))):
                    oc,ob,os,op=old.block(order,op);nc,nb,ns,np=new.block(order,np)
                    assert oc==nc and os.keys()==ns.keys() and (ob==0)==(nb==0)
                    db=abs(old.bow(order-1,ob)-new.bow(order-1,nb));max_b=max(max_b,db)
                    assert db<=new.quant[order-2][3]/2+2e-6
                    for t,q in os.items():
                        dp=abs(old.prob(order,q)-new.prob(order,ns[t]));max_p=max(max_p,dp)
                        assert dp<=new.quant[order-1][1]/2+2e-6
                    if os:samples.append((oc,rng.choice(list(os))))
                    samples.append((oc,rng.randrange(len(old.tokens))))
                    checked+=1
    # Exercise unseen history, OOV, EOS and BOS-backed ordinary paths as well.
    token_id={s:i for i,s in enumerate(new.tokens)}
    requests=[]
    for h,t in samples:
        text=[new.tokens[i] for i in h if i!=new.bos]+[new.tokens[t]]
        requests.append(text)
    requests += [['你','好','</s>'],['not-in-vocabulary','的','</s>'],['</s>']]
    # Map stored special token names to the runtime's BOS/EOS convention.
    def runtime(t):return '\x02' if t=='<s>' else '\x03' if t=='</s>' else t
    requests=[[runtime(t) for t in seq] for seq in requests]
    (a.out/'queries.tsv').write_text('\n'.join('\t'.join(seq) for seq in requests)+'\n')
    (a.out/'probe.lua').write_text(LUA)
    result=dict(sampled_blocks=checked,queries=len(requests),max_log10_probability_error=max_p,max_log10_backoff_error=max_b,runs=[])
    for m,path in [(old,a.q16),(new,a.q8)]:
        expected=[]
        for seq in requests:
            h=[m.bos]
            for t in seq:
                tid=m.bos if t=='\x02' else m.u16(60) if t=='\x03' else token_id.get(t,m.unk)
                score=m.score(h,tid);h=(h+[tid])[-4:]
            expected.append(score)
        for lua in ('lua','luajit'):
            dest=a.out/f'scores-v{m.version}-{lua}.txt'
            subprocess.run([lua,str(a.out/'probe.lua'),str(a.reader),str(path),str(a.out/'queries.tsv'),str(dest)],check=True)
            actual=list(map(float,dest.read_text().splitlines()));assert len(actual)==len(expected)
            error=max(abs(x-y) for x,y in zip(expected,actual));assert error<1e-10,error
            result['runs'].append(dict(version=m.version,lua=lua,max_score_error=error))
    (a.out/'validation.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))

if __name__=='__main__':main()
