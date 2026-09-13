"""Isolated full sentence lattice differential against the actual C# decoder.

Neutral or real n-gram model, optional isolation/supplements: verifies lexicon filtering,
selectors, segmentation, beam pruning, candidate ordering and confidence mass.
Does not claim coverage of incremental decoding, early commit or the live host.
"""
import argparse
import json
import math
import os
from pathlib import Path
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
p.add_argument('--model', help='Read-only Windows V2 n-gram model; omitted uses neutral scoring')
p.add_argument('--isolation', action='store_true', help='Enable isolation scoring with canonical C# character ranks')
p.add_argument('--supplements', action='store_true', help='Enable overlapping supplemental text rewards')
p.add_argument('--incremental', action='store_true', help='Compare continuous editing against C# incremental decoder')
p.add_argument('--paths', action='store_true', help='Compare exact reachability/prefix/excluded-text queries without Beam')
p.add_argument('--evidence', action='store_true', help='Compare complete and incomplete-tail early-commit evidence')
a = p.parse_args()
rng = random.Random(202609091)

def units(text):
    data = text.encode('utf-16-le', errors='surrogatepass')
    return [data[i] + 256 * data[i+1] for i in range(0, len(data), 2)]

cases = []
codes = ['a', 'b', 'aa', 'ab', 'ba', 'bb', 'aaa', 'aab', 'aba', 'aaaa', ';', '[', '/']
texts = ['甲', '乙', '丙', '丁', '甲乙', '乙甲', '甲甲', '𠀀', 'a\u0301', '', '甲']
for index in range(4000):
    selected = rng.sample(codes, rng.randrange(2, len(codes)+1))
    entries = [[units(code), [units(rng.choice(texts)) for _ in range(rng.randrange(1, 12))]] for code in selected]
    if index % 2:
        raw = ''.join(rng.choice(selected) + rng.choice(['', '', '', ';', "'", '2', '0', '00', '0003'])
                      for _ in range(rng.randrange(1, 6)))
    else:
        raw = ''.join(rng.choice('aaabb ;/[') for _ in range(rng.randrange(0, 17)))
    if index % 3 == 0:
        raw = raw.upper()
    if index % 7 == 0:
        raw = '\u2003' + raw + '\t'
    # A second family keeps every 2/3/4-key edge alive to exercise actual
    # ambiguous lattices, duplicate-text merges and aggressive pruning.
    if index >= 2000:
        entries = [[units(code), [units(t) for t in rng.sample(texts[:9], 5)]]
                   for code in ['aa', 'aaa', 'aaaa', 'aaaaaa']]
        raw = 'a' * rng.randrange(2, 25)
    cases.append(dict(sentence_raw=units(raw), entries=entries,
                      common=[units(t) for t in rng.sample(texts[:4], rng.randrange(5))],
                      whitelist=[units(t) for t in rng.sample(texts[:4], rng.randrange(3))],
                      beam=rng.choice([1, 2, 5, 48, 2000]), limit=rng.choice([1, 3, 5, 20]),
                      rank_penalty=rng.choice([0, .03, .7]), reward=rng.choice([0, 2]),
                      single_reward=rng.choice([0, 5]), duplicates=bool(index % 2), boundaries=bool(index % 3)))

if a.supplements:
    for index, case in enumerate(cases):
        case['supplements'] = [[units(text), weight] for text, weight in
            [('甲', 1000), ('乙甲', 5000), ('甲甲', 30000), ('甲甲', 200), ('𠀀', 10000000000),
             ('a\u0301甲', 2500), ('甲乙甲', 1200), ('', 10000), ('丙', -1)]] if index % 3 else []
if a.isolation:
    ranks = {}
    rank_path = Path(__file__).resolve().parents[1] / 'next/TigerClaw.Core/Data/sentence_char_ranks.txt'
    for line in rank_path.read_text(encoding='utf-8-sig').splitlines():
        token = line.strip()
        if token and not token.startswith('#') and token not in ranks:
            ranks[token] = len(ranks) + 1
    for index, case in enumerate(cases):
        case['isolation'] = {'threshold': [0, 1, 3000, 20000][index % 4], 'lambda': 2.0,
                             'log': bool(index % 3)}
if a.incremental:
    cases = cases[:150] + cases[2000:2150]
    for case in cases:
        raw = ''
        sequence = []
        for step in range(50):
            if step % 13 == 0:
                raw = 'aaaaaa'
            elif step % 7 == 0:
                raw = raw[:-1]
            elif step % 11 != 0:
                raw += rng.choice(['a', 'a', 'b', ';', "'", '2'])
            sequence.append(units(raw))
        case['sequence'] = sequence
if a.paths:
    if a.incremental:
        p.error('--paths and --incremental are separate query modes')
    for case in cases:
        case['path_queries'] = [dict(required=units(rng.choice(['', '', '甲', '乙甲', '甲乙甲', '𠀀', 'a\u0301'])),
                                     excluded=None if i % 3 == 0 else units(rng.choice(['', '甲', '甲甲', '甲乙', '乙甲', '𠀀'])),
                                     group=bool(i % 2)) for i in range(15)]
if a.evidence:
    for index, case in enumerate(cases):
        case['evidence_required'] = units(['', '', '', '', '', '甲', '甲乙', '𠀀'][index % 8])
payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
if a.isolation:
    payload = (json.dumps({'sentence_rank_queries': [units(t) for t in ranks] + [[], [0], [0xd800]]}) + '\n').encode() + payload
def run(command):
    environment = os.environ.copy()
    environment.pop('TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL', None)
    if a.model:
        environment['TIGERCLAW_NATIVE_PROBE_SENTENCE_MODEL'] = a.model
    completed = subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180, env=environment)
    return [json.loads(line) for line in completed.stdout.splitlines()]

expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
if a.isolation:
    assert expected.pop(0) == actual.pop(0), 'Embedded rank/TakeTop parity failed'
if a.paths:
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert left == right, (index, cases[index], left, right)
    print(json.dumps(dict(path_queries=sum(len(case['path_queries']) for case in cases), parity=True)))
    raise SystemExit(0)
comparison_cases = [dict(case, sentence_raw=raw) for case in cases for raw in case.get('sequence', [case['sentence_raw']])]
assert len(expected) == len(actual) == len(comparison_cases)
candidate_count = 0
maximum_error = 0.0
negative_scores = 0
for index, (left, right) in enumerate(zip(expected, actual)):
    details = (index, comparison_cases[index], left, right)
    assert left['raw'] == right['raw'] and left['expanded'] == right['expanded'], details
    assert len(left['candidates']) == len(right['candidates']), details
    if a.evidence:
        def compare(x, y):
            if isinstance(x, dict):
                return x.keys() == y.keys() and all(compare(x[key], y[key]) for key in x)
            if isinstance(x, list):
                return len(x) == len(y) and all(compare(v, w) for v, w in zip(x, y))
            if isinstance(x, float):
                return math.isclose(x, y, rel_tol=1e-13, abs_tol=1e-12)
            return x == y
        assert compare(left['evidence'], right['evidence']), details
    for x, y in zip(left['candidates'], right['candidates']):
        candidate_count += 1
        negative_scores += x['score'] < 0
        assert all(x[key] == y[key] for key in ['text', 'code', 'rank']), details
        assert all(math.isclose(x[key], y[key], rel_tol=1e-13, abs_tol=1e-12) for key in ['score', 'mass', 'supplement']), details
        maximum_error = max(maximum_error, *(abs(x[key] - y[key]) for key in ['score', 'mass', 'supplement']))
if a.model:
    assert negative_scores > candidate_count // 2, 'Expected real-model log scores, not neutral fallback'
print(json.dumps(dict(cases=len(comparison_cases), candidates=candidate_count, evidence=a.evidence, incremental=a.incremental, model=bool(a.model), isolation=a.isolation, supplements=a.supplements,
                      rank_queries=len(ranks) + 3 if a.isolation else 0, maximum_error=maximum_error, parity=True)))
