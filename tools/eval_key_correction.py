"""Deterministic 20/80 calibration and once-only holdout; max twelve workers."""
import argparse, concurrent.futures, csv, hashlib, json, random, shutil, subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETS = {
    'old10k': Path('/mnt/c/Archive/mohu-v5-old10k-20260924/cases.tsv'),
    'articles': Path('/mnt/c/Archive/char5-corpus4_0-20260922/articles-comparison-20260923/cases.tsv'),
    'thucnews': Path('/mnt/c/Archive/char5-corpus4_0-20260922/thucnews-comparison-20260923/cases.tsv'),
}
NEIGHBORS = {}
for row in ('qwertyuiop', 'asdfghjkl', 'zxcvbnm'):
    for i, ch in enumerate(row):
        NEIGHBORS[ch] = list(row[max(0, i - 1):i] + row[i + 1:i + 2])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('work', type=Path)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--phase', choices=['calibration', 'holdout'], required=True)
    p.add_argument('--penalty', type=int)
    p.add_argument('--lua', choices=['lua', 'luajit'], default='luajit')
    p.add_argument('--workers', type=int, choices=range(1, 13), default=12)
    args = p.parse_args()
    weights = [4, 6, 8, 10, 12, 16] if args.phase == 'calibration' else [args.penalty]
    assert all(x is not None for x in weights)
    out = args.work / args.phase
    out.mkdir(exist_ok=False)
    snapshot = args.work / 'source'
    if args.phase == 'calibration':
        assert not snapshot.exists(), 'Use a fresh work directory for a new implementation'
        shutil.copytree(ROOT / 'rime/tiger_sentence', snapshot)
    else:
        assert snapshot.is_dir(), 'Holdout must use the frozen calibration implementation'
        calibration = json.loads((args.work / 'calibration/summary.json').read_text())
        assert args.penalty == calibration['selected_penalty'], 'Use the frozen development-selected penalty'
        frozen = json.loads((args.work / 'calibration/manifest.json').read_text())['decoder_files']
        actual = {str(f.relative_to(snapshot)): hashlib.sha256(f.read_bytes()).hexdigest()
                  for f in sorted(snapshot.rglob('*.lua'))}
        assert actual == frozen, 'Calibration decoder changed before holdout'
    partitions = [[] for _ in range(args.workers)]
    manifest = {'phase': args.phase, 'penalties': weights, 'lua': args.lua, 'seed': 'tiger-key-correction-v1',
                'sets': {}, 'training_independence_claimed': False}
    manifest['decoder_files'] = {str(f.relative_to(snapshot)): hashlib.sha256(f.read_bytes()).hexdigest()
                                 for f in sorted(snapshot.rglob('*.lua'))}
    for dataset, source in SETS.items():
        lines = source.read_text().splitlines()
        manifest['sets'][dataset] = {'sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'total': len(lines)}
        for line in lines:
            ident, group, raw, target = line.split('\t')
            digest = hashlib.sha256((dataset + ':' + ident).encode()).digest()
            calibration = int.from_bytes(digest[:8], 'big') % 5 == 0
            if calibration != (args.phase == 'calibration'):
                continue
            one = two = '-'
            if len(raw) >= 4 and all(ch in NEIGHBORS for ch in raw) and len(target) >= 2:
                rng = random.Random(int.from_bytes(hashlib.sha256(b'tiger-key-correction-v1' + digest).digest(), 'big'))
                positions = rng.sample(range(len(raw)), 2)
                values = list(raw)
                for n, pos in enumerate(positions):
                    values[pos] = rng.choice(NEIGHBORS[raw[pos]])
                    if n == 0: one = ''.join(values)
                    else: two = ''.join(values)
            row = '\t'.join([ident, dataset, raw, target, one, two])
            partitions[int.from_bytes(digest[8:16], 'big') % args.workers].append(row)
    manifest['rows'] = sum(map(len, partitions))
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2))

    def run(index):
        cases, output = out / f'cases-{index}.tsv', out / f'results-{index}.tsv'
        cases.write_text('\n'.join(partitions[index]) + '\n')
        with (out / f'worker-{index}.log').open('w') as log:
            subprocess.run([args.lua, str(ROOT / 'tools/eval_key_correction.lua'),
                            str(snapshot), str(args.work / 'data'),
                            str(args.baseline), str(cases), str(output), ','.join(map(str, weights))],
                           stdout=log, stderr=subprocess.STDOUT, check=True)
        return output

    totals = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
        outputs = list(executor.map(run, range(args.workers)))
    for output in outputs:
        for row in csv.DictReader(output.open(), delimiter='\t'):
            key = f"{row['dataset']}/{row['variant']}/{row['penalty']}"
            v = totals.setdefault(key, dict(n=0, baseline=0, top1=0, top5=0, changed=0, miscorrected=0))
            v['n'] += 1
            for metric, column in [('baseline', 'base_correct'), ('top1', 'top1'), ('top5', 'top5'), ('changed', 'changed')]:
                v[metric] += int(row[column])
            v['miscorrected'] += row['base_correct'] == '1' and row['top1'] == '0'
    report = {'totals': totals}
    if args.phase == 'calibration':
        eligible = []
        for weight in weights:
            clean = [totals[f'{name}/0/{weight}'] for name in SETS]
            errors, correct = sum(v['miscorrected'] for v in clean), sum(v['baseline'] for v in clean)
            noise = []
            for variant in [1, 2]:
                values = [totals[f'{name}/{variant}/{weight}'] for name in SETS]
                noise.append(sum(v['top1'] for v in values) / sum(v['n'] for v in values))
            if errors / max(1, correct) <= .0005:
                eligible.append((sum(noise) / 2, weight))
        report['selected_penalty'] = max(eligible)[1] if eligible else None
    (out / 'summary.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
