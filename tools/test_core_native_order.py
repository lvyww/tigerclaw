"""Actual-directory order parity with C# across explicit cultures."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    parser.add_argument("--globalization", choices=["nls", "icu"], default="nls")
    args = parser.parse_args()
    oracle_env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_USENLS="true" if args.globalization == "nls" else "false")
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
        for request in requests:
            request['nls_sort_keys'] = args.globalization == 'nls'
        payload = ''.join(json.dumps(item) + '\n' for item in requests).encode()
        def run(command, data=payload):
            return subprocess.run(command, input=data, capture_output=True, check=True, env=oracle_env, timeout=180).stdout.splitlines()
        mode = json.loads(run([args.dotnet, args.assembly, '--native-core-text-stdio'], b'{"globalization_probe":true}\n')[-1])
        assert mode['useNls'] == (args.globalization == 'nls'), mode
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        if expected and expected[0] == b'Core startup context tests passed':
            expected = expected[1:]  # Known managed preflight; all remaining lines must be JSON.
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(requests)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Order mismatch {requests[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(requests), order_parity=True, comparator='NLS sort keys' if args.globalization == 'nls' else 'CurrentCulture', globalization=args.globalization, names_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
