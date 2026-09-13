"""Read-only Windows runtime path/schema selection differential."""
import argparse
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
    rng = random.Random(20260922)
    with tempfile.TemporaryDirectory(prefix='tiger-runtime-paths-') as directory:
        base = Path(directory)
        names = ['虎整句', '快符', 'ABC', 'zebra', 'é', 'Éclair', 'ı', 'İ', 'ſ', 'ß', 'Σ', 'ς', '𐐀', '𐐨x', '\ue000', '😀']
        cases = []
        def add(root, current='', exe=None):
            cases.append(dict(runtime_paths=dict(base=str(exe or base), root=str(root), current=current)))
        for index in range(60):
            root = base / f'root-{index}'
            root.mkdir()
            chosen = rng.sample(names, rng.randrange(len(names)+1))
            for name in chosen:
                (root / name).mkdir()
            (root / 'not-a-schema.txt').write_bytes(b'ignored')
            for current in ['', 'missing', *chosen[:4], *[name.lower() for name in chosen[:4]]]:
                add(root, current)
                add(root.name + '\\.', current)
        for configured in ['', ' ', '.', '..', 'missing', '/', '\\', 'C:', 'C:.', 'C:..', 'C:\\',
                           str(base) + '\\root-0\\..\\root-1\\', str(base).replace('\\', '/') + '/root-2///',
                           '\\\\?\\' + str(base) + '\\root-3']:
            add(configured)
        # Ensure fallback itself exercises supplementary-vs-BMP ordering; an
        # earlier ASCII directory would mask an incorrect UTF-16 comparison.
        for index, pair in enumerate([['\ue000', '𐐀'], ['\ufffd', '😀'], ['𐐨x', '𐐀']]):
            special = base / f'unicode-order-{index}'
            special.mkdir()
            for name in pair:
                (special / name).mkdir()
            add(special)
        (base / 'file-root').write_bytes(b'not a directory')
        add(base / 'file-root')
        payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(cases)
        for index, (left, right) in enumerate(zip(expected, actual)):
            assert json.loads(left) == json.loads(right), (index, cases[index], left, right)
        print(json.dumps(dict(cases=len(cases), runtime_path_selection_parity=True)))


if __name__ == '__main__':
    main()
