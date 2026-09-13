"""Invariant numeric-prefix recognition against actual C# IsTimerOrCnum."""
import argparse
import json
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(20260930)
numbers = ['', '0', '1.', '.1', '1e99999999999', '1e-99999999999', 'NaN', '-NaN',
           '+NaN', 'Infinity', '-Infinity', '+Infinity', 'inf', '∞', '1,000', '1\0',
           '1 \0', '1\0 ', 'NaN\0', '\u3000NaN\u3000', '\u30001\u3000', '\ud800']
codes = [prefix + lead + number + tail for prefix in ['D', 'd', 'S', 's', '', 'DS']
         for lead in ['', ' ', '\t', '\u00a0'] for number in numbers
         for tail in ['', '\n', '\0', '\u3000']]
for _ in range(20000):
    number = ''.join(rng.choice('0123456789.,eE+- \t\r\n\0NaInfity') for _ in range(rng.randrange(25)))
    codes.append(rng.choice(['D', 'd', 'S', 's', 'DS']) + number)
for _ in range(5000):
    codes.append(rng.choice(['D', 'd', 'S']) + rng.choice(['', '+', '-']) +
                 str(rng.randrange(10**20)) + rng.choice(['', '.123', 'e+400', 'e-400', '.0E9']))
codes += [prefix + body + suffix for prefix in ['Ds', 'DS', 'ds', 'S', 's', 'D']
          for body in ['', ',', '.,', '1.', '.1', '1,2.3,4', '..1', '1.2.3', '١', '0', '+1', '-1']
          for suffix in ['', '\n', '\r\n', '\n\n', '\0', ' ']]
for _ in range(10000):
    codes.append(rng.choice(['Ds', 'DS', 'ds', 'S', 's']) +
                 ''.join(rng.choice('0123456789,.') for _ in range(rng.randrange(50))) +
                 rng.choice(['', '\n', '\r\n', '\0']))
def units(text):
    b = text.encode('utf-16-le', errors='surrogatepass')
    return [int.from_bytes(b[i:i+2], 'little') for i in range(0, len(b), 2)]
payload = ''.join(json.dumps(dict(upper_numeric=units(c)))+'\n' for c in codes).encode()
def run(cmd):
    return subprocess.run(cmd, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
left = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
right = run([a.native, '--lexicon-text-stdio'])
assert len(left) == len(right) == len(codes)
for i, (l, r) in enumerate(zip(left, right)):
    assert json.loads(l) == json.loads(r), (i, repr(codes[i]), l, r)
print(json.dumps(dict(cases=len(codes), upper_numeric_parity=True)))
