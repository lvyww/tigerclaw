"""Compare isolated renderer probes; no production IPC or deployment."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--baseline', type=Path, required=True)
p.add_argument('--optimized', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=False)
report = {'binaries': {name: hashlib.sha256(path.read_bytes()).hexdigest()
                      for name, path in [('baseline', a.baseline), ('optimized', a.optimized)]}, 'runs': []}
def run(path, *args):
    return json.loads(subprocess.run([str(path.resolve()), *args], cwd=path.parent,
                     capture_output=True, text=True, encoding='utf-8', check=True, timeout=120).stdout)

before = run(a.baseline, '--verify')
after = run(a.optimized, '--verify')
(a.output / 'baseline-pixels.json').write_text(json.dumps(before, indent=2))
(a.output / 'optimized-pixels.json').write_text(json.dumps(after, indent=2))
if before['frames'] != after['frames']:
    bad = [x['id'] for x, y in zip(before['frames'], after['frames']) if x != y]
    raise RuntimeError('Bitmap size/pixel digest mismatch, cases: ' + str(bad[:20]))
report['pixel_cases'] = len(before['frames'])
print('Pixel digests and dimensions match:', report['pixel_cases'], flush=True)
for round_number, order in enumerate([['baseline', 'optimized'], ['optimized', 'baseline']]):
    for variant in order:
        result = run(getattr(a, variant))
        entry = {'variant': variant, 'round': round_number, 'result': result}
        report['runs'].append(entry)
        (a.output / 'raw.json').write_text(json.dumps(report))
        for workload, data in result.items():
            values = sorted(data['ms'])
            print(variant, round_number, workload, 'p50', round(statistics.median(values), 4),
                  'p95', round(values[int((len(values)-1)*.95)], 4), data['creations'], flush=True)
summary = {}
for variant in ['baseline', 'optimized']:
    summary[variant] = {}
    for workload in ['selection', 'typing', 'resize', 'theme']:
        values = sorted(v for r in report['runs'] if r['variant'] == variant for v in r['result'][workload]['ms'])
        summary[variant][workload] = dict(count=len(values), mean=statistics.mean(values), p50=statistics.median(values),
                                         p95=values[int((len(values)-1)*.95)], p99=values[int((len(values)-1)*.99)])
(a.output / 'summary.json').write_text(json.dumps(summary, indent=2))
print('Saved', a.output)
