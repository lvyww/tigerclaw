"""Actual C# decimal-output eligibility helpers, including Unicode cluster boundaries."""
import argparse
import hashlib
import json
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    cases = [dict(output_digit=[unit, 0x39], vk=0x39, modifiers=0) for unit in range(65536)]
    cases += [dict(output_digit=[unit], vk=0, modifiers=0) for unit in range(65536)]
    cases += [dict(output_digit=[], vk=vk, modifiers=mask) for vk in range(256) for mask in range(16)]
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (index, cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), decimal_eligibility_parity=True, trace_sha256=hashlib.sha256(payload).hexdigest())))


if __name__ == '__main__':
    main()
