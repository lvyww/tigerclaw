"""Differential test of the C# and C++ replay caches; no live Core or input."""
import argparse
import hashlib
import json
import random
import subprocess


def units(text):
    data = text.encode('utf-16-le', errors='surrogatepass')
    return [data[i] | data[i + 1] << 8 for i in range(0, len(data), 2)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    operations = [dict(op='reset', capacity=1)]
    # Exercise actual System.Char.IsWhiteSpace behavior, including lone surrogates.
    operations += [dict(op='build', session=[unit], event=[49]) for unit in range(65536)]
    for session in [None, [], units(' \t\u3000'), units(' session '), units('\u200b'), units('a\nb')]:
        for event in [None, [], units(' \n'), units('42'), units('b\nc')]:
            operations.append(dict(op='build', session=session, event=event))
    rng = random.Random(719341)
    for capacity in [-1, 1, 16, 33, 512]:
        operations.append(dict(op='reset', capacity=capacity))
        for i in range(2400):
            op = rng.choice(['store', 'get', 'execute', 'get'])
            seq = rng.choice([-2147483648, 2147483647, i])
            operations.append(dict(op=op, session=units(rng.choice(['s', ' s ', '\u3000', '\U00020000'])),
                                   event=units(str(rng.randrange(70))), seq=seq,
                                   response=rng.choice(['', '{"seq":1}', '{"seq":1,"commit_text":"旋"}',
                                                        '{"type":"response"}', '{"seq":0', '{"seq": 77, "handled":true}'])))
        # Fill past capacity, read/overwrite oldest, verify it still expires FIFO.
        operations.append(dict(op='reset', capacity=capacity))
        size = max(16, capacity)
        for i in range(size):
            operations.append(dict(op='store', session=[115], event=units(str(i)), response='{"seq":1}'))
        operations += [dict(op='get', session=[115], event=[48], seq=91),
                       dict(op='store', session=[115], event=[48], response='{"seq":2}'),
                       dict(op='store', session=[115], event=units(str(size)), response='{"seq":3}'),
                       dict(op='get', session=[115], event=[48], seq=92)]
    payload = ''.join(json.dumps(op, ensure_ascii=True, separators=(',', ':')) + '\n' for op in operations)

    def run(command):
        completed = subprocess.run(command, input=payload, text=True, encoding='utf-8',
                                   capture_output=True, timeout=120, check=True)
        results = [json.loads(line) for line in completed.stdout.splitlines()]
        if len(results) != len(operations):
            raise AssertionError(f'Wrong response count: {len(results)} vs {len(operations)}')
        return results

    reference = run([args.dotnet, args.assembly, '--native-core-replay-stdio'])
    native = run([args.native, '--replay-stdio'])
    for index, (expected, actual) in enumerate(zip(reference, native)):
        if expected != actual:
            raise AssertionError(f'Replay mismatch at operation {index}: {operations[index]!r}; C#={expected!r}, C++={actual!r}')
    print(json.dumps(dict(operations=len(operations), all_utf16_units_checked=True, replay_parity=True,
                          trace_sha256=hashlib.sha256(payload.encode()).hexdigest())))


if __name__ == '__main__':
    main()
