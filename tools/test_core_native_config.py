"""Default/merge/path/bool configuration differential, with no config writes."""
import argparse
import hashlib
import json
import random
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    reference = [args.dotnet, args.assembly, '--native-core-text-stdio']
    def run(command, payload):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    defaults = json.loads(run(reference, b'{"config_lines":[]}\n')[0])
    def text(units):
        return b''.join(unit.to_bytes(2, 'little') for unit in units).decode('utf-16-le', errors='surrogatepass')
    keys = [text(pair['key']) for pair in defaults]
    def units(value):
        raw = value.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]
    cases = [dict(config_lines=[])]
    paths = ['', ' ', '/', '\\', '\\\\', 'C:\\', 'C:/', 'C:/foo///', 'relative\\', '\\\\server\\share\\', ' X:\\ ']
    for path in paths:
        cases.append(dict(config_lines=[units('码表存储位置\t'+path)]))
    rng = random.Random(20260921)
    values = ['', ' ', '是', '否', 'TRUE', 'false', 'On', 'OFF', '1', '0', 'yes', '#literal', 'x=y,z', '\ud800', '𠀀', '\u3000', '\0'] + paths
    for _ in range(1000):
        lines = []
        for _ in range(rng.randrange(1, 50)):
            key = rng.choice(keys + ['unknown', '', '#comment'])
            lines.append(rng.choice(['', ' ', '\t', '\u3000']) + key + rng.choice(['\t', ' ', ',', '=', '\u00a0']) +
                         rng.choice(values) + rng.choice(['', ' ', '\r\n', '\u3000']))
        cases.append(dict(config_lines=list(map(units, lines))))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    expected = run(reference, payload)
    actual = run([args.native, '--lexicon-text-stdio'], payload)
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Config case {i} mismatch: {cases[i]}')
    with tempfile.TemporaryDirectory(prefix='tiger-native-config-') as directory:
        file_cases = []
        originals = []
        content_hash = hashlib.sha256()
        encodings = [('utf-8', b''), ('utf-8', b'\xef\xbb\xbf'), ('utf-16-le', b'\xff\xfe'),
                     ('utf-16-be', b'\xfe\xff'), ('utf-32-le', b'\xff\xfe\0\0'), ('utf-32-be', b'\0\0\xfe\xff')]
        for index, case in enumerate(cases[:112]):
            encoding, bom = encodings[index % len(encodings)]
            separator = ['\r\n', '\n', '\r'][index % 3]
            body = separator.join(text(line) for line in case['config_lines'])
            data = bom + body.encode(encoding, errors='surrogatepass')
            # Exercise invalid trailing sequences through actual .NET file decoding.
            if index % 7 == 0:
                data += b'\xff\x80\x00'
            path = Path(directory) / f'config-{index}.txt'
            path.write_bytes(data)
            originals.append(data)
            content_hash.update(data)
            file_cases.append(dict(config_file=str(path)))
        file_payload = ''.join(json.dumps(case)+'\n' for case in file_cases).encode()
        file_expected = run(reference, file_payload)
        file_actual = run([args.native, '--lexicon-text-stdio'], file_payload)
        assert len(file_expected) == len(file_actual) == len(file_cases)
        for index, (left, right) in enumerate(zip(file_expected, file_actual)):
            assert json.loads(left) == json.loads(right), f'Config file {index} mismatch'
        for case, original in zip(file_cases, originals):
            assert Path(case['config_file']).read_bytes() == original
        # Missing files must fail, not return a successful all-default snapshot.
        missing = (json.dumps(dict(config_file=str(Path(directory) / 'missing.txt')))+'\n').encode()
        for command in [reference, [args.native, '--lexicon-text-stdio']]:
            result = subprocess.run(command, input=missing, capture_output=True, timeout=30)
            assert result.returncode != 0 and not result.stdout.strip()
        assert not (Path(directory) / 'missing.txt').exists()
        print(json.dumps(dict(config_file_cases=len(file_cases), content_sha256=content_hash.hexdigest(), missing_file_rejected=True)))
    print(json.dumps(dict(cases=len(cases), defaults=len(keys), config_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
