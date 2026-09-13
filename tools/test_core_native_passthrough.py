"""Exhaustive byte-sized VK/Shift/CapsLock passthrough-text differential."""
import argparse
import json
import subprocess

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    cases = [dict(guess_vk=vk, shift=shift, caps=caps) for vk in range(-1, 257) for shift in [False, True] for caps in [False, True]]
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), passthrough_guess_parity=True)))

if __name__ == '__main__':
    main()
