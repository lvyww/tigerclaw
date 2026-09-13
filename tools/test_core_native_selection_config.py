"""Selection binding parser compared with actual C# parser, including partial errors."""
import argparse
import hashlib
import json
import random
import subprocess

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    rng = random.Random(20260927)
    def units(text):
        data = text.encode('utf-16-le', errors='surrogatepass')
        return [int.from_bytes(data[i:i+2], 'little') for i in range(0, len(data), 2)]
    labels = ['1选', '2选', '10选', '0选', '11选', '+1选', '01选', '-1选', '١选', '1\0选', '1', 'bad']
    tokens = ['VK_A', 'vk_a', 'VK_0', 'VK_F24', 'VK_F01', 'VK_SHIFT', 'VK_RSHIFT', 'VK_LWIN', 'VK_RWIN', 'VK_OEM_7', 'VK_OEM_PLUS', 'VK_NUMPAD0', '0', '-1', '+160', '0xA0', '0Xffffffff', '0x80000000', '0x100000000', '2147483647', '-2147483648', '2147483648', '-2147483649', '0x', '0x+1', '0x\v1', '1\0', '0xF\0', '#comment', '\ud800', '\u3000']
    cases = [dict(selection_lines=[])]
    # Random malformed lines often fail at the label before reaching a key.
    # Exercise every key independently under valid labels, and after a valid
    # override so failures must preserve partial results without publishing the
    # failing line. Also cover every generated name, not just a random subset.
    names = ([f'VK_{c}' for c in '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ']
             + [f'VK_F{i}' for i in range(1, 25)]
             + ['VK_' + name for name in (
                 'SHIFT', 'LSHIFT', 'RSHIFT', 'CONTROL', 'LCONTROL', 'RCONTROL',
                 'MENU', 'LMENU', 'RMENU', 'LWIN', 'RWIN', 'CAPITAL', 'SPACE',
                 'BACK', 'RETURN', 'TAB', 'ESCAPE', 'OEM_1', 'OEM_2', 'OEM_4',
                 'OEM_7', 'OEM_COMMA', 'OEM_PERIOD')])
    for token in tokens + names + [name.lower() for name in names]:
        for rank in range(1, 11):
            lines = ['1选 VK_A', '2选', f'{rank}选 {token}', '10选 VK_Z']
            cases.append(dict(selection_lines=list(map(units, lines))))
    for label in labels:
        cases.append(dict(selection_lines=list(map(units, ['1选 VK_A', label + ' VK_B']))))
    for _ in range(10000):
        lines = []
        for _ in range(rng.randrange(1, 10)):
            line = rng.choice(labels) + rng.choice(['', ' ', '\t']) + ' '.join(rng.choice(tokens) for _ in range(rng.randrange(5)))
            lines.append(rng.choice(['', ' ', '\u3000', '#']) + line + rng.choice(['', ' ', '\u3000', '\r\n']))
        cases.append(dict(selection_lines=list(map(units, lines))))
    format_rng = random.Random(20260928)
    cases.append(dict(selection_format=[]))
    for _ in range(1000):
        bindings = []
        for rank in range(-1, 13):
            if format_rng.randrange(3) == 0:
                continue
            keys = [format_rng.choice([0, 1, 255, 256, -1, -2147483648, 2147483647,
                                       format_rng.randint(-2147483648, 2147483647)])
                    for _ in range(format_rng.randrange(12))]
            bindings.append(dict(number=rank, keys=keys + keys[:2]))
        cases.append(dict(selection_format=bindings))
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (index, cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), selection_config_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))

if __name__ == '__main__':
    main()
