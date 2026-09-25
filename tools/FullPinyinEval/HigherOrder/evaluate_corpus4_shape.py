"""Reproduce frozen 20k shape accuracy across corpus4 and historical models.
No latency measurement, learning, Qwen, installation or parameter tuning.
Run from repository root; --output retains pools, predictions and paired changes.
"""
import argparse, concurrent.futures, csv, hashlib, json, os, subprocess
from pathlib import Path


def digest(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--new-model', type=Path, required=True)
    p.add_argument('--workers', type=int, default=8)
    args = p.parse_args()
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    here = Path(__file__).resolve().parent
    scratch = Path('/home/yc/tmp')
    direct = scratch / 'tiger-shape-direct5'
    lines = (scratch / 'tigirl-tcs3-cases.tsv').read_text().splitlines()
    lines.sort(key=lambda s: (s.startswith('fresh'), int(s.split('\t')[0].split('_')[-1])))
    cases = out / 'cases.tsv'
    cases.write_text('\n'.join(lines) + '\n')
    assert digest(cases) == 'd46d90a24bacf8c43d5c9713eb519062a23441b31e1c5fed45beb031e192372a'
    rows = [dict(zip(('id', 'source', 'code', 'target'), s.split('\t'))) for s in lines]
    assert len(rows) == 20000 and len({r['id'] for r in rows}) == 20000
    methods = {
        'm5_trigram': (scratch / 'tiger-char5-shape-20k/fixture', None, (9941, 9952)),
        'fused_trigram': (scratch / 'tiger-fused3-shape-20k/fixture', None, (9894, 9958)),
        'old_full_fivegram': (direct / 'fixture', direct / 'char5-q8.klm', (9963, 9967)),
        'old_420mb_fivegram': (direct / 'fixture', scratch / 'tiger-char5-500/char5-context128-q8.klm', (9958, 9968)),
        'corpus4_fivegram': (direct / 'fixture', args.new_model, None),
    }
    manifest = {'cases_sha256': digest(cases), 'methods': {}, 'workers': args.workers,
                'latency_evaluated': False, 'llm': False, 'learning': False,
                'production_changed': False, 'native_sha256': digest(direct / 'shape5.so'),
                'exporter_sha256': digest(here / 'export_shape_pool.lua')}
    for row in rows:
        row['predictions'], row['target_ranks'] = {}, {}
    for name, (fixture, model, expected) in methods.items():
        folder = out / name
        folder.mkdir(exist_ok=True)
        env = dict(os.environ)
        env.pop('SHAPE5_MODEL', None)
        env.pop('SHAPE5_LIB', None)
        if model:
            env.update(SHAPE5_MODEL=str(model), SHAPE5_LIB=str(direct / 'shape5.so'))
        model_path = model or fixture / 'models/sentence-ngram-mobile.bin'
        info = {'model_path': str(model_path), 'bytes': model_path.stat().st_size,
                'model_sha256': digest(model_path), 'decoder_sha256': digest(fixture / 'lua/tiger_sentence.lua')}
        print(name, 'model verified', info, flush=True)
        if name == 'corpus4_fivegram':
            assert info['model_sha256'] == '6114583494db90093d7738fe0f268a104dd82d08b0f7b4b80ef95c153787effc'
            check = subprocess.run(['lua', str(here / 'test_shape5.lua'), str(fixture)], env=env,
                                   capture_output=True, text=True, check=True)
            (folder / 'validation.log').write_text(check.stdout + check.stderr)
            print(check.stdout, flush=True)
        def run(i):
            part = folder / f'cases-{i}.tsv'
            part.write_text('\n'.join(lines[i::args.workers]) + '\n')
            with (folder / f'worker-{i}.log').open('w') as log:
                subprocess.run(['lua', str(here / 'export_shape_pool.lua'), str(fixture),
                                str(part), str(folder / f'pool-{i}.tsv')], env=env,
                               stdout=log, stderr=subprocess.STDOUT, check=True)
            print(name, 'worker complete', i, flush=True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
            list(executor.map(run, range(args.workers)))
        groups = {}
        for i in range(args.workers):
            with (folder / f'pool-{i}.tsv').open() as f:
                for candidate in csv.DictReader(f, delimiter='\t'):
                    groups.setdefault(candidate['id'], []).append(candidate)
        assert len(groups) == 20000
        for row in rows:
            pool = groups[row['id']]
            assert all(pool[0][k] == row[k] for k in ('source', 'code', 'target'))
            row['predictions'][name] = pool[0]['text']
            row['target_ranks'][name] = next((int(c['index']) for c in pool if c['text'] == row['target']), 0)
        counts = tuple(sum(r['source'] == s and r['predictions'][name] == r['target'] for r in rows)
                       for s in ('old', 'fresh'))
        if expected:
            assert counts == expected, (name, counts, expected)
        info['correct_old_fresh'] = counts
        manifest['methods'][name] = info
        print(name, 'RESULT', counts, flush=True)
        (out / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    summary = {}
    for source in ('old', 'fresh', 'combined'):
        subset = [r for r in rows if source == 'combined' or r['source'] == source]
        stats = {'n': len(subset), 'methods': {}, 'comparisons': {}}
        for name in methods:
            stats['methods'][name] = {
                'correct': sum(r['predictions'][name] == r['target'] for r in subset),
                'top5': sum(0 < r['target_ranks'][name] <= 5 for r in subset),
                'top20': sum(0 < r['target_ranks'][name] <= 20 for r in subset)}
        for baseline in list(methods)[:-1]:
            rescued = sum(r['predictions']['corpus4_fivegram'] == r['target'] and r['predictions'][baseline] != r['target'] for r in subset)
            regressed = sum(r['predictions']['corpus4_fivegram'] != r['target'] and r['predictions'][baseline] == r['target'] for r in subset)
            stats['comparisons'][baseline] = dict(rescued=rescued, regressed=regressed, net=rescued-regressed)
        summary[source] = stats
    with (out / 'predictions.jsonl').open('w') as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + '\n')
    for baseline in list(methods)[:-1]:
        with (out / f'changes-vs-{baseline}.tsv').open('w') as f:
            writer = csv.writer(f, delimiter='\t')
            writer.writerow(('id', 'source', 'code', 'target', 'before', 'after', 'change'))
            for r in rows:
                before, after = r['predictions'][baseline], r['predictions']['corpus4_fivegram']
                if before != after:
                    change = 'improved' if after == r['target'] else 'regressed' if before == r['target'] else 'both_differ'
                    writer.writerow((r['id'], r['source'], r['code'], r['target'], before, after, change))
    (out / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
