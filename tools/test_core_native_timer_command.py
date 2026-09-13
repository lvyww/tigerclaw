"""Timer command delays against .NET parsing and reference arithmetic (no timers)."""
import argparse
import json
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(20261001)
codes = ['Ds' + value for value in ['', ',', '.', '0', '.0', '.000001', '1', '1,000',
                                    '35791.39411666666', '35791.39411666667', '9'*400,
                                    '0.'+'0'*400+'1', '0.'+'0'*323+'5', '1\n', '1\r\n']]
for _ in range(12000):
    value = str(rng.randrange(100000)) + '.' + str(rng.randrange(10**18)).zfill(18)
    if rng.randrange(2):
        value = '0.' + '0'*rng.randrange(340) + str(rng.randrange(1, 10**18))
    if rng.randrange(3) == 0:
        value = ','.join(value)
    codes.append(rng.choice(['Ds', 'DS', 'ds', 'S']) + value + rng.choice(['', '', '\n', '\r\n']))
payload = ''.join(json.dumps(dict(timer_command=c))+'\n' for c in codes).encode()
def run(cmd):
    return subprocess.run(cmd, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
left = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
right = run([a.native, '--lexicon-text-stdio'])
assert len(left) == len(right) == len(codes)
for i, (l, r) in enumerate(zip(left, right)):
    assert json.loads(l) == json.loads(r), (i, codes[i], l, r)
print(json.dumps(dict(cases=len(codes), timer_command_delay_parity=True)))
