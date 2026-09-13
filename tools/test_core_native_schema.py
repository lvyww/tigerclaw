"""End-to-end schema main-table/construct-map comparison with BuildLexiconSnapshot."""
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
    rng = random.Random(20260919)
    digest = hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix='tiger-schema-') as directory:
        cases = [dict(schema=''), dict(schema=str(Path(directory)/'missing'))]
        for i in range(80):
            name = '虎整句' if i == 0 else '方案'+str(i)
            root = Path(directory) / name
            root.mkdir()
            primary = ['甲\tabcd', '乙\tefgh', '丙\tijkl', '丁\tmnop', '可\tz', '可以\tz', '甲乙\t10', '丁A',
                       '符\t;', '另符\t;a', '斜\t/', '框\t[', '首\ta', '末\txabc', 'word=>甲乙\tabcde']
            if i % 3 == 0: primary.append('禁z符\txz')
            primary += [rng.choice(['甲', '乙', '丙', '丁', '甲乙', 'é', '𠀀', 'display=>甲'])+'\t'+rng.choice(['ab', 'cd', 'zabc', 'oabc', 'abc', ';x'])+'\t'+str(rng.randrange(20)) for _ in range(40)]
            files = {name+'.txt': '\r\n'.join(primary), '快符.txt': '{重复上屏}\tz\n甲乙丙\t25',
                     'extra.dict.yaml': 'name: extra\n...\n甲\tzzzz\t99\n乙丙\t5',
                     '补充语料.txt': '不应入码表\tqq\t999',
                     '用户调整.txt': '{置顶}z\t可以\n{添加}ab\t新字\n{删除}abcd\t甲\n{前移}ab\t甲\n{删除}[\t框',
                     'first.注释': '甲\t注释一\n甲 注释二\n甲乙 hello\\sworld ignored\nA Upper\na lower\n\\s space\n#skip',
                     'second.注释': '甲\t注释三\n𠀀 扩展\n坏行\n乙 #literal',
                     'first.拆分': '甲\t拆分一\n甲\t拆分二\n乙 e\\tpart',
                     'second.拆分': '甲\t拆分三\n甲乙 #literal\n𠀀 构件'}
            if i % 2 == 0:
                files['构词.txt'] = '甲\tyyyy\n甲\txxxx\t999\n乙\tzzzz\n丙\ta\n甲乙\t9'
            for filename, content in files.items():
                encoding = rng.choice(['utf-8', 'utf-16'])
                data = content.encode(encoding)
                (root/filename).write_bytes(data)
                digest.update(data)
            (root/'nested').mkdir()
            (root/'nested'/'ignored.txt').write_bytes('误读\tq'.encode())
            for locale in ['zh-CN', 'en-US']:
                cases.append(dict(schema=str(root), locale=locale))
        payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
        def run(command):
            return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
        actual = run([args.native, '--lexicon-text-stdio'])
        assert len(expected) == len(actual) == len(cases)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Schema main-table/map mismatch case {i}: {cases[i]}')
    print(json.dumps(dict(cases=len(cases), schema_table_parity=True, content_sha256=digest.hexdigest())))


if __name__ == '__main__':
    main()
