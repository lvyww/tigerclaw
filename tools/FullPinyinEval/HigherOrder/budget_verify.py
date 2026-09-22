"""Score checks, incremental invariants and sequential offline latency for a model."""
import ctypes as C
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
from budget500 import B, E, O, REPO, dump, native, rows, local_input

name, model_arg, candidate_arg = sys.argv[1:4]
model, candidates = Path(model_arg), Path(candidate_arg)
model = local_input(model)
rerank = local_input(Path(sys.argv[4])) if len(sys.argv) > 4 else None
alpha = float(sys.argv[5]) if rerank else 1.
lib = native()
m = lib.ho_load(str(model).encode())
assert m
rm = lib.ho_load(str(rerank).encode()) if rerank else None
assert not rerank or rm
count = 0
delta = 0.
o = C.c_uint()
for i, r in enumerate(rows(candidates)):
    for c in r['candidates']:
        tokens = [t for s in c['segments'] for t in s['tokens']]
        assert ''.join(t.rsplit('/', 1)[0] for t in tokens) == c['text']
        v = lib.ho_score(m, ' '.join(tokens).encode(), C.byref(o))*math.log(10) + 2*len(tokens)
        if rm:
            rv = lib.ho_score(rm, ' '.join(tokens).encode(), C.byref(o))*math.log(10) + 2*len(tokens)
            v += alpha*(rv-v)
        assert math.isfinite(v) and abs(v-c['score']) < 1e-8
        delta = max(delta, abs(v-c['score']))
        count += 1
    if i == 99:
        break
assert count == 5000
lib.ho_free(m)
if rm:
    lib.ho_free(rm)
dump(O / (name + '-score-check.json'), {'candidates': count, 'maxScoreDelta': delta})

env = os.environ.copy()
env['LD_LIBRARY_PATH'] = str(E / 'bin')
env.pop('BUDGET_RERANK_MODEL', None)
env.pop('BUDGET_RERANK_ALPHA', None)
if rerank:
    env['BUDGET_RERANK_MODEL'] = str(rerank)
    env['BUDGET_RERANK_ALPHA'] = str(alpha)
runtime = str(B / 'dotnet/dotnet')
common = [str(model), str(local_input(REPO / 'release/拼音反查码表/拼音.txt')), str(local_input(B / 'tokens.json'))]
if model.name.endswith('joint3-q8.klm'):
    evidence = O / 'logs/incremental-q8-final.log'
    assert 'PASS 983 incremental/full Top50 comparisons' in evidence.read_text()
    dump(O / (name + '-incremental-reference.json'), {'baseDecoderEvidence': str(evidence),
         'scope': 'Unchanged trigram search; reranker is stateless and separately checked against native scores.'})
else:
    with (O / 'logs' / (name + '-incremental.log')).open('x') as log:
        subprocess.run([runtime, str(E / 'incremental-bin/Joint.dll')] + common + [str(local_input(E / 'check100.jsonl'))],
                       env=env, stdout=log, stderr=subprocess.STDOUT, check=True)

out = O / (name + '-latency.jsonl')
with (O / 'logs' / (name + '-latency.log')).open('x') as log:
    binary = O / 'combo-v2-bin/Joint.dll' if rerank else E / 'bench-bin/Joint.dll'
    subprocess.run([runtime, str(binary), 'bench', 'joint'] + common +
                   [str(local_input(E / 'bench30.jsonl')), str(out)], env=env,
                   stdout=log, stderr=subprocess.STDOUT, check=True)
data = list(rows(out))
stats = {}
for direction in ('append', 'backspace'):
    values = sorted(r['ms'] for r in data if r['direction'] == direction)
    stats[direction] = {'n': len(values), 'p50Ms': statistics.median(values),
                        'p95Ms': values[round((len(values)-1)*.95)],
                        'p99Ms': values[round((len(values)-1)*.99)], 'maxMs': max(values)}
dump(O / (name + '-latency-summary.json'), {'scope': 'Same 30 sentences and isolated full-history decoder; sequential after experiments. Includes JIT/cache effects; excludes load/hash/GUI. Not installed Windows latency.', 'stats': stats})
print(name, 'verified and benchmarked', stats, flush=True)
