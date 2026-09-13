"""Real custom/adjustment file loading compared with C# private loaders."""
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
    source = [dict(code=units('z'), candidates=list(map(units, ['first', 'old\x1eb', 'other\x1eb', 'last'])))]
    rng = random.Random(20260915)
    digest = hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix='tiger-edit-files-') as directory:
        root = Path(directory)
        cases = [dict(entries=source, edit_files=[dict(path=str(root / 'missing.txt'), custom=custom)]) for custom in [False, True]]
        cases += [dict(entries=source, edit_files=[dict(path=str(root), custom=custom)]) for custom in [False, True]]
        sequence = []
        for i in range(120):
            custom = bool(i % 2)
            lines = []
            for _ in range(30):
                action = '' if custom else rng.choice(['{添加}', '{删除}', '{置顶}', '{前移}'])
                lines.append(action + rng.choice(['Z', 'z', 'ab', '', ' x ', '#comment']) +
                             rng.choice(['\t', '\t\t', ' ']) +
                             rng.choice(['new=>b', 'b', 'first', 'last', '𠀀', r'\s', '#literal', '字', '']))
            # Explicit replacement removes every old display for the same commit.
            lines.insert(0, 'z\tnew=>b' if custom else '{置顶}z\tb')
            content = rng.choice(['\r', '\n', '\r\n']).join(lines)
            encoding, prefix = rng.choice([('utf-8', b''), ('utf-8', b'\xef\xbb\xbf'),
                                           ('utf-16-le', b'\xff\xfe'), ('utf-16-be', b'\xfe\xff')])
            data = prefix + content.encode(encoding)
            digest.update(data)
            path = root / ('调整' + str(i) + '.txt')
            path.write_bytes(data)
            item = dict(path=str(path), custom=custom)
            sequence.append(item)
            cases.append(dict(entries=source, edit_files=[item]))
            cases.append(dict(entries=source, edit_files=sequence.copy()))
        payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(cases)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Edit file case {i} mismatch')
    print(json.dumps(dict(cases=len(cases), edit_file_parity=True, content_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
