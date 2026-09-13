"""Compare native TCLX compiler output byte-for-byte with C# CompactLexicon.Build."""
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
    rng = random.Random(20260912)
    def units(value):
        raw = value.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]
    cases = [dict(entries=[])]
    special = ['', '字', '可以', '\ud800', '\udfff', '\x00', '𠀀', 'e\u0301', 'a\x1eb', 'x'*1000]
    for _ in range(500):
        codes = rng.sample(range(10000), rng.randrange(100))
        entries = []
        for code in codes:
            text = rng.choice(['code', 'CODE', 'Code']) + str(code)
            values = [rng.choice(special + [text]) for _ in range(rng.randrange(12))]
            entries.append(dict(code=units(text), candidates=list(map(units, values))))
        cases.append(dict(entries=entries))
    cases.append(dict(entries=[dict(code=[], candidates=[[], [0], [0xd800], [0xd800]])]))
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'TCLX byte mismatch in case {i}')
    print(json.dumps(dict(cases=len(cases), binary_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
