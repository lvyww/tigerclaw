#!/usr/bin/env python3
"""Freeze historical inputs by seeded hash, independent of predictions."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

def identity(path):
    p = Path(path)
    with p.open('rb') as f:
        digest = hashlib.file_digest(f, 'sha256').hexdigest()
    return dict(path=str(p), bytes=p.stat().st_size, sha256=digest)

def select_performance(work):
    rows = [json.loads(line) for line in (work/'freeze-A.jsonl').read_text().splitlines()]
    selected, counts, rules = [], {}, {}
    for name, n, low, high in [('short',20,0,6),('medium',40,6,14),('long',40,14,999)]:
        eligible = [r for r in rows if len(r['pool']) > 1 and not r['item']['diagnostic']
                    and low < sum(len(c['text']) for c in r['pool'][:5])/min(5,len(r['pool'])) <= high]
        eligible.sort(key=lambda r: hashlib.sha256(('20261004:perf:'+r['item']['id']).encode()).hexdigest())
        assert len(eligible) >= n
        selected.extend(dict(r['item'], perf_group=name) for r in eligible[:n])
        counts[name], rules[name] = len(eligible), [low, high]
    (work/'performance-cases.json').write_text(json.dumps(selected, ensure_ascii=False, indent=2))
    (work/'performance-selection.json').write_text(json.dumps(dict(seed=20261004, hash_format='20261004:perf:ID',
        length='mean Unicode characters of first min(5,N) candidate texts', bounds_exclusive_inclusive=rules,
        eligible=counts, selected={'short':20,'medium':40,'long':40}, order=['B1','C1','C2','B2']), indent=2))

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--repo', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--source-plan', type=Path, required=True)
    p.add_argument('--select-performance', action='store_true')
    a = p.parse_args()
    if a.select_performance:
        select_performance(a.output)
        return
    if a.output.exists():
        raise ValueError('Use a fresh output directory')
    a.output.mkdir(parents=True)
    sources = json.loads(a.source_plan.read_text())['cases']
    selected, seen, counts, identities = [], set(), {}, {}
    # Source order is fixed before sampling. Cross-source duplicates belong to
    # the first selected source. Hash ties are broken by original case ID.
    for source in ('old10k', 'articles', 'thucnews'):
        path = Path(sources[source]['path'])
        identities[source] = identity(path)
        assert identities[source]['sha256'] == sources[source]['sha256']
        rows = []
        for line in path.read_text().splitlines():
            cid, group, code, text = line.split('\t')
            key = hashlib.sha256(f'20261004\t{source}\t{cid}\t{code}\t{text}'.encode()).hexdigest()
            rows.append(dict(id=f'{source}:{cid}', source=source, group=group, code=code, text=text, hash=key, diagnostic=False))
        counts[source] = 0
        for row in sorted(rows, key=lambda r: (r['hash'], r['id'])):
            if row['text'] in seen:
                continue
            selected.append(row)
            seen.add(row['text'])
            counts[source] += 1
            if counts[source] == 1000:
                break
        assert counts[source] == 1000
    selected.sort(key=lambda r: (r['hash'], r['id']))
    selected.extend([dict(id='diagnostic:'+code, source='diagnostic', code=code, text='', diagnostic=True)
                     for code in ('tlleo', 'zhhbi')])
    (a.output/'cases.json').write_text(json.dumps(selected, ensure_ascii=False, indent=2))
    runtime = a.output/'runtime'
    schema = runtime/'码表'/'虎整句'
    schema.mkdir(parents=True)
    daily = a.repo/'release_arm64'
    originals = [daily/'config.txt', daily/'Models'/'sentence-fivegram-mobile.bin',
                 *sorted(p for p in (daily/'码表'/'虎整句').glob('*.txt')
                         if not p.name.startswith('自学习-'))]
    copied = []
    for src in originals:
        dst = runtime/src.relative_to(daily)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dst)
        before, after = identity(src), identity(dst)
        assert before['sha256'] == after['sha256']
        copied.append(dict(source=before, frozen=after))
    # Learning/early commit disabled in the in-memory state; journal never copied.
    manifest = dict(seed=20261004, hash_format='seed TAB source TAB id TAB code TAB text',
                    source_order=list(counts), counts=counts, sources=identities, runtime=copied,
                    cases=identity(a.output/'cases.json'), historical_not_independent=True)
    (a.output/'sampling.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
    print(json.dumps(counts))

if __name__ == '__main__':
    main()
