"""Chinese punctuation resolver/table differential, including decimal-arm effects."""
import argparse
import json
import subprocess

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    cases = [dict(punct_vk=vk, shift=bool(mask & 1), english=bool(mask & 2), slash=bool(mask & 4), armed=bool(mask & 8)) for vk in range(256) for mask in range(16)]
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), punctuation_parity=True)))

if __name__ == '__main__':
    main()
