"""Fixed-length mixed decoder vs actual C# decoder, including raw UTF-16 units."""
import argparse
import json
import random
import subprocess
p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(20261002)
cases = [dict(mixed_decode=''.join(rng.choices('abABxyXY', k=rng.randrange(100))),
              maximum=rng.choice([-1, 0, 1, 2, 3, 4, 16, 128]),
              preferred=[[rng.randrange(100), rng.choice(['', 'chosen', 'other'])] for _ in range(12)]) for _ in range(5000)]
alphabet = [0, 65, 97, 88, 120, 0x131, 0x130, 0x3c2, 0x3c3, 0x3a3,
            0x4e00, 0x301, 0xd800, 0xdc00, 0xdbff, 0xdfff, 0xffff]
# Arrays preserve lone surrogates and segment boundaries inside surrogate pairs;
# JSON string replacement must not conceal a decoder mismatch.
cases += [dict(mixed_units=[rng.choice(alphabet) if rng.randrange(2) else rng.randrange(65536)
                            for _ in range(rng.randrange(100))],
               maximum=rng.choice([-1, 0, 1, 2, 3, 4, 16, 128]),
               preferred=[[rng.randrange(100), [rng.randrange(65536) for _ in range(rng.randrange(5))]]
                          for _ in range(12)]) for _ in range(5000)]
cases += [dict(mixed_units=list(pair) * 4 + [97], maximum=1, preferred=[])
          for pair in [(65, 97), (0x131, 73), (0x3c2, 0x3c3), (0xd800, 0xdc00)]]
payload = ''.join(json.dumps(c)+'\n' for c in cases).encode()
def run(cmd):
    return [json.loads(line) for line in subprocess.run(cmd, input=payload, capture_output=True, check=True, timeout=60).stdout.splitlines()]
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(cases)
for case, left, right in zip(cases, expected, actual):
    assert left == right, (case, left, right)
print(json.dumps(dict(cases=len(cases), mixed_parity=True)))
