"""StringInfo text-element and actual ConstructCiFromLookup differential."""
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
    for start in range(0, 0x110000, 512):
        text = '|'.join(chr(c) for c in range(start, min(start+512, 0x110000)))
        cases.append(dict(graphemes=units(text)))
    representatives = ['a', '\r', '\n', '\0', '\u0301', '\u200d', '🇦', '🇧', '\u0600', '\u0903',
                       '\u1100', '\u1161', '\u11a8', '\uac00', '\uac01', '👩', '🏽', '💻', '\ud800', '\udc00']
    rng = random.Random(20260917)
    for _ in range(30000):
        cases.append(dict(graphemes=units(''.join(rng.choices(representatives, k=rng.randrange(30))))))
    words = ['甲', '乙', '丙', '丁', '戊', 'e\u0301', '𠀀', '👩\u200d💻', '\ud800', '…', '—', 'A', 'Z', '9']
    for _ in range(10000):
        lookup = [dict(text=units(word), code=units(rng.choice(['', 'x', 'ab', 'cdef', 'XY', '𠀀']))) for word in rng.sample(words, rng.randrange(len(words)))]
        text = ''.join(rng.choices(words + ['?', '\n', ' ', '……', '——', '#', '\u3000', '）'], k=rng.randrange(15)))
        cases.append(dict(word=units(text), lookup=lookup))
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Case {i}: {cases[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(cases), construct_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
