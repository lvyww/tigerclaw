from pathlib import Path
import shutil
import sys
from budget500 import E, O

source = O / ((sys.argv[1] if len(sys.argv) > 1 else 'combo') + '-source')
source.mkdir(exist_ok=False)
for p in (E / 'bench-source').iterdir():
    if p.is_file() and p.suffix in ('.cs', '.csproj'):
        shutil.copy2(p, source / p.name)
shutil.copy2(Path(__file__).with_name('BudgetReranker.cs'), source / 'BudgetReranker.cs')
p = source / 'Program.cs'
s = p.read_text()
marker = 'string mode=args[0],type=args[1];'
assert s.count(marker) == 1
s = s.replace(marker, marker + '\n        var rrPath=Environment.GetEnvironmentVariable("BUDGET_RERANK_MODEL"); using var reranker=rrPath==null?null:new BudgetReranker(rrPath);')
for statement in ('var r=decoder.Decode(c.Code[..i]);', 'var r=decoder.Decode(c.Code,50,false);'):
    assert statement in s
    s = s.replace(statement, statement + 'if(reranker!=null)r=reranker.Rerank(r);')
marker = 'decoderAssembly=Hash(typeof(Decoder).Assembly.Location),beam'
assert marker in s
s = s.replace(marker, 'decoderAssembly=Hash(typeof(Decoder).Assembly.Location),rerankModel=rrPath==null?null:Hash(rrPath),rerankAlpha=Environment.GetEnvironmentVariable("BUDGET_RERANK_ALPHA")??"1",beam')
p.write_text(s)
print(source)
