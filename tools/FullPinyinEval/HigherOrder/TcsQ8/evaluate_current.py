"""Matched Q16/Q8 evaluation with the actual current Rime decoder and priors."""
import concurrent.futures, csv, hashlib, json, os, subprocess
from pathlib import Path

def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

def main():
    import argparse
    p=argparse.ArgumentParser();p.add_argument('work',type=Path);p.add_argument('cases',type=Path);a=p.parse_args()
    lines=a.cases.read_text().splitlines();expected={r.split('\t')[0]:r.split('\t')[1:] for r in lines};assert len(lines)==len(expected)
    exporter=Path(__file__).resolve().parents[1]/'export_shape_pool.lua'
    env=dict(os.environ)
    for key in ('SHAPE5_MODEL','SHAPE5_LIB','TCS03_READER_ROOT'):env.pop(key,None)
    jobs=[]
    for variant,root in [('q16',a.work/'current-q16'),('q8',a.work/'package')]:
        out=a.work/f'current-{variant}-eval';out.mkdir(exist_ok=True)
        (out/'manifest.json').write_text(json.dumps(dict(cases_sha256=sha(a.cases),decoder_sha256=sha(root/'lua/tiger_sentence.lua'),reader_sha256=sha(root/'lua/tiger_sentence_fivegram.lua'),model_sha256=sha(root/'models/sentence-fivegram-mobile.bin'),llm=False,learning=False,early_commit=False,production_changed=False),indent=2))
        for i in range(4):
            part=out/f'cases-{i}.tsv';part.write_text('\n'.join(lines[i::4])+'\n');jobs.append((root,out,i,part))
    def run(job):
        root,out,i,part=job
        with (out/f'worker-{i}.log').open('w')as log:subprocess.run(['lua',str(exporter),str(root),str(part),str(out/f'pool-{i}.tsv')],env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8)as ex:list(ex.map(run,jobs))
    for variant in ('q16','q8'):
        out=a.work/f'current-{variant}-eval';groups={}
        for i in range(4):
            with (out/f'pool-{i}.tsv').open()as f:
                for row in csv.DictReader(f,delimiter='\t'):
                    assert [row[k]for k in ('source','code','target')]==expected[row['id']]
                    groups.setdefault(row['id'],[]).append(row)
        assert groups.keys()==expected.keys();pred=[]
        for ident,(source,code,target)in expected.items():
            rows=sorted(groups[ident],key=lambda r:int(r['index']))
            pred.append(dict(id=ident,source=source,code=code,target=target,prediction=rows[0]['text'],rank=next((int(r['index'])for r in rows if r['text']==target),0)))
        (out/'predictions.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n'for r in pred))
        summary={}
        for source in ['combined']+sorted({r['source']for r in pred}):
            rs=[r for r in pred if source=='combined' or r['source']==source]
            summary[source]=dict(n=len(rs),correct=sum(r['prediction']==r['target']for r in rs),top5=sum(0<r['rank']<=5 for r in rs),top20=sum(0<r['rank']<=20 for r in rs))
        (out/'summary.json').write_text(json.dumps(summary,indent=2));print(variant,summary,flush=True)

if __name__=='__main__':main()
