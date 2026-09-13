"""Stateful page-tracker traces against actual private C# input-engine methods."""
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
    rng = random.Random(20260925)
    cases = []
    for _ in range(100):
        steps = []
        code, mode, size, total = [97, 98], 2, 5, 107
        for _ in range(500):
            if rng.randrange(10) == 0: code = rng.choice([[], [97], [65], [97, 32], [0xd800], [0xd800, 0xdc00]])
            if rng.randrange(10) == 0: mode = rng.randrange(6)
            if rng.randrange(8) == 0: size = rng.randrange(-2, 15)
            if rng.randrange(8) == 0: total = rng.choice([0, 1, 5, 10, 11, 100, 107])
            steps.append(dict(op=rng.choice(['get', 'move', 'move', 'key', 'reset']), code=code, mode=mode, size=size, total=total, delta=rng.choice([-100, -1, 0, 1, 100]),
                              keys=rng.choice(['', 'unknown', '[ ]', 'Shift Tab/Tab', 'PageUp/PageDown', ' [ ] ', 'shift tab/tab', '- =']),
                              vk=rng.choice([9, 0x21, 0x22, 0xbb, 0xbd, 0xdb, 0xdd, 0x41]), shift=rng.choice([True, False])))
        cases.append(dict(page_trace=steps))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (index, left, right)
    print(json.dumps(dict(operations=50000, actual_engine_page_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
