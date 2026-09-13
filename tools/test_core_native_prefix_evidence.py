"""Compare prefix and boundary confidence aggregation against actual C# method."""
import argparse
import json
import math
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(202609097)
cases = []
for index in range(5000):
    candidates = []
    for j in range(rng.randrange(21)):
        text = [rng.choice([97, 98, 30002, 20057, 0xd840, 0xdc00, 0x301]) for _ in range(rng.randrange(1, 12))]
        boundaries = [[rng.randrange(len(text)+2), rng.randrange(1, 20)] for _ in range(rng.randrange(1, 8))]
        candidates.append(dict(text=text, boundaries=boundaries, mass=rng.choice([0.0, -1000.0, -12.0, rng.uniform(-100, 0)])))
    cases.append(dict(prefix_evidence=candidates))
payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
def run(command):
    return [json.loads(line) for line in subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()]
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(cases)
count = 0
maximum_error = 0.0
for index, (left, right) in enumerate(zip(expected, actual)):
    assert len(left) == len(right), (index, left, right)
    for x, y in zip(left, right):
        assert all(x[key] == y[key] for key in ['text', 'raw', 'closed']), (index, x, y)
        for key in ['share', 'boundary_share']:
            maximum_error = max(maximum_error, abs(x[key]-y[key]))
            assert math.isclose(x[key], y[key], rel_tol=1e-13, abs_tol=1e-12), (index, x, y)
        count += 1
print(json.dumps(dict(cases=len(cases), prefixes=count, maximum_error=maximum_error, parity=True)))
