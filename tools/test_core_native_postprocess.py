"""PostProcessKey traces on isolated C# objects; no persistence actions or live input."""
import argparse
import hashlib
import json
import random
import subprocess

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    rng = random.Random(20260926)
    def units(text):
        data = text.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(data[i:i+2], 'little') for i in range(0, len(data), 2)]
    texts = ['', 'x', 'abc', '9', '９', '。', ':', '：', '“', '”', '‘', '’', 'á', '😀', '\ud800', '1️⃣', '{重复上屏}', '{添加}', '{加词}', 'unknown']
    cases = []
    for _ in range(100):
        steps = []
        for _ in range(200):
            steps.append(dict(vk=rng.choice([8, 0x20, 0x31, 0x41, 0x11, 0x14, 0xa0, 0xbe, 0xde]), flags=rng.randrange(32),
                              down=rng.randrange(5) != 0, handled=rng.choice([True, False]), text=units(rng.choice(texts)), quote=rng.choice([0, 0, 0, 1, 2])))
        cases.append(dict(post_trace=steps))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        a, b = json.loads(left), json.loads(right)
        for step, (x, y) in enumerate(zip(a, b)):
            assert x == y, (index, step, cases[index]['post_trace'][step], x, y)
        assert len(a) == len(b) == 200
    print(json.dumps(dict(operations=20000, postprocess_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))

if __name__ == '__main__':
    main()
