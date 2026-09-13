"""Compare isolated supplemental text file loading against actual C# loader."""
import argparse
import json
import math
from pathlib import Path
import random
import subprocess
import tempfile

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(202609095)
weights = ['', '1', '1000', '2500', '1000000000', '9223372036854775807', '9223372036854775808',
           '-1', '0', '+123', '00042', '1.0', '１２', '\v1000\v', '\u20031000', '123\0', '123\0\v']
with tempfile.TemporaryDirectory(prefix='tiger-supplements-') as directory:
    cases = [{'supplement_dir': ''}, {'supplement_dir': str(Path(directory) / 'absent')}]
    for index in range(300):
        root = Path(directory) / str(index)
        root.mkdir()
        cases.append({'supplement_dir': str(root)})
        if index == 0:
            continue
        lines = ['甲 1000', '乙 2000', '甲 3000', '#skip', 'first second third']
        for _ in range(100):
            lines.append(rng.choice(['甲', '乙', 'A', 'a', '𠀀', 'a\u0301', 'escaped\\#hash', 'x#y']) +
                         rng.choice([' ', '\t', '  \t']) + rng.choice(weights) + rng.choice(['', ' # comment', '\t', '']))
        content = '\r\n'.join(lines)
        encoding = ['utf-8', 'utf-8-sig', 'utf-16', 'utf-32'][index % 4]
        (root / '补充语料.txt').write_bytes(content.encode(encoding))
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        result = subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120)
        return [json.loads(line) for line in result.stdout.splitlines()]
    expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
    actual = run([a.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    count = 0
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert len(left) == len(right), (index, left, right)
        for x, y in zip(left, right):
            assert x['text'] == y['text'] and x['weight'] == y['weight'], (index, x, y)
            assert math.isclose(x['reward'], y['reward'], abs_tol=1e-12, rel_tol=1e-13), (index, x, y)
            count += 1
    print(json.dumps(dict(files=len(cases), entries=count, parity=True)))
