"""Frozen-policy confirmation, including record-clustered paired intervals."""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path

p=argparse.ArgumentParser()
p.add_argument('directory',type=Path)
p.add_argument('--complete',action='store_true')
p.add_argument('--write',action='store_true')
a=p.parse_args()
cases=json.loads((a.directory/'cases.json').read_text())
sampling=json.loads((a.directory/'cases-sampling.json').read_text())
rows=[]
for path in sorted(a.directory.glob('rows-*.jsonl')):
    for line in path.open(encoding='utf-8'):
        if line.endswith('\n'): rows.append(json.loads(line))
assert len({r['id'] for r in rows})==len(rows),'Duplicate IDs'
for r in rows:
    c=cases[r['id']]
    assert all(r[k]==c[k] for k in ('text','code','length','source'))
    assert r['scoring_policy']=='candidate-convex-v1'
    assert r['provider'] in ('llama.cpp-cpu-q8','single-or-empty-no-ranking-change')
    assert len(r['qwen_scores'])==(min(5,len(r['pool'])) if len(r['pool'])>1 else 0)
    assert all(math.isfinite(x) for x in r['qwen_scores'])
    r['mixed']=len({len(c['text']) for c in r['pool'][:5]})>1
    r['record']=c['record']
if a.complete:
    assert len(cases)==50000 and {r['id'] for r in rows}==set(range(50000))
    assert len({c['text'] for c in cases})==50000
    previous_path=Path(sampling['excluded_cases'])
    assert hashlib.sha256(previous_path.read_bytes()).hexdigest()==sampling['excluded_cases_sha256']
    previous=json.loads(previous_path.read_text())
    assert not ({c['text'] for c in cases}&{c['text'] for c in previous})
    assert not ({(c['source'],c['record']) for c in cases}&{(c['source'],c['record']) for c in previous})
    assert all(c['record']>sampling['previous_record_cutoffs'][c['source']] for c in cases)
    assert all(sum(c['source']==s and c['length']==k for c in cases)==2500
               for s in ('webtext','news','baike','wiki') for k in range(2,7))
    manifests=[json.loads(f.read_text()) for f in a.directory.glob('manifest-*.json')]
    assert len(manifests)==2
    for key in ('ngram','qwen','host','core','cases'):
        assert len({m[key]['sha256'] for m in manifests})==1,key
    for key in ('schema','high_freq_limit','duplicate_single','whitelist','beam','reward',
                'whole_single_reward','lexicon_codes','lexicon_files','policy','workers'):
        assert manifests[0][key]==manifests[1][key],key
    assert {m['worker'] for m in manifests}=={0,1} and manifests[0]['workers']==2
    previous_manifest=json.loads(previous_path.with_name('manifest-0.json').read_text())
    for key in ('ngram','qwen','host'):
        assert previous_manifest[key]['sha256']==manifests[0][key]['sha256'],key
    for key in ('whitelist','lexicon_files','high_freq_limit','duplicate_single','beam','reward','whole_single_reward'):
        assert previous_manifest[key]==manifests[0][key],key
    assert hashlib.sha256((a.directory/'cases.json').read_bytes()).hexdigest().upper()==manifests[0]['cases']['sha256']
    assert hashlib.sha256((a.directory/'cases.json').read_bytes()).hexdigest()==sampling['cases_sha256']

fields={'off':'base_top','original':'legacy_top','shared':'shared_top','convex':'qwen_top'}
def paired(subset,against):
    changes=[int(r['qwen_top']==r['text'])-int(r[fields[against]]==r['text']) for r in subset]
    n=len(changes)
    delta=sum(changes)/n
    clusters=collections.defaultdict(lambda:[0,0])
    for r,d in zip(subset,changes):
        g=clusters[r['source'],r['record']]
        g[0]+=d; g[1]+=1
    g=len(clusters)
    se=math.sqrt(sum((total-delta*count)**2 for total,count in clusters.values())/n**2*g/(g-1)) if g>1 else 0
    return dict(against=against,fixes=changes.count(1),regressions=changes.count(-1),net=sum(changes),
                delta_pp=100*delta,record_clusters=g,cluster_95ci_pp=[100*(delta-1.96*se),100*(delta+1.96*se)])

summary=[]
categories=[('total',lambda r:True),('mixed',lambda r:r['mixed']),('same',lambda r:not r['mixed'])]
categories += [(str(k),lambda r,k=k:r['length']==k) for k in range(2,7)]
categories += [(s,lambda r,s=s:r['source']==s) for s in ('webtext','news','baike','wiki')]
for category,condition in categories:
    subset=[r for r in rows if condition(r)]
    if not subset: continue
    correct={name:sum(r[field]==r['text'] for r in subset) for name,field in fields.items()}
    summary.append(dict(category=category,n=len(subset),correct=correct,
                        accuracy={name:value/len(subset) for name,value in correct.items()},
                        paired=[paired(subset,name) for name in ('off','original','shared')]))
    if category in ('total','mixed','same','2','3','4','5','6'):
        print(category,len(subset),' '.join(f'{k}={v/len(subset):.4%}' for k,v in correct.items()),flush=True)
examples={}
for kind in ('fixes','regressions'):
    selected=[r for r in rows if (r['qwen_top']==r['text'])==(kind=='fixes')
              and (r['shared_top']==r['text'])!=(kind=='fixes')]
    examples[kind]=[{k:r[k] for k in ('id','text','code','base_top','legacy_top','shared_top','qwen_top','mixed')}
                    for r in selected[:20]]
report=dict(complete=a.complete,cases=len(rows),providers=dict(collections.Counter(r['provider'] for r in rows)),
            note='Frozen policies. Record-clustered normal intervals; subgroup intervals unadjusted for multiplicity.',
            summary=summary,examples=examples)
if a.write:
    (a.directory/'confirmation-summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    lines=['分组,样本数,关闭Qwen,最初参数,整组权重,候选独立配比,相对整组纠正,相对整组改错']
    for s in summary:
        lines.append(','.join([s['category'],str(s['n'])]+[f"{s['accuracy'][name]:.4%}" for name in fields]+
                              [str(s['paired'][2]['fixes']),str(s['paired'][2]['regressions'])]))
    (a.directory/'confirmation-summary.csv').write_text('\n'.join(lines)+'\n',encoding='utf-8-sig')
