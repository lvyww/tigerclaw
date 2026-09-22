"""Frozen old/fresh 20k Tiger shape evaluation, trigram pools + character 5gram.
No latency evaluation. Preserve code/lexical/isolation priors by replacing only
trigram log probability. Alpha 0.5 is fixed in advance, not fitted on these sets.
"""
import argparse, concurrent.futures as F, csv, ctypes as C, hashlib, json, math, shutil, subprocess
from collections import defaultdict
from pathlib import Path

def digest(p):
    with p.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()
def dump(p,v): p.write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n')
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--output',type=Path,required=True);ap.add_argument('--workers',type=int,default=8);args=ap.parse_args()
    repo=Path(__file__).resolve().parents[3];archive=Path('/mnt/c/Archive/tigerclaw_sentence_ml')
    prior=repo/'next/_run/original-size-sweep-20260920';frozen=repo/'next/_run/ngram-214-materialize-20260919/pruned214'
    out=args.output;out.mkdir(parents=True,exist_ok=True)
    ref=json.loads((prior/'full_m5-metadata.json').read_text());m3=Path(ref['source']);m5=archive/'brightmart-char5-20260922/char5-q8.klm'
    assert digest(m3)==ref['sha256'];assert digest(prior/'cases.tsv')==ref['cases_sha256']
    manifest=json.loads((archive/'brightmart-char5-20260922/manifest.json').read_text());assert digest(m5)==manifest['validation']['sha256']
    cache_root=Path('/home/yc/tmp/tiger-char5-shape-20k');cache_root.mkdir(parents=True,exist_ok=True)
    cached=cache_root/'sentence-ngram-mobile.bin'
    if not cached.exists() or digest(cached)!=ref['sha256']:
        shutil.copy2(m3,cached)
    assert digest(cached)==ref['sha256']
    fixture=cache_root/'fixture';fixture.mkdir(exist_ok=True)
    for name,h in ref['input_hashes'].items():
        src=frozen/name;assert digest(src)==h,name
        dst=fixture/name;dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
    (fixture/'models').mkdir(exist_ok=True);link=fixture/'models/sentence-ngram-mobile.bin'
    if not link.exists():link.symlink_to(cached)
    lines=(prior/'cases.tsv').read_text().splitlines(keepends=True);assert len(lines)==20000
    shutil.copy2(prior/'cases.tsv',out/'cases.tsv')
    jobs=[]
    exporter=Path(__file__).with_name('export_shape_pool.lua')
    for i in range(args.workers):
        cases=out/f'cases-{i}.tsv';cases.write_text(''.join(lines[i::args.workers]));pool=out/f'pool-{i}.tsv'
        jobs.append((i,cases,pool))
    def run(job):
        i,cases,pool=job
        with (out/f'worker-{i}.log').open('w') as log:
            subprocess.run(['lua',str(exporter),str(fixture),str(cases),str(pool)],stdout=log,stderr=subprocess.STDOUT,check=True)
        print('pool complete',i,flush=True)
    with F.ThreadPoolExecutor(max_workers=args.workers) as executor:list(executor.map(run,jobs))
    grouped=defaultdict(list)
    for _,_,pool in jobs:
        for r in csv.DictReader(pool.open(),delimiter='\t'):
            for k in ('score','lm3'):r[k]=float(r[k])
            for k in ('index','rank','prefer_score'):r[k]=int(r[k])
            grouped[r['id']].append(r)
    baseline={r['id']:r for r in csv.DictReader((prior/'full_m5.tsv').open(),delimiter='\t')}
    assert len(grouped)==len(baseline)==20000
    libpath=archive/'experiments/joint-5gram-rerank-20260921/libhigherorder.so'
    lib=C.CDLL(str(libpath));lib.ho_load.argtypes=[C.c_char_p];lib.ho_load.restype=C.c_void_p
    lib.ho_score.argtypes=[C.c_void_p,C.c_char_p,C.POINTER(C.c_uint)];lib.ho_score.restype=C.c_double
    lib.ho_order.argtypes=[C.c_void_p];lib.ho_order.restype=C.c_uint
    lib.ho_free.argtypes=[C.c_void_p]
    model=lib.ho_load(str(m5).encode());assert model and lib.ho_order(model)==5
    modes={'baseline':(0,20),'fivegram_top5':(1,5),'fivegram_top20':(1,20),'fusion_half_top5':(.5,5),'fusion_half_top20':(.5,20)}
    cache={};results=[];summary={};deltas=defaultdict(list)
    with (out/'scored-pools.jsonl').open('w') as sink:
        for line in lines:
            id,source,code,target=line.rstrip('\n').split('\t');pool=sorted(grouped[id],key=lambda c:c['index']);b=baseline[id]
            assert [source,code,target]==[b['source'],b['code'],b['target']]
            assert pool[0]['text']==b['top1'],('baseline differs',id)
            rank=next((i+1 for i,c in enumerate(pool) if c['text']==target),0);assert rank==int(b['rank']),('rank differs',id)
            prefer=pool[0]['prefer_score']
            def key(c,alpha):
                score=c['score']+alpha*(c.get('lm5',0)-c['lm3'])
                return (-score,c['rank'],c['text']) if prefer else (c['rank'],-score,c['text'])
            assert min(pool,key=lambda c:key(c,0))['text']==b['top1']
            for c in pool:
                if c['text'] not in cache:
                    oov=C.c_uint();score=lib.ho_score(model,' '.join(c['text']).encode(),C.byref(oov))*math.log(10)
                    assert math.isfinite(score);cache[c['text']]=(score,oov.value)
                c['lm5'],c['oov']=cache[c['text']]
            row=dict(id=id,source=source,code=code,target=target,base=b['top1'],rank=rank,top5_oracle=0<rank<=5,top20_oracle=rank>0,any_oov=any(c['oov'] for c in pool),predictions={})
            for name,(alpha,k) in modes.items():
                pred=min(pool[:k],key=lambda c:key(c,alpha))['text'];row['predictions'][name]=pred
                if pred!=b['top1']:deltas[name].append(dict(id=id,source=source,code=code,target=target,before=b['top1'],after=pred,rescued=pred==target,regressed=b['top1']==target))
            sink.write(json.dumps(dict(id=id,prefer_score=prefer,pool=pool),ensure_ascii=False)+'\n');results.append(row)
    lib.ho_free(model)
    for dataset in ('old','fresh','combined'):
        rows=[r for r in results if dataset=='combined' or r['source']==dataset];assert len(rows)==(20000 if dataset=='combined' else 10000)
        data={'n':len(rows),'oracle_top5':sum(r['top5_oracle'] for r in rows),'oracle_top20':sum(r['top20_oracle'] for r in rows),'rows_with_candidate_oov':sum(r['any_oov'] for r in rows),'methods':{}}
        for name in modes:
            correct=sum(r['predictions'][name]==r['target'] for r in rows)
            rescued=sum(r['predictions'][name]==r['target'] and r['base']!=r['target'] for r in rows)
            regressed=sum(r['predictions'][name]!=r['target'] and r['base']==r['target'] for r in rows)
            data['methods'][name]=dict(correct=correct,accuracy=correct/len(rows),rescued=rescued,regressed=regressed,net=rescued-regressed)
        summary[dataset]=data
    with (out/'predictions.jsonl').open('w') as f:
        for r in results:f.write(json.dumps(r,ensure_ascii=False)+'\n')
    for name,rows in deltas.items():dump(out/(name+'-changes.json'),rows)
    dump(out/'summary.json',summary)
    dump(out/'manifest.json',dict(cases_sha256=ref['cases_sha256'],baseline_reproduced_all_20000=True,decoder_commit=ref['decoder_commit'],input_hashes=ref['input_hashes'],model3=ref,model5=manifest['validation'],exporter_sha256=digest(exporter),scorer_sha256=digest(libpath),methods=modes,formula='base_score + alpha * (lnP5(BOS,text,EOS) - lnP3(BOS,text,EOS)); preserve original rank comparator and all priors',latency_evaluated=False,llm=False,scope='same frozen trigram Top20 pools, not full fivegram beam search',training_overlap='New model excludes official valid/test files, but all wiki training records included; historical benchmark not independently held out from new model.'))
    print(json.dumps(summary,ensure_ascii=False,indent=2),flush=True)
if __name__=='__main__':main()
