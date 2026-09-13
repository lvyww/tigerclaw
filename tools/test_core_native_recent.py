"""Recent-schema history and target-selection differential (no runtime writes)."""
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
    rng = random.Random(20260923)
    names = ['A', 'a', 'B', 'C', '', ' ', '\u3000', '虎整句', '快符', 'Σ', 'ς', 'σ', 'İ', 'ı', 'ſ', 'S', '𐐀', '𐐨', '\ud800', '\udc00', 'x|y']
    cases = []
    for _ in range(10000):
        # Keep a canonical distinct list; current/history names include Unicode
        # and invalid unit edge cases. File list ordering is tested separately.
        schemas = rng.sample(['A', 'B', 'C', '虎整句', '快符', 'Σ', 'İ', '𐐀'], rng.randrange(9))
        current = rng.choice(names)
        persisted = '|'.join(rng.choice(names) for _ in range(rng.randrange(8)))
        record = [rng.choice(['', ' ', '\u3000']) + rng.choice(names) + rng.choice(['', ' ', '\u3000']) for _ in range(rng.randrange(8))]
        cases.append(dict(recent_schemas=dict(persisted=units(persisted), record=list(map(units, record)), schemas=list(map(units, schemas)), current=units(current))))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (index, cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), recent_schema_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
