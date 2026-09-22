"""Isolated 500,000,000-byte experiment; reuses frozen full-history evaluator."""
import argparse
import ctypes as C
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import time
import shutil

R = Path('/mnt/c/Archive/tigerclaw_sentence_ml')
O = R / 'experiments/joint-500mb-20260922'
B = R / 'experiments/joint-pinyin-20260921'
E = R / 'experiments/joint-5gram-beam-20260921'
REPO = Path(__file__).resolve().parents[3]

def dump(path, obj):
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + '\n')

def sha(path):
    h = hashlib.sha256()
    expected = path.stat().st_size
    count = 0
    with path.open('rb') as f:
        while count < expected:
            for attempt in range(6):
                try:
                    f.seek(count)
                    block = f.read(min(1024**2, expected-count))
                    if not block:
                        raise OSError('Premature end of model file')
                    break
                except OSError:
                    if attempt == 5:
                        raise
                    time.sleep(1)
            h.update(block)
            count += len(block)
    assert count == expected == path.stat().st_size
    return h.hexdigest()

def rows(path):
    with path.open() as f:
        for line in f:
            yield json.loads(line)

def local_input(path):
    """Avoid transient large-read ENOMEM from the Windows-mounted filesystem."""
    path = Path(path)
    digest = sha(path)
    cache = Path('/home/yc/tmp/tiger500/eval-inputs')
    cache.mkdir(exist_ok=True)
    target = cache / (digest[:16] + '-' + path.name)
    if not target.exists():
        temporary = target.with_suffix(target.suffix + '.copying')
        shutil.copyfile(path, temporary)
        assert sha(temporary) == digest
        temporary.replace(target)
    assert target.stat().st_size == path.stat().st_size and sha(target) == digest
    return target

def summary(path):
    data = list(rows(path))
    return {'n': len(data), 'correct': sum(r['rank'] == 1 and r['consumed'] == len(r['code']) for r in data),
            'top50': sum(0 < r['rank'] <= 50 and r['consumed'] == len(r['code']) for r in data)}

def decode(name, model, split, jobs, rerank=None, alpha=1.):
    cases = E / 'dev-eval.jsonl' if split == 'dev' else B / 'cases.jsonl'
    model = local_input(model)
    cases = local_input(cases)
    table = local_input(REPO / 'release/拼音反查码表/拼音.txt')
    tokens = local_input(B / 'tokens.json')
    for folder in ('parts', 'logs'):
        (O / folder).mkdir(exist_ok=True)
    output = O / (name + '.jsonl')
    assert not output.exists(), output
    env = os.environ.copy()
    env['LD_LIBRARY_PATH'] = str(E / 'bin')
    env.pop('BUDGET_RERANK_MODEL', None)
    env.pop('BUDGET_RERANK_ALPHA', None)
    binary = E / 'bench-bin/Joint.dll'
    if rerank is not None:
        env['BUDGET_RERANK_MODEL'] = str(local_input(rerank))
        env['BUDGET_RERANK_ALPHA'] = str(alpha)
        binary = O / 'combo-v2-bin/Joint.dll'
    base = [str(B / 'dotnet/dotnet'), str(binary), 'decode', 'joint', str(model),
            str(table), str(tokens), str(cases)]
    procs = []
    for i in range(jobs):
        part = O / 'parts' / f'{name}-{i}.jsonl'
        assert not part.exists(), part
        log = (O / 'logs' / f'{name}-{i}.log').open('x')
        procs.append((subprocess.Popen(base + [str(part), str(i), str(jobs)], env=env,
                                      stdout=log, stderr=subprocess.STDOUT), log, part))
    failed = []
    for p, log, part in procs:
        rc = p.wait()
        log.close()
        if rc:
            failed.append((str(part), rc))
    assert not failed, failed
    data = [r for _, _, part in procs for r in rows(part)]
    expected = [r for r in rows(cases) if r['split'] == 'test']
    lookup = {r['id']: r for r in data}
    assert len(lookup) == len(data) == len(expected)
    with output.open('x') as f:
        for c in expected:
            r = lookup[c['id']]
            assert (r['text'], r['code']) == (c['text'], c['code'])
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    dump(O / (name + '-summary.json'), summary(output))
    print(name, summary(output), flush=True)

def native():
    lib = C.CDLL(str(E / 'bin/libhigherorder.so'))
    lib.ho_load.argtypes = [C.c_char_p]
    lib.ho_load.restype = C.c_void_p
    lib.ho_score.argtypes = [C.c_void_p, C.c_char_p, C.POINTER(C.c_uint)]
    lib.ho_score.restype = C.c_double
    lib.ho_free.argtypes = [C.c_void_p]
    return lib

def rescore(name, model, source):
    lib = native()
    m = lib.ho_load(str(local_input(model)).encode())
    assert m
    oov = C.c_uint()
    output = O / (name + '.jsonl')
    with output.open('x') as out:
        for r in rows(source):
            candidates = []
            for c in r['candidates']:
                tokens = [t for s in c['segments'] for t in s['tokens']]
                assert ''.join(t.rsplit('/', 1)[0] for t in tokens) == c['text']
                value = lib.ho_score(m, ' '.join(tokens).encode(), C.byref(oov)) * math.log(10) + 2 * len(tokens)
                assert math.isfinite(value)
                candidates.append(dict(c, score=value))
            candidates.sort(key=lambda c: (-c['score'], -c['frequency'], c['text'].encode('utf-16-be')))
            result = {k: r[k] for k in ('id', 'text', 'code', 'consumed')}
            result.update(candidates=candidates, rank=next((i for i, c in enumerate(candidates, 1) if c['text'] == r['text']), 0))
            out.write(json.dumps(result, ensure_ascii=False) + '\n')
    lib.ho_free(m)
    dump(O / (name + '-summary.json'), summary(output))
    dump(O / (name + '.manifest.json'), {'model': str(model), 'modelSha256': sha(model),
         'source': str(source), 'sourceSha256': sha(source), 'timingMeasured': False})
    print(name, summary(output), flush=True)

if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('mode', choices=['decode', 'rescore'])
    p.add_argument('name')
    p.add_argument('model', type=Path)
    p.add_argument('source', help='dev/test for decode, candidate JSONL for rescore')
    p.add_argument('--jobs', type=int, default=4)
    p.add_argument('--rerank', type=Path)
    p.add_argument('--alpha', type=float, default=1.)
    a = p.parse_args()
    if a.mode == 'decode':
        assert a.source in ('dev', 'test')
        assert 0 <= a.alpha <= 1
        decode(a.name, a.model, a.source, a.jobs, a.rerank, a.alpha)
    else:
        rescore(a.name, a.model, Path(a.source))
