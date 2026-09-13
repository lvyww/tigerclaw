"""Summarize paired Core/Qwen length evaluation, including progress snapshots."""
import argparse
import json
import math
import hashlib
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('directory', type=Path)
parser.add_argument('--complete', action='store_true')
parser.add_argument('--write', action='store_true')
args = parser.parse_args()
rows = []
for path in sorted(args.directory.glob('rows-*.jsonl')):
    with path.open(encoding='utf-8') as stream:
        for line in stream:
            if not line.endswith('\n'):
                continue
            rows.append(json.loads(line))
ids = [r['id'] for r in rows]
if len(ids) != len(set(ids)):
    raise ValueError('duplicate sample IDs')
if args.complete:
    cases_path = args.directory/'cases.json'
    cases = json.loads(cases_path.read_text(encoding='utf-8'))
    if len(cases) != 50000 or set(ids) != set(range(50000)):
        raise ValueError('Incomplete or unexpected sample ID coverage')
    if len({case['text'] for case in cases}) != 50000:
        raise ValueError('Repeated target text')
    for row in rows:
        if any(row[key] != cases[row['id']][key] for key in ('text','code','length','source')):
            raise ValueError('Result/case identity mismatch')
    manifests = [json.loads(p.read_text(encoding='utf-8')) for p in args.directory.glob('manifest-*.json')]
    for key in ('ngram','qwen','host','core','cases'):
        if len({m[key]['sha256'] for m in manifests}) != 1:
            raise ValueError('Mixed worker model/build fingerprints: '+key)
    if hashlib.sha256(cases_path.read_bytes()).hexdigest().upper() != manifests[0]['cases']['sha256']:
        raise ValueError('Cases changed since evaluation started')
    for length in range(2,7):
        for source in ('webtext','news','baike','wiki'):
            if sum(r['length']==length and r['source']==source for r in rows) != 2500:
                raise ValueError('Unexpected source balance')
summary = []
examples = {}
for length in range(2, 7):
    subset = [r for r in rows if r['length'] == length]
    n = len(subset)
    if args.complete and n != 10000:
        raise ValueError(f'{length} characters: expected 10000, got {n}')
    if not n:
        continue
    base = sum(r['base_rank'] == 1 for r in subset)
    qwen = sum(r['qwen_rank'] == 1 for r in subset)
    fixes = [r for r in subset if r['base_rank'] != 1 and r['qwen_rank'] == 1]
    regressions = [r for r in subset if r['base_rank'] == 1 and r['qwen_rank'] != 1]
    delta = (qwen - base) / n
    se = math.sqrt(max(0, (len(fixes) + len(regressions)) / n - delta * delta) / n)
    summary.append(dict(length=length, cases=n, base_correct=base, qwen_correct=qwen,
        base_accuracy=base/n, qwen_accuracy=qwen/n, delta_pp=100*delta,
        fixes=len(fixes), regressions=len(regressions),
        delta_95ci_pp=[100*(delta - 1.96*se), 100*(delta + 1.96*se)],
        base_mrr=sum(1/r['base_rank'] if r['base_rank'] else 0 for r in subset)/n,
        qwen_mrr=sum(1/r['qwen_rank'] if r['qwen_rank'] else 0 for r in subset)/n,
        oracle_top5=sum(0 < r['base_rank'] <= 5 for r in subset)/n,
        oracle_top20=sum(r['base_rank'] > 0 for r in subset)/n,
        singleton_or_empty=sum(len(r['pool']) <= 1 for r in subset),
        weight_030=sum(r['weight'] == .30 for r in subset),
        weight_084=sum(r['weight'] == .84 for r in subset),
        explicit_selection=sum(any(c in ";'0123456789" for c in r['code']) for r in subset)))
    examples[length] = {kind: [{k:r[k] for k in ('text','code','base_top','qwen_top','weight')} for r in values[:20]]
                        for kind,values in [('fixes',fixes),('regressions',regressions)]}
report = dict(complete=len(rows)==50000, cases=len(rows), summary=summary, examples=examples)
print('characters cases ngram_top1 qwen_top1 delta_pp fixes regressions')
for r in summary:
    print(f"{r['length']} {r['cases']} {r['base_accuracy']:.4%} {r['qwen_accuracy']:.4%} {r['delta_pp']:+.4f} {r['fixes']} {r['regressions']}")
print('total', len(rows))
if args.write:
    (args.directory/'summary.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    lines=['字数,样本数,n-gram正确数,Qwen正确数,n-gram准确率,Qwen准确率,变化百分点,纠正数,退化数']
    for r in summary:
        lines.append(f"{r['length']},{r['cases']},{r['base_correct']},{r['qwen_correct']},{r['base_accuracy']:.4%},{r['qwen_accuracy']:.4%},{r['delta_pp']:+.4f},{r['fixes']},{r['regressions']}")
    (args.directory/'summary.csv').write_text('\n'.join(lines)+'\n', encoding='utf-8-sig')
