"""Compare the parallel C++ reader against C#-generated TCLX fixtures. No live IPC."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, required=True)
    args = parser.parse_args()
    executable = args.native.resolve()
    count = 0
    names = json.loads((args.fixtures / 'manifest.json').read_text(encoding='utf-8-sig'))
    for name in names:
        if not isinstance(name, str) or not name.startswith('case-') or not name[5:].isdigit():
            raise ValueError('Invalid fixture manifest name')
        binary = (args.fixtures / (name + '.tclx')).resolve()
        result = subprocess.run([str(executable), '--lexicon-json', str(binary)], check=True,
                                capture_output=True, text=True, encoding='utf-8', timeout=30)
        expected = [json.loads(line) for line in (args.fixtures / (name + '.jsonl')).read_text(encoding='utf-8-sig').splitlines()]
        actual = [json.loads(line) for line in result.stdout.splitlines()]
        if actual != expected:
            raise AssertionError(f'Native/C# compact lexicon mismatch: {name}')
        count += len(expected)
    print(json.dumps(dict(fixtures=len(names), codes=count, exact_utf16_and_candidate_order=True,
                          native_sha256=hashlib.sha256(executable.read_bytes()).hexdigest())))


if __name__ == '__main__':
    main()
