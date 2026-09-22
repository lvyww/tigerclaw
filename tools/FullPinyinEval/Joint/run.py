"""Frozen dictionary/model comparison. All processes and files are offline."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import shutil
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import analyze as metrics
from lexicon_ablation import write_rows


def main():
    p = argparse.ArgumentParser()
    p.add_argument('root', type=Path)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--table', type=Path, required=True)
    p.add_argument('--m5', type=Path, required=True)
    p.add_argument('--models', type=Path, required=True)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--jobs', type=int, default=12)
    p.add_argument('--beam', type=int, default=200)
    p.add_argument('--kinds', nargs='+', choices=['m5','char','joint'], default=['m5','char','joint'])
    a = p.parse_args()
    if not 1 <= a.jobs <= 32:
        p.error('--jobs must be 1..32')
    if a.beam < 1:
        p.error('--beam must be positive')
    root = a.root.resolve()
    parts = root / 'parts'; parts.mkdir(exist_ok=True)
    data = root / 'cases.jsonl'
    if data.exists() and metrics.digest(data) != metrics.digest(a.baseline/'data/cases.jsonl'):
        raise ValueError('frozen input differs')
    shutil.copy2(a.baseline / 'data/cases.jsonl', data)
    cases = metrics.indexed(metrics.read(data))
    models = {'m5': a.m5, 'char': a.models / 'char_3gram.klm', 'joint': a.models / 'joint_3gram.klm'}
    models = {k: models[k] for k in a.kinds}
    common = dict(table=metrics.digest(a.table), cases=metrics.digest(data),
                  executable=metrics.digest(root / 'bin/Joint.dll'),
                  native=metrics.digest(root / 'bin/libjointkenlm.so'),
                  tokens=metrics.digest(root / 'tokens.json'),
                  models={k: metrics.digest(v) for k, v in models.items()},
                  jobs=a.jobs, beam=a.beam, reward=2, qwen=False)
    identity = root / 'run-manifest.json'
    if identity.exists() and json.loads(identity.read_text()) != common:
        raise ValueError('existing experiment fingerprint differs')
    identity.write_text(json.dumps(common, indent=2))
    command = [str(a.dotnet), str(root / 'bin/Joint.dll')]
    environment = dict(os.environ)
    environment['LD_LIBRARY_PATH'] = str(root/'bin') + ':' + environment.get('LD_LIBRARY_PATH','')
    def run(label, args):
        print('START', label, flush=True)
        with (root / (label + '.log')).open('w') as log:
            subprocess.run(command + list(map(str,args)) + ['--beam',str(a.beam)], stdout=log, stderr=subprocess.STDOUT, check=True, env=environment)
        print('DONE', label, flush=True)
    for kind, path in models.items():
        def shard(i):
            out = parts / f'{kind}-{i}.jsonl'
            run(f'parts/{kind}-{i}', ['decode',kind,path,a.table,root/'tokens.json',data,out,i,a.jobs])
            return out
        with ThreadPoolExecutor(max_workers=a.jobs) as pool:
            paths = list(pool.map(shard, range(a.jobs)))
        rows = [r for path in paths for r in metrics.decode_rows(path)]
        metrics.validate(cases, rows, 'test')
        merged = root / f'{kind}.jsonl'
        write_rows(merged, (json.loads(line) for path in paths for line in path.open()))
        metadata = {str(path.relative_to(root)): json.loads(Path(str(path)+'.manifest.json').read_text()) for path in paths}
        metadata['sha256'] = metrics.digest(merged)
        Path(str(merged)+'.manifest.json').write_text(json.dumps(metadata,indent=2))
        print(kind, json.dumps(metrics.accuracy(rows)), flush=True)
    # No competing decoder processes while measuring per-key latency.
    for kind, path in models.items():
        run('latency-'+kind, ['bench',kind,path,a.table,root/'tokens.json',data,root/f'latency-{kind}.jsonl'])


if __name__ == '__main__':
    main()
