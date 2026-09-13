"""Offline UTF-16 lexicon token differential against production C# helpers."""
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
    rng = random.Random(20260909)
    cases = ['', '#comment', r'\#literal', r'\\#comment', 'a=>a', 'a=>b=>c', '=>a',
             'a=>', r'\s=>\t', 'Bime20231222BIME', r'\\n\n', '\u3000x\u00a0']
    # Every code unit is checked both bare and at a trim/comment/token boundary.
    for unit in range(65536):
        cases.append(chr(unit))
        cases.append(chr(unit) + 'x=>y#z' + chr(unit))
    pieces = ['\\', '#', '=>', ' ', '\t', 'n', 't', 's', '\r\n', 'Bime20231222BIME',
              '字', '𠀀', '\ud800', '\x00', '\u3000', '\x1e']
    for _ in range(10000):
        cases.append(''.join(rng.choice(pieces) for _ in range(rng.randrange(1, 25))))
    def units(value):
        raw = value.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]
    payload = ''.join(json.dumps({'text': units(value)}) + '\n' for value in cases).encode()
    def run(command):
        result = subprocess.run(command, input=payload, capture_output=True, timeout=180, check=True)
        return result.stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    if len(expected) != len(cases) or len(actual) != len(cases):
        raise AssertionError('Output row count mismatch')
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Case {i}: {ascii(cases[i])}: C#={left!r}, C++={right!r}')
    print(json.dumps({'cases': len(cases), 'text_parity': True,
                      'trace_sha256': hashlib.sha256(payload).hexdigest()}))


if __name__ == '__main__':
    main()
