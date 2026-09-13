"""Configuration UTF-8 serialization differential against actual C# output builder."""
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
    reference = [args.dotnet, args.assembly, '--native-core-text-stdio']
    def run(command, payload):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    defaults = json.loads(run(reference, b'{"config_lines":[]}\n')[0])
    keys = [pair['key'] for pair in defaults] + [list(map(ord, 'unknown'))]
    rng = random.Random(20260924)
    cases = [dict(config_serialize=[]), dict(config_serialize=[dict(key=pair['key'], value=pair['value']) for pair in defaults])]
    values = [[], [0], [9], [10, 13], [0xd800], [0xdc00], [0xd800, 0xd800, 0xdc00], [0xd800, 0xdc00], [0x23], [0x3000]]
    for _ in range(2000):
        pairs = []
        for _ in range(rng.randrange(60)):
            value = rng.choice(values) if rng.randrange(2) else [rng.randrange(65536) for _ in range(rng.randrange(20))]
            pairs.append(dict(key=rng.choice(keys), value=value))
        cases.append(dict(config_serialize=pairs))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    expected = run(reference, payload)
    actual = run([args.native, '--lexicon-text-stdio'], payload)
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (index, cases[index])
    print(json.dumps(dict(cases=len(cases), config_byte_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
