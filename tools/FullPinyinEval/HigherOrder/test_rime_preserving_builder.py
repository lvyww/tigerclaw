"""Check rare-history preservation and default conversion compatibility."""
import argparse,json,math,struct,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--builder',type=Path,required=True);p.add_argument('--rime',type=Path,required=True);p.add_argument('--work',type=Path,required=True);a=p.parse_args()
a.work.mkdir(parents=True,exist_ok=True)
chars=[chr(0x4e00+i) for i in range(130)];rare=chars[-1];head=chars[0]
r={1:[('-6','<unk>','0'),('0','<s>','0'),('-2','</s>','0')]+[(str(-3-i*.001),c,'-.1') for i,c in enumerate(chars)],2:[('-.5',rare+' '+head,'-.2')],3:[('-.4',rare+' '+head+' '+head,'-.3')],4:[('-.2',rare+' '+head+' '+head+' '+head,'-.4')],5:[('-.1',rare+' '+head+' '+head+' '+head+' </s>','')]}
for n in range(2,6):r[n].append(('-.6',' '.join([head]*(n-1)+(['</s>'] if n==5 else [head])),'0' if n<5 else ''))
lines=['\\data\\']+[f'ngram {n}={len(r[n])}' for n in range(1,6)]
for n in range(1,6):
 lines+=['',f'\\{n}-grams:']+['\t'.join(x).rstrip('\t') for x in r[n]]
lines+=['','\\end\\'];arpa=a.work/'rare-history.arpa';arpa.write_text('\n'.join(lines)+'\n')
for name,flags in [('default',[]),('preserved',['--preserve-all'])]:
 model=a.work/f'{name}.bin'
 with (a.work/f'{name}.log').open('w') as f:subprocess.run([str(a.builder),str(arpa),str(model),str(a.work/f'tmp-{name}'),*flags],stdout=f,stderr=subprocess.STDOUT,check=True)
 header=model.read_bytes()[:256];assert header[:8]==b'TCSKNM03'
 counts=[struct.unpack_from('<Q',header,64+i*24+16)[0] for i in range(4)]
 assert counts==([2,2,1,1] if name=='default' else [2,2,2,2]),counts
lua=a.work/'verify.lua'
lua.write_text('''package.path=arg[1]..'/lua/?.lua;'..package.path
local m=require('tiger_sentence_fivegram').load(arg[2],{})
local a,b,c,d,n=m.bos_id,0,0,0,1
local values={}
for _,token in ipairs({arg[3],arg[4],arg[4],arg[4],'\\3'}) do
 local v;v,a,b,c,d,n=m.step(a,b,c,d,n,token);values[#values+1]=v/math.log(10)
end
assert(math.abs(values[2]+.5)<.0002)
assert(math.abs(values[3]+.4)<.0002)
assert(math.abs(values[4]+.2)<.0002)
assert(math.abs(values[5]+.1)<.0002)
m.close();print('PASS rare history 2/3/4/5-gram scores')
''')
runtimes=[]
import shutil
for runtime in ['lua','luajit']:
 if shutil.which(runtime):
  result=subprocess.run([runtime,str(lua),str(a.rime),str(a.work/'preserved.bin'),rare,head],check=True,capture_output=True,text=True)
  runtimes.append(runtime);print(runtime,result.stdout.strip())
(a.work/'result.json').write_text(json.dumps(dict(default_counts=[133,2,2,1,1],preserved_counts=[133,2,2,2,2],runtimes=runtimes,status='pass'),indent=2)+'\n')
