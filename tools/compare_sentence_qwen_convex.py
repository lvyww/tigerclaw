"""Compare shared weights against candidate-length convex blends using cached scores.

No runtime changes. Grouped inner CV chooses the family before reporting the
previously viewed holdout (exploratory validation, not a fresh test set).
"""
import argparse
import hashlib
import json
from pathlib import Path
import numpy as np

p = argparse.ArgumentParser()
p.add_argument('directory', type=Path)
a = p.parse_args()
cases = json.loads((a.directory/'cases.json').read_text())
rows = sorted((json.loads(line) for f in a.directory.glob('rows-*.jsonl')
               for line in f.open(encoding='utf-8')), key=lambda r:r['id'])
assert len(rows) == 50000 and [r['id'] for r in rows] == list(range(50000))
n = len(rows)
base = np.zeros((n, 5))
qwen = np.zeros((n, 5))
lengths = np.zeros((n, 5), dtype=int)
valid = np.zeros((n, 5), dtype=bool)
target = np.full(n, -1)
old = np.zeros(n, dtype=int)
old_weights = np.array([r['weight'] for r in rows])
for i,r in enumerate(rows):
    assert 'scoring_policy' not in r, 'This experiment requires the original additive-score baseline'
    assert all(r[k] == cases[i][k] for k in ('text','code','length','source'))
    for j,c in enumerate(r['pool'][:5]):
        base[i,j], lengths[i,j], valid[i,j] = c['score'], len(c['text']), True
        if c['text'] == r['text']: target[i] = j
        if c['text'] == r['qwen_top']: old[i] = j
    qwen[i,:len(r['qwen_scores'])] = r['qwen_scores']
assert np.all(np.argmax(np.where(valid,base+old_weights[:,None]*qwen,-np.inf),axis=1)==old)
def hash_groups(prefix, modulo):
    return np.array([int.from_bytes(hashlib.sha256(
        f"{prefix}:{c['source']}:{c['record']}".encode()).digest()[:8],'big') % modulo for c in cases])
train = hash_groups('qwen-calibration-v1',10) < 7
fold = hash_groups('qwen-inner-v1',5)
supported = (lengths[:,0]>=2)&(lengths[:,0]<=6)
mixed = np.any(valid & (lengths != lengths[:,:1]),axis=1)
alpha_grid = np.round(np.arange(0,1.001,.05),2)
shared_grid = np.round(np.arange(0,1.201,.05),2)
bucket = np.clip(lengths[:,0],2,6)-2
candidate_bucket = np.clip(lengths,2,6)-2
candidate_supported = (lengths>=2)&(lengths<=6)
fallback = np.where(lengths<=1,.30,.84)
fallback = fallback/(1+fallback)

def standardize(values):
    count = np.maximum(valid.sum(axis=1,keepdims=True),1)
    mean = np.where(valid,values,0).sum(axis=1,keepdims=True)/count
    centered = values-mean
    std = np.sqrt(np.where(valid,centered**2,0).sum(axis=1,keepdims=True)/count)
    return np.divide(centered,std,out=np.zeros_like(values),where=std>1e-9)

spaces = {'raw_convex':(base,qwen), 'standardized_convex':(standardize(base),standardize(qwen))}
def predict(family, parameters):
    if family == 'shared':
        score = base + np.asarray(parameters)[bucket,None]*qwen
    else:
        ng,neural = spaces[family]
        alpha = np.where(candidate_supported,np.asarray(parameters)[candidate_bucket],fallback)
        score = (1-alpha)*ng+alpha*neural
    result = np.argmax(np.where(valid,score,-np.inf),axis=1)
    return np.where(supported,result,old)

def fit(family, mask):
    if family == 'shared':
        predictions = np.array([np.argmax(np.where(valid,base+w*qwen,-np.inf),axis=1) for w in shared_grid])
        return [float(shared_grid[np.argmax(np.sum(predictions[:,mask&supported&(bucket==b)] ==
                        target[mask&supported&(bucket==b)],axis=1))]) for b in range(5)]
    # Deterministic multi-start coordinate grid search. No claim of a global optimum.
    # Cache each length group's best score and index under each alpha.
    ng,neural = (x[mask&supported] for x in spaces[family])
    v,ls,t = valid[mask&supported], lengths[mask&supported], target[mask&supported]
    fb = fallback[mask&supported]
    scores = (1-fb)*ng+fb*neural
    outside = v & ((ls<2)|(ls>6))
    outside_score = np.max(np.where(outside,scores,-np.inf),axis=1)
    outside_idx = np.argmax(np.where(outside,scores,-np.inf),axis=1)
    cached_s,cached_i = [],[]
    for b in range(2,7):
        s = np.where((v&(ls==b))[None,:,:],
            (1-alpha_grid[:,None,None])*ng+alpha_grid[:,None,None]*neural,-np.inf)
        cached_s.append(np.max(s,axis=2))
        cached_i.append(np.argmax(s,axis=2))
    def hits(indices):
        s = np.array([outside_score]+[cached_s[b][indices[b]] for b in range(5)])
        ix = np.array([outside_idx]+[cached_i[b][indices[b]] for b in range(5)])
        # Stable original-pool tie break; production equality is checked separately.
        win = np.min(np.where(s==s.max(axis=0),ix,99),axis=0)
        return int(np.sum(win==t))
    best = None
    for start in (0,3,6,10,16,20):
        indices = [start]*5
        for iteration in range(6):
            previous = list(indices)
            for b in range(5):
                choices = []
                for k in range(len(alpha_grid)):
                    trial = list(indices)
                    trial[b] = k
                    choices.append(hits(trial))
                indices[b] = int(np.argmax(choices))
            if indices == previous: break
        candidate = (hits(indices),-sum(indices),tuple(-k for k in indices))
        if best is None or candidate>best[0]: best=(candidate,list(indices))
    return alpha_grid[best[1]].tolist()

report = {'grid':alpha_grid.tolist(),'search':'six-start coordinate search, six passes maximum; not exhaustive',
          'holdout_warning':'Previously viewed holdout; exploratory, new unseen records needed for confirmation',
          'families':{}}
for family in ('shared','raw_convex','standardized_convex'):
    cv_prediction = old.copy()
    fold_parameters=[]
    for f in range(5):
        parameters=fit(family,train&(fold!=f))
        prediction=predict(family,parameters)
        cv_prediction[train&(fold==f)]=prediction[train&(fold==f)]
        fold_parameters.append(parameters)
        print(family,'fold',f,'parameters',parameters,flush=True)
    parameters=fit(family,train)
    report['families'][family]={'parameters':parameters,'fold_parameters':fold_parameters,
        'cv_correct':int(np.sum((cv_prediction==target)&train))}
selected=max(report['families'],key=lambda k:report['families'][k]['cv_correct'])
report['selected_family']=selected
current=predict('shared',[.10,.35,.45,.25,.55])
for family,result in report['families'].items():
    prediction=predict(family,result['parameters'])
    result['metrics']={}
    for part,mask in [('train',train),('validation',~train),('all',np.ones(n,dtype=bool))]:
        result['metrics'][part]=[]
        for label,m in [('total',mask),('mixed',mask&mixed),('same',mask&~mixed)]+[
                (str(k),mask&np.array([r['length']==k for r in rows])) for k in range(2,7)]:
            good=prediction==target
            before=current==target
            fixes=int(np.sum(m&good&~before)); regressions=int(np.sum(m&~good&before))
            count=int(m.sum()); delta=(fixes-regressions)/count
            se=np.sqrt(((fixes+regressions)/count-delta**2)/count)
            result['metrics'][part].append(dict(category=label,n=count,correct=int(np.sum(m&good)),
                current_correct=int(np.sum(m&before)),fixes=fixes,regressions=regressions,
                delta_95ci_pp=[100*(delta-1.96*se),100*(delta+1.96*se)]))
    print('RESULT',family,'CV',result['cv_correct'],'parameters',result['parameters'],
          'validation',result['metrics']['validation'][:3],flush=True)
print('SELECTED',selected,flush=True)
(a.directory/'convex-predictions.json').write_text(json.dumps(
    predict(selected,report['families'][selected]['parameters']).tolist()),encoding='utf-8')
(a.directory/'convex-comparison.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
