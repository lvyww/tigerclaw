"""Uppercase currency commands vs actual C# ConvertToChineseCurrency."""
import argparse
import json
import random
import subprocess
p = argparse.ArgumentParser(description=__doc__)
for n in ['native', 'dotnet', 'assembly']:
    p.add_argument('--'+n, required=True)
a = p.parse_args()
rng = random.Random(20261002)
values = ['0', '0.005', '1.005', '100000001', ',', '.,',
          '79228162514264337593543950335', '79228162514264337593543950336',
          '79228162514264337593543950335.4', '79228162514264337593543950335.5']
for _ in range(12000):
    integer = ''.join(rng.choice('00000123456789') for _ in range(rng.randrange(1, 34)))
    fraction = ''.join(rng.choice('00000123456789') for _ in range(rng.randrange(1, 50)))
    value = integer + rng.choice(['', '.'+fraction])
    if rng.randrange(4) == 0:
        value = ','.join(value)
    values.append(value)
values += [f'{i//100}.{i%100:02}' for i in range(10001)]
for n in ['79228162514264337593543950335', '79228162514264337593543950334', '9999999999999999999999999999']:
    for scale in range(29):
        number = n if not scale else n[:-scale] + '.' + n[-scale:]
        for tail in ['0', '4', '5', '500000000000000000000000001', '499999999999999999999999999', '9']:
            values.append(number + ('' if scale else '.') + tail)
codes = ['S'+v for v in values]
payload = ''.join(json.dumps(dict(currency_command=c))+'\n' for c in codes).encode()
def run(cmd):
    return subprocess.run(cmd, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
left = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
right = run([a.native, '--lexicon-text-stdio'])
assert len(left) == len(right) == len(codes)
for i, (l, r) in enumerate(zip(left, right)):
    assert json.loads(l) == json.loads(r), (i, codes[i], json.loads(l), json.loads(r))
print(json.dumps(dict(cases=len(codes), currency_parity=True)))
