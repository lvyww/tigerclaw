#!/usr/bin/env python3
"""File-backed C#/C++ mainline differential: no production pipes; journal tests use fresh isolated files.

Build both probes first. Pass the actual frozen Q8 model and an isolated output
folder. On WSL Windows executable arguments are converted to drive paths.
"""
import argparse,json,random,subprocess,pathlib,math,hashlib,sys,tempfile,os
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--native',type=pathlib.Path,required=True)
parser.add_argument('--model',type=pathlib.Path,required=True)
parser.add_argument('--output',type=pathlib.Path,required=True)
parser.add_argument('--dotnet',default='/mnt/c/Program Files/dotnet/dotnet.exe')
parser.add_argument('--csharp',type=pathlib.Path)
parser.add_argument('--shape-table',type=pathlib.Path,help='Optional frozen text<TAB>code table for actual schema cases')
args=parser.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]
out=args.output.resolve();out.mkdir(exist_ok=True,parents=True)
def win(p):
 p=str(p)
 return p[5].upper()+':'+p[6:].replace('/','\\') if p.startswith('/mnt/') and len(p)>6 and p[6]=='/' else p
model=args.model.resolve()
csharp=args.csharp or root/'next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll'
def write_input(path,text):
 data=text.encode('utf-8')
 with path.open('wb') as file:
  file.write(data);file.flush();os.fsync(file.fileno())
 if path.read_bytes()!=data:raise IOError('Probe input readback differs from generated JSON: '+str(path))
random.seed(20261008)
cases=[]
for i in range(24):
 events=[dict(code=random.choice(['aabb','aa','bb','aabbcc']),text=random.choice(['中国','中华','甲乙','甲','乙','中华人民共和国']),context=random.choice(['','人','中国']),levels=random.randrange(1,4)) for _ in range(i*3)]
 queries=[dict(code=c,text=t,context=x) for c in ['','aa','bb','aab','aabb','aabbcc'] for t in ['','甲','乙','甲乙','中','中国','中华','中华人民共和国'] for x in ['','人','中国']]
 cases.append(dict(op='learning',events=events,queries=queries))
lex=[['aa',['中','甲','乙']],['bb',['国','文','华']],['cc',['人','民']],['aabb',['中国','中华','中文','甲文']],['a',['中']],['b',['国']]]
for canonical in [0,2]:
 for beam in [1,2,2000]:
  for levels in [0,1,3,9]:
   cases.append(dict(op='decode',lexicon=lex,canonical=canonical,lexical=.1 if levels%2 else 0,beam=beam,raws=['a','aa','aabb','aabbcc','aabbccaabb','aabbcc','aabbccaabb','aa2bb','aab','aabb'],events=[dict(code='aabb',text='乙华',levels=3) for _ in range(levels)]))
for canonical in [0,2]:
 for levels in [0,3]:
  cases.append(dict(op='decode',lexicon=lex,canonical=canonical,lexical=.1,lockRaw='aabb',lockIndex=1,
    raws=['aabb','aabbcc','aabbccaabb','aabbcc','aab','aabb','aabbcc'],
    events=[dict(code='aabb',text='乙华',levels=3) for _ in range(levels)]))
# Pair preferences reorder Direct/Composed subsequences without changing scores.
def configuration_hash(text):
 value=14695981039346656037
 for byte in text.encode('utf-16-le'):value=((value^byte)*1099511628211)&0xffffffffffffffff
 return format(value,'016x')
pair='~f'+configuration_hash('aabb\0D\0中国\0C\0乙文')
for winner in ['C','D']:
 events=[dict(mode='fusion-v1|test-v1',code=pair,text='C',levels=1)]
 if winner=='D':events.append(dict(mode='fusion-v1|test-v1',code=pair,text='D',levels=1))
 cases.append(dict(op='decode',lexicon=lex,raws=['aabb','aabbcc','aabb','aabb2'],events=events))
if args.shape_table:
 entries={}
 for line in args.shape_table.read_text(encoding='utf-8-sig').splitlines():
  if not line or line.startswith('#'):continue
  fields=line.split('\t')
  if len(fields)!=2:raise ValueError('Expected frozen text<TAB>code table')
  text,code=fields
  if text not in entries.setdefault(code,[]):entries[code].append(text)
 actual=list(entries.items())
 codes=[c for c in entries if c.isascii() and c.isalpha() and 2<=len(c)<=4]
 raws=['t','tl','tll','tlle','tlleo','tlle','tlleo']+[random.choice(codes)+random.choice(codes) for _ in range(16)]
 for corrections in [0,1,2]:
  cases.append(dict(op='decode',lexicon=actual,canonical=2,lexical=.1,isolation=True,protectedFactor=0,preserve=True,
    raws=raws,events=[dict(code='tlleo',text='陲机',levels=3) for _ in range(corrections)],
    supplements=[['陲机',1],['中华',1000],['中国人民',3000]]))
# Preserve the 64 CODE ROW limit even across wrong modes/equal-code rows.
large=[dict(code='aa'+str(i//100).zfill(3),mode='other' if i%7==0 else 'test-v1',
            text='学习'+chr(0x4e00+i),context='上下' if i%2 else '',levels=1+i%3) for i in range(10000)]
cases.append(dict(op='learning',events=large,queries=[dict(code=c,text=t,context=x)
    for c in ['a','aa','aa000','aa063','aa099','aa100'] for t in ['学','学习','学习中'] for x in ['','上下','他']]))
for native in [False,True]:
 path=out/('parity-cpp-input.jsonl' if native else 'parity-csharp-input.jsonl');dest=out/('parity-cpp.jsonl' if native else 'parity-csharp.jsonl')
 write_input(path,''.join(json.dumps(dict(c,model=str(model) if native and args.native.suffix.lower()!='.exe' else win(model)),ensure_ascii=False)+'\n' for c in cases))
 executable=args.native.resolve() if native else args.dotnet
 windows=str(executable).lower().endswith('.exe')
 path_arg=lambda p:win(p) if windows else str(p)
 cmd=[str(executable), '--mainline-probe',path_arg(path),path_arg(dest)] if native else [str(executable),path_arg(csharp),'--native-core-mainline-probe',path_arg(path),path_arg(dest)]
 with (out/('parity-cpp.log' if native else 'parity-csharp.log')).open('w') as log:subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=600)
a=[json.loads(x) for x in (out/'parity-csharp.jsonl').read_text(encoding='utf-8-sig').splitlines()];b=[json.loads(x) for x in (out/'parity-cpp.jsonl').read_text(encoding='utf-8-sig').splitlines()]
differences=[]
def compare(a,b,path):
 if isinstance(a,(float,int)) and not isinstance(a,bool) and isinstance(b,(float,int)):
  if not math.isclose(a,b,rel_tol=1e-11,abs_tol=1e-10):differences.append((path,a,b))
 elif type(a)!=type(b):differences.append((path,a,b))
 elif isinstance(a,dict):
  for k in a:compare(a[k],b.get(k),path+'/'+k)
 elif isinstance(a,list):
  if len(a)!=len(b):differences.append((path+'/length',len(a),len(b)))
  for i,(x,y) in enumerate(zip(a,b)):compare(x,y,path+'/'+str(i))
 elif a!=b:differences.append((path,a,b))
compare(a,b,'')
# Bidirectional readable-journal compatibility. All writes stay under --output.
journal=pathlib.Path(tempfile.mkdtemp(prefix='journal-',dir=out))/'自学习-虎爪.txt'
def journal_probe(native,events,name):
 executable=args.native.resolve() if native else args.dotnet
 windows=str(executable).lower().endswith('.exe')
 arg=lambda p:win(p) if windows else str(p)
 source=out/(name+'-input.jsonl');dest=out/(name+'-output.jsonl')
 write_input(source,json.dumps(dict(op='journal',path=arg(journal),events=events),ensure_ascii=False)+'\n')
 cmd=[str(executable),'--mainline-probe',arg(source),arg(dest)] if native else [str(executable),arg(csharp),'--native-core-mainline-probe',arg(source),arg(dest)]
 with (out/(name+'.log')).open('w') as log:subprocess.run(cmd,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=60)
 return json.loads(dest.read_text(encoding='utf-8-sig'))
event=dict(id='csharp',time=1720000000,mode='测试\\mode\t\n',code='tlleo',text='陲机',context='中𠀀',levels=3)
first=journal_probe(False,[event],'journal-csharp-write')
second=journal_probe(True,[dict(event,id='cpp')],'journal-cpp-append')
third=journal_probe(False,[],'journal-csharp-reload')
compare(second,third,'journal/reload')
compare(first,second[:1],'journal/preserved')
if len(first)!=1 or len(second)!=2:differences.append(('journal/counts',[1,2],[len(first),len(second)]))
(out/'parity-differences.json').write_text(json.dumps(differences,ensure_ascii=False,indent=2),encoding='utf-8')
print('cases',len(cases),'differences',len(differences));print(json.dumps(differences[:15],ensure_ascii=False,indent=2))

def digest(path):
 h=hashlib.sha256()
 with open(path,'rb') as file:
  for chunk in iter(lambda:file.read(1024*1024),b''):h.update(chunk)
 return h.hexdigest()
report=dict(cases=len(cases),learningQueries=sum(len(c.get('queries',[])) for c in cases),
            decodeGenerations=sum(len(c.get('raws',[])) for c in cases),differences=len(differences),
            modelSha256=digest(model),nativeSha256=digest(args.native),csharpSha256=digest(csharp),
            journalInterop=True,journalPath=str(journal),shapeTableSha256=digest(args.shape_table) if args.shape_table else None,physicalFrontendTested=False)
(out/'parity-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
sys.exit(bool(differences))
