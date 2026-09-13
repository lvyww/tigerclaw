"""Actual-directory order parity with C# across explicit cultures."""
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
    rng = random.Random(20260916)
    cultures = ['', 'zh-CN', 'en-US', 'sv-SE', 'tr-TR', 'ja-JP', 'de-DE', 'zh-TW', 'fr-FR']
    with tempfile.TemporaryDirectory(prefix='tiger-order-') as directory:
        requests = []
        digest = hashlib.sha256()
        for number in range(20):
            schema = '虎整句' if number == 0 else '方案' + str(number)
            root = Path(directory) / schema
            root.mkdir()
            names = [schema+'.txt', schema+'.dict.yaml', '虎整句.txt', '快符.txt', 'a-1.txt', 'a1.txt',
                     'a_1.txt', 'a 1.txt', 'A2.TXT', 'a10.txt', 'é.txt', 'é.txt', 'ä.txt', 'z.txt',
                     'İ.txt', 'ı.txt', 'あ.txt', 'ア.txt', '😀.txt', '１.txt', '1.txt', 'ignored.yaml', 'ignored.txt.bak']
            names += [''.join(rng.choices('甲乙丙abcáäøσςーＡ', k=8))+rng.choice(['.txt', '.dict.yaml']) for _ in range(50)]
            names = list(dict.fromkeys(names))
            rng.shuffle(names)
            for name in names:
                (root / name).touch()
                digest.update(name.encode('utf-8'))
            (root / 'nested.txt').mkdir()
            (root / 'nested.txt' / 'not-top-level.txt').touch()
            for culture in cultures:
                requests.append(dict(directory=str(root), locale=culture))
            requests.append(dict(directory=str(root)))
            requests.append(dict(directory=str(root) + '/.', locale='zh-CN'))
            requests.append(dict(directory=str(root) + '/', locale='zh-CN'))
        payload = ''.join(json.dumps(item) + '\n' for item in requests).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(requests)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Order mismatch {requests[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(requests), order_parity=True, names_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
