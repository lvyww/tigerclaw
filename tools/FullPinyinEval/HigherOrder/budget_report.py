"""Finalize metrics without selecting parameters on test outcomes."""
import json
from pathlib import Path
from budget500 import B, O, R, dump, rows

def compact(path):
    result = {}
    for r in rows(path):
        assert r['id'] not in result
        result[r['id']] = {k: r[k] for k in ('id', 'text', 'code', 'consumed', 'rank')}
        result[r['id']]['top'] = r['candidates'][0]['text'] if r['candidates'] else ''
    assert len(result) == 8019
    return result

def distance(a, b):
    d = list(range(len(b)+1))
    for i, x in enumerate(a, 1):
        n = [i]
        for j, y in enumerate(b, 1):
            n.append(min(n[-1]+1, d[j]+1, d[j-1]+(x != y)))
        d = n
    return d[-1]

def ok(r):
    return r['rank'] == 1 and r['consumed'] == len(r['code'])

baseline = compact(B/'joint.jsonl')
pair = {r['id']: r for r in rows(R/'experiments/joint-qwen-wetype-1050-20260921/cases.jsonl')}
assert len(pair) == 1050
selection = json.loads((O/'dev-selection.json').read_text())
models = {'trigram': baseline, 'q8': compact(O/'test3q8.jsonl'),
          'fusion': compact(O/'test-fusion.jsonl'), 'single': compact(O/'test-single.jsonl')}
report = {'selection': selection, 'models': {},
          'limitations': ['Training/test disjointness not independently proven.',
                         'Exact original-text match is not semantic acceptability.',
                         'Runtime LM file budget only; not process RSS, full app size or archive size.',
                         'Linux ARM64 offline prototypes; no Windows deployment or application acceptance.']}
for name, data in models.items():
    assert set(data) == set(baseline)
    for key, r in data.items():
        assert (r['text'], r['code']) == (baseline[key]['text'], baseline[key]['code'])
        if key in pair:
            assert (r['text'], r['code']) == (pair[key]['text'], pair[key]['code'])
    changes = []
    for key, r in data.items():
        old = baseline[key]
        if old['top'] != r['top']:
            changes.append({'id': key, 'text': r['text'], 'old': old['top'], 'new': r['top'],
                            'rescued': not ok(old) and ok(r), 'regressed': ok(old) and not ok(r)})
    stats = {'n': len(data), 'correct': sum(ok(r) for r in data.values()),
             'paired1050Correct': sum(ok(data[k]) for k in pair),
             'cerPercent': 100*sum(distance(r['text'], r['top']) for r in data.values())/sum(len(r['text']) for r in data.values()),
             'rescued': sum(c['rescued'] for c in changes), 'regressed': sum(c['regressed'] for c in changes),
             'changed': len(changes),
             'recall': {str(n): sum(0 < r['rank'] <= n and r['consumed'] == len(r['code']) for r in data.values()) for n in (5, 10, 50)}}
    stats['accuracyPercent'] = 100*stats['correct']/len(data)
    report['models'][name] = stats
    dump(O/(name+'-vs-trigram-changes.json'), changes)
    print(name, stats, flush=True)
dump(O/'report.json', report)
