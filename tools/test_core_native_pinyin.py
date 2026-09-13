"""Compare independent pinyin-table loading against actual C# loader."""
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
    rng = random.Random(20260920)
    digest = hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix='tiger-pinyin-') as directory:
        cases = [dict(pinyin_base=str(Path(directory)/'missing'))]
        for i in range(100):
            base = Path(directory)/str(i)
            root = base/'拼音反查码表'
            root.mkdir(parents=True)
            files = {}
            for name in ['拼音反查码表.txt', 'a.txt', 'z.TXT']:
                lines = ['可\tke', '刻\tKE', '可以\tkeyi', '无编码词\t99', 'old=>可\tke\t10']
                lines += [rng.choice(['可', '刻', '可以', '𠀀', 'é', '绿', 'new=>可'])+'\t'+rng.choice(['ke', 'keyi', 'lv', 'lü', 'a', 'z'])+'\t'+str(rng.randrange(20)) for _ in range(30)]
                rng.shuffle(lines)
                files[name] = '\r\n'.join(lines)
            names = list(files); rng.shuffle(names)
            for name in names:
                data = files[name].encode(rng.choice(['utf-8', 'utf-16']))
                (root/name).write_bytes(data); digest.update(data)
            (root/'ignored.dict.yaml').write_bytes(b'...\nignored\tx')
            (root/'nested.txt').mkdir()
            (root/'nested.txt'/'child.txt').write_bytes(b'ignored\tx')
            cases.append(dict(pinyin_base=str(base)))
        payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(cases)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Pinyin image mismatch {i}: {cases[i]}')
    print(json.dumps(dict(cases=len(cases), pinyin_parity=True, content_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
