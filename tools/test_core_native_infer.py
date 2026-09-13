"""Construct-code lookup and uncoded-row inference against actual C# helpers."""
import argparse
import hashlib
import json
from pathlib import Path
import random
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    def units(text):
        raw = text.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]
    def entry(code, texts):
        return dict(code=units(code), candidates=list(map(units, texts)))
    cases = []
    for unit in range(65536):
        cases.append(dict(entries=[entry('oz', ['字']), entry(chr(unit)+'q', ['字'])],
                          construct_file='', uncoded=[dict(text=units('字A'), freq=7)]))
    rng = random.Random(20260918)
    names = ['甲', '乙', '丙', '丁', 'e\u0301', '𠀀', '👩\u200d💻', 'display\x1e甲', 'AB', '', '\ud800']
    digest = hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix='tiger-infer-') as directory:
        paths = ['']
        for i in range(20):
            path = Path(directory) / ('构词'+str(i)+'.txt')
            lines = ['甲\tx', '甲\tabcd\t999', '乙\toooo', 'new=>丙\tzz', '丁\tffff', '无编码词']
            rng.shuffle(lines)
            data = '\r\n'.join(lines).encode('utf-8')
            path.write_bytes(data); paths.append(str(path)); digest.update(data)
        for _ in range(1000):
            codes = rng.sample(['ab', 'cd', 'zabc', 'oabc', 'a', '1a', ';a', 'áq', 'Ωq', '中q'], rng.randrange(11))
            entries = [entry(code, rng.choices(names, k=rng.randrange(6))) for code in codes]
            uncoded = [dict(text=units(''.join(rng.choices(names + ['A', '?', ' '], k=rng.randrange(8)))),
                            freq=rng.choice([0, 9, -1, 2147483647])) for _ in range(10)]
            cases.append(dict(entries=entries, construct_file=rng.choice(paths), uncoded=uncoded))
        payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(cases)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Inference case {i}: {cases[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(cases), inference_parity=True, fixture_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
