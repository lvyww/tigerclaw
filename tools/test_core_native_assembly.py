"""Normalization and frequency-ordered candidate assembly differential."""
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
    def units(text):
        raw = text.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]
    cases = []
    # Separators prevent accidental surrogate pairing and edge trim; additional
    # singleton cases check whitespace removal and malformed UTF-16 explicitly.
    for start in range(0, 0x110000, 512):
        cases.append(dict(normalize=units('|' + '|'.join(chr(c) for c in range(start, min(start+512, 0x110000))) + '|')))
    for c in range(65536):
        cases.append(dict(normalize=[c]))
    rng = random.Random(20260913)
    codes = ['ab', 'AB', ' Ab ', 'z', 'Z', '', '\u3000', 'ß', 'ẞ', 's', 'ſ', 'Σ', 'ς', 'σ',
             'I', 'i', 'İ', 'ı', 'K', 'K', 'k', '𐐀', '𐐨', '\ud800', '\udc00', 'a\x00b']
    texts = ['字', '词', 'Word', 'word', '', '\ud800', '\x00', '𠀀']
    for _ in range(2000):
        rows = [dict(code=units(rng.choice(codes)), text=units(rng.choice(texts)),
                     freq=rng.choice([0, 1, 2, 99, -1, -2147483648, 2147483647]))
                for _ in range(rng.randrange(100))]
        cases.append(dict(rows=rows))
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Case {i}: {cases[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(cases), assembly_parity=True,
                          trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
