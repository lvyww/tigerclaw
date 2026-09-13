"""Ordinary composition operations vs actual C# idle/composing key branches."""
import argparse
import json
import random
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--native', required=True)
p.add_argument('--dotnet', required=True)
p.add_argument('--assembly', required=True)
a = p.parse_args()
rng = random.Random(20260929)
cases = []
for _ in range(120):
    table = {'ab': ['first', 'second', 'third'], 'abc': ['long'], 'abd': [], 'xy': ['unique']}
    for prefix in [';', '/', '[', 'z']:
        if rng.randrange(2):
            table[prefix] = [f'symbol{i}' for i in range(rng.randrange(4))]
        table[prefix + 'a'] = [f'short{i}' for i in range(rng.randrange(4))]
    for _ in range(35):
        code = ''.join(rng.choice('abcxy') for _ in range(rng.randrange(1, 7)))
        table[code] = [f'text{i}' for i in range(rng.randrange(8))]
    cases.append(dict(entries=[dict(code=k, texts=v) for k, v in table.items()],
                      max=rng.choice([1, 2, 3, 4, 16]), size=rng.choice([1, 2, 5, 10]),
                      auto=bool(rng.randrange(2)), clear=bool(rng.randrange(2)),
                      enter=bool(rng.randrange(2)), tab=bool(rng.randrange(2)),
                      english=bool(rng.randrange(2)), slash=bool(rng.randrange(2)),
                      second=bool(rng.randrange(2)), third=bool(rng.randrange(2)),
                      ordinary_trace=[dict(vk=rng.choice([65, 66, 67, 88, 89, 90]*5 +
                                                [8, 9, 13, 27, 32, 49, 50, 57, 48, 187, 189,
                                                 186, 188, 190, 191, 219, 220, 221, 222]),
                                           shift=bool(rng.randrange(3) == 0)) for _ in range(300)]))
ordinary_cases = list(cases)
cases += [dict(c, uppercase=True, upper_start=rng.choice([65, 68, 83])) for c in ordinary_cases]
for c in ordinary_cases:
    # Plain symbol re-entry needs the future outer dispatcher. Keep this trace
    # on in-mode edits/selectors; shifted symbols are complete local operations.
    keys = [dict(vk=rng.choice([65, 66, 67, 88, 89]*5 + [8, 9, 13, 27, 32, 48, 49, 50, 57, 187, 189, 222]),
                 shift=bool(rng.randrange(3) == 0)) for _ in range(300)]
    cases.append(dict(c, pinyin=True, ordinary_trace=keys))
for c in ordinary_cases:
    keys = [dict(vk=rng.choice([65, 66, 67, 88, 89, 90]*5 +
                 [8, 9, 13, 27, 32, 48, 49, 50, 57, 186, 187, 188, 189, 190, 191, 192, 219, 220, 221, 222]),
                 shift=bool(rng.randrange(3) == 0)) for _ in range(300)]
    cases.append(dict(c, chinese=True, ordinary_trace=keys))
cases += [dict(c, postprocess=True) for c in cases if c.get('chinese')]
for c in list(cases):
    if c.get('postprocess'):
        keys = [dict(vk=0, shift=False, language=bool(rng.randrange(2))) if i % 17 == 0 else key
                for i, key in enumerate(c['ordinary_trace'])]
        cases.append(dict(c, ordinary_trace=keys))
for c in list(cases):
    if c.get('postprocess'):
        keys = [dict(vk=20, shift=False) if i % 23 == 0 else key for i, key in enumerate(c['ordinary_trace'])]
        cases.append(dict(c, ordinary_trace=keys))
for c in ordinary_cases:
    keys = []
    for i in range(300):
        vk = rng.choice([65, 66, 67, 88, 89, 90]*4 + [8, 9, 13, 27, 32, 49, 50, 187, 189,
                        186, 188, 190, 191, 192, 219, 220, 221, 222, 160, 161, 20])
        keys.append(dict(vk=vk, shift=bool(rng.randrange(3) == 0), up=bool(rng.randrange(3) == 0),
                         ctrl=vk == 67 and bool(rng.randrange(2))))
    cases.append(dict(c, chinese=True, postprocess=True, physical=True, ordinary_trace=keys))
cases += [dict(c, mixed=True) for c in list(cases) if c.get('chinese') and not c.get('physical')]
for c in list(cases):
    if c.get('physical'):
        # Exercise quote key-down only here; isolated quote-up has a separately
        # identified reference mixed-state bug and needs its own regression.
        keys = [dict(key, up=False) if key['vk'] == 222 else key for key in c['ordinary_trace']]
        cases.append(dict(c, mixed=True, ordinary_trace=keys))
for c in list(cases):
    if c.get('mixed') and c.get('postprocess') and not c.get('physical'):
        keys = [dict(vk=0, shift=False, schema_max=rng.choice([1, 2, 4, 16]), schema_mixed=bool(rng.randrange(2)))
                if i % 19 == 0 else key for i, key in enumerate(c['ordinary_trace'])]
        cases.append(dict(c, ordinary_trace=keys))
payload = ''.join(json.dumps(c)+'\n' for c in cases).encode()
def run(cmd):
    return subprocess.run(cmd, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(cases)
for index, (left, right) in enumerate(zip(expected, actual)):
    left, right = json.loads(left), json.loads(right)
    for step, (l, r) in enumerate(zip(left, right)):
        assert l == r, (index, step, cases[index], l, r)
    assert len(left) == len(right) == 300
print(json.dumps(dict(cases=len(cases), operations=len(cases)*300, ordinary_parity=True)))
