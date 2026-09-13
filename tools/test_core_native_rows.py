"""Compare native streaming lexicon rows with the actual C# ParseMbLines."""
import argparse
import hashlib
import json
import random
import subprocess


def units(value):
    raw = value.encode('utf-16-le', errors='surrogatepass')
    return [int.from_bytes(raw[i:i+2], 'little') for i in range(0, len(raw), 2)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    rng = random.Random(20260910)
    cases = []
    def add(lines, yaml=False, positive='+', negative='-'):
        cases.append(dict(lines=list(map(units, lines)), yaml=yaml,
                          positive=units(positive), negative=units(negative)))
    add(['name: test', '... # not a delimiter', '字\tab', '...', '字\tab', 'AB 甲 乙 99'], True)
    add(['{添加} 字\tab', '{删除}x', '{置顶}x', '{前移}x', '{重复上屏}\tz'])
    numeric = ['0', '-0', '+1', '-2147483648', '2147483647', '2147483648', '-2147483649',
               '0000000000000000000009', '12\x00', '12\x00 ', ' 12 ', '12\u00a0', '\u00a012',
               '１２', '١٢', '1,000', '1e3', '--2', '−3', '+', '', '12\x00\t']
    for number in numeric:
        for sep in [' ', '\t']:
            for lines in [['字'+sep+'ab'+sep+number], ['字'+sep+number+sep+'ab'],
                          ['AB'+sep+'字'+sep+number], ['字'+sep+number]]:
                add(lines)
    # Numeric parsing and trimming interact differently for every UTF-16 unit.
    for c in range(65536):
        add(['字\tab\t' + chr(c) + '12' + chr(c)])
    pieces = ['字', '甲=>乙', r'\s', r'\t', 'AB', 'a;', '123', '#comment', r'\#',
              '𠀀', '\ud800', '\x00', '\u3000', '2147483648', '-2147483648'] + numeric
    for _ in range(10000):
        lines = [rng.choice([' ', '\t', '  ', '\t ']).join(rng.choices(pieces, k=rng.randrange(1, 7)))
                 for _ in range(rng.randrange(1, 6))]
        yaml = rng.choice([True, False])
        if yaml:
            lines.insert(rng.randrange(len(lines)+1), '...')
        add(lines)
    signs = [('+', c) for c in '\u2012\u207b\u208b\u2212\u2796\ufe63\uff0d']
    for positive, negative in signs + [('plus', 'minus'), ('', '-'), ('++', '--')]:
        add(['字\tab\t'+positive+'9', '字\t'+negative+'5\tab', 'ab 字 '+negative+'9',
             '字\tab\t-7', '字\tab\t+7'],
            positive=positive, negative=negative)
    payload = ''.join(json.dumps(case) + '\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for i, (left, right) in enumerate(zip(expected, actual)):
        if json.loads(left) != json.loads(right):
            raise AssertionError(f'Case {i}: {cases[i]}: C#={left!r}, C++={right!r}')
    print(json.dumps(dict(cases=len(cases), row_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
