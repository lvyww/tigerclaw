"""Exhaustive VK/modifier creation, exact matching and reserved conflict parity."""
import argparse
import json
import subprocess
import random

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--native', required=True)
p.add_argument('--dotnet', required=True)
p.add_argument('--assembly', required=True)
a = p.parse_args()
cases = [dict(shortcut_create=[key, flags]) for key in range(-1, 258) for flags in range(16)]
rng = random.Random(20261001)
texts = ['Ctrl+VK_M', '', 'Ctrl++VK_M', 'Ctrl+Ctrl+VK_M', 'Shift+VK_A', 'Alt+0x14']
for key in range(258):
    for prefix in ['Ctrl+', 'Alt+Shift+', 'Win+Ctrl+']:
        texts.append(prefix + f'0x{key:X}')
for _ in range(10000):
    parts = rng.choices(['Ctrl', 'Control', 'ctrl', 'Alt', 'Shift', 'Win', 'Windows', 'VK_M',
                        'vk_a', 'VK_F24', 'VK_F01', 'VK_NUMPAD1', 'VK_OEM_PLUS', '0x0',
                        '0x0000BB', '0x FF', '0xFF\0', '0xFF \0', '0xFFFFFFFF', '0x100', '', 'M', '77'], k=rng.randrange(1, 6))
    texts.append('+'.join(rng.choice(['', ' ', '\u3000', '\t']) + part + rng.choice(['', ' ', '\u00a0']) for part in parts))
for text in texts:
    encoded = text.encode('utf-16-le')
    cases.append(dict(shortcut_parse=[encoded[i] + 256*encoded[i+1] for i in range(0, len(encoded), 2)]))
payload = ''.join(json.dumps(c) + '\n' for c in cases).encode()
def run(cmd):
    return [json.loads(line) for line in subprocess.run(cmd, input=payload, capture_output=True,
            check=True, timeout=60).stdout.splitlines()]
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(cases)
for case, left, right in zip(cases, expected, actual):
    assert left == right, (case, left, right)
print(json.dumps(dict(cases=len(cases), shortcut_parity=True)))
