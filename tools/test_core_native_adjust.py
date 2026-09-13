"""Replay adjustment prefixes against actual C# actions; compare compact bytes."""
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
    rng = random.Random(20260914)
    cases = []
    texts = ['字', '词', 'a\x1eb', 'b', 'c\x1eb', '', '\x1eb', 'a\x1e', 'a\x1eb\x1ec', '\ud800', '𠀀', '#literal']
    payloads = ['字', '词', 'a=>b', 'b', 'c=>b', '\\s', '\\n', '\\t', '#literal', '\\#', '', '\ud800', '𠀀', 'a\x1e']
    for _ in range(200):
        source = [dict(code=units(code), candidates=[units(rng.choice(texts)) for _ in range(rng.randrange(8))])
                  for code in ['z', 'ab', 'x']]
        commands = []
        for _ in range(25):
            action = rng.choice(['{添加}', '{置顶}', '{删除}', '{前移}', '{unknown}', '#comment'])
            code = rng.choice(['z', 'Z', ' ab ', 'x', 'new', '', ' '])
            commands.append(units(action + code + rng.choice(['\t', '\t\t', ' ']) + rng.choice(payloads) + rng.choice(['', '\textra', ' #tail'])))
            # Every prefix is checked, so later deletes cannot hide a bad earlier move.
            cases.append(dict(entries=source, adjustments=commands.copy()))
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Adjustment prefix {i} mismatch: {cases[i]}')
    print(json.dumps(dict(prefixes=len(cases), adjustment_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
