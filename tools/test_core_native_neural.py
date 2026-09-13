"""Compare native neural weighting/order with the actual C# helpers/comparers."""
import argparse
import json
import math
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(202609099)
cases = []
for _ in range(10000):
    candidates, seen = [], set()
    for j in range(rng.randrange(10)):
        while True:
            text = ''.join(rng.choice(['a', 'b', '甲', '乙', 'a\u0301', '𠀀', '👨‍👩‍👧', '\r\n'])
                           for _ in range(rng.randrange(10)))
            if text not in seen:
                seen.add(text)
                break
        encoded = text.encode('utf-16-le')
        units = [int.from_bytes(encoded[i:i+2], 'little') for i in range(0, len(encoded), 2)]
        candidates.append(dict(text=units, base=rng.choice([0., -10., rng.uniform(-200, 20)]),
                               neural=rng.choice([0., -10., rng.uniform(-200, 20)]),
                               rank=rng.randrange(1, 5), segmented=rng.choice([False, True])))
    cases.append(dict(neural_candidates=candidates, duplicates=rng.choice([False, True])))
payload = ''.join(json.dumps(c) + '\n' for c in cases).encode()
def run(command):
    return [json.loads(line) for line in subprocess.run(command, input=payload, capture_output=True,
            check=True, timeout=120).stdout.splitlines()]
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(cases)
maximum_error = 0.
for index, (left, right) in enumerate(zip(expected, actual)):
    assert len(left) == len(right), (index, left, right)
    for x, y in zip(left, right):
        assert x['index'] == y['index'], (index, cases[index], left, right)
        maximum_error = max(maximum_error, abs(x['score'] - y['score']))
        assert math.isclose(x['score'], y['score'], rel_tol=1e-13, abs_tol=1e-12), (index, x, y)
print(json.dumps(dict(cases=len(cases), candidates=sum(len(c['neural_candidates']) for c in cases),
                     maximum_error=maximum_error, parity=True)))
