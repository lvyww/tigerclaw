from pathlib import Path
import shutil,json,hashlib
repo=Path.cwd();root=Path('/mnt/c/Archive/tigerclaw_sentence_ml/experiments/joint-5gram-beam-20260921');src=root/'source'
files=list((repo/'next/TigerClaw.Pinyin').glob('*.cs'))+list((repo/'next/TigerClaw.Core').glob('SentenceNgramModel*.cs'))+list((repo/'tools/FullPinyinEval/Joint').glob('*.cs'))+[repo/'tools/FullPinyinEval/Tests.cs']
manifest={}
for p in files:
 shutil.copy2(p,src/p.name);manifest[str(p)] = hashlib.sha256(p.read_bytes()).hexdigest()
p=src/'Decoder.cs';s=p.read_text();assert s.count('a = b;')==3;s=s.replace('a = b;', 'a = model is Kenlm historyModel ? historyModel.Advance(a, b) : b;');p.write_text(s)
for name in ['Program.cs','JointTests.cs']:
 p=src/name;s=p.read_text().replace('a=b;b=c;', 'a=lm.Advance(a,b);b=c;');p.write_text(s)
(src/'Joint.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>''')
(root/'source-original-hashes.json').write_text(json.dumps(manifest,indent=2))

shutil.copy2(repo/"tools/FullPinyinEval/HigherOrder/KenlmHistory.cs",src/"Kenlm.cs")
