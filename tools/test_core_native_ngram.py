"""Read-only Windows V2 model score differential against actual C# scorer."""
import argparse
import json
import math
import mmap
import random
import struct
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['native', 'dotnet', 'assembly', 'model']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
rng = random.Random(20261010)

def units(scalar):
    if scalar <= 65535:
        return [scalar]
    scalar -= 65536
    return [0xd800 + (scalar >> 10), 0xdc00 + (scalar & 1023)]

queries = []
with open(a.model, 'rb') as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
    assert data[:8] == b'TCSKNM01' and struct.unpack_from('<i', data, 8)[0] == 1
    position = 12
    sections = []
    for count_format, record_size in [('<i', 8), ('<q', 12), ('<i', 8), ('<q', 12), ('<q', 12)]:
        count = struct.unpack_from(count_format, data, position)[0]
        position += struct.calcsize(count_format)
        sections.append((position, count, record_size))
        position += count * record_size
    assert position == len(data)
    for section_index in [1, 3]:
        offset, count, size = sections[section_index]
        if not count:
            continue
        for _ in range(10000):
            key = struct.unpack_from('<Q', data, offset + rng.randrange(count) * size)[0]
            c, b = key & 0x1fffff, (key >> 21) & 0x1fffff
            first = (key >> 42) & 0x1fffff if section_index == 3 else rng.randrange(0x110000)
            queries.append([units(first), units(b), units(c)])
for _ in range(10000):
    queries.append([units(rng.randrange(0x110000)) for _ in range(3)])
for token in [[], [0], [0xd800], [0xdc00], [97, 98], [0xd840, 0xdc00], [97, 0x301]]:
    queries.extend([[token, other, token] for other in [[], [97], [0xd800]]])
# Repeat keys exercises C# cache-hit results against native lookup results.
queries += queries[:1000]
payload = (json.dumps(dict(ngram_path=a.model, queries=queries)) + '\n').encode()
def run(command):
    return json.loads(subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout)
expected = run([a.dotnet, a.assembly, '--native-core-text-stdio'])
actual = run([a.native, '--lexicon-text-stdio'])
assert len(expected) == len(actual) == len(queries)
maximum_error = 0.0
for query, left, right in zip(queries, expected, actual):
    assert left[2] == right[2], (query, left, right)
    for x, y in zip(left[:2], right[:2]):
        maximum_error = max(maximum_error, abs(x-y))
        assert math.isclose(x, y, rel_tol=1e-13, abs_tol=1e-12), (query, left, right)
print(json.dumps(dict(queries=len(queries), scores=2*len(queries), maximum_error=maximum_error, parity=True)))
