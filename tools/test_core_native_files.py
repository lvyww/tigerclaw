"""Offline byte-decoder and actual-file differential tests for parallel Core."""
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
    rng = random.Random(20260911)
    blobs = [b'', b'\xef', b'\xef\xbb', b'\xff', b'\xfe']
    blobs += [bytes([i >> 8, i & 255]) for i in range(65536)]
    prefixes = [b'', b'\xef\xbb\xbf', b'\xff\xfe', b'\xfe\xff', b'\xff\xfe\0\0', b'\0\0\xfe\xff']
    for prefix in prefixes:
        for _ in range(3000):
            blobs.append(prefix + rng.randbytes(rng.randrange(0, 80)))
        for encoding in ['utf-8', 'utf-16-le', 'utf-16-be', 'utf-32-le', 'utf-32-be']:
            blobs.append(prefix + '字\tab\t10\r\n𠀀\tz\n'.encode(encoding))
        for size in [1023, 1024, 1025, 4095, 4096, 4097]:
            blobs.append(prefix + b'a' * size + b'\xf0\xa0\x80\x80\r\n')
    requests = [dict(bytes=list(blob)) for blob in blobs]
    def run(command, payload):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=180).stdout.splitlines()
    def compare(requests):
        payload = ''.join(json.dumps(item) + '\n' for item in requests).encode()
        expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'], payload)
        actual = run([args.native, '--lexicon-text-stdio'], payload)
        assert len(expected) == len(actual) == len(requests)
        for i, (left, right) in enumerate(zip(expected, actual)):
            if json.loads(left) != json.loads(right):
                raise AssertionError(f'Case {i}: {requests[i]}: C#={left!r}, C++={right!r}')
        return hashlib.sha256(payload).hexdigest()
    byte_hash = compare(requests)
    # Temporary test-owned files only. Tests never write production tables/config.
    with tempfile.TemporaryDirectory(prefix='tiger-native-files-') as directory:
        requests = []
        payload_hash = hashlib.sha256()
        content = 'name: demo\n...\r\nAB 字 词 99\r字\tab\t10\n{重复上屏}\tz\n甲=>乙\ty\r\n'
        for prefix in prefixes:
            for encoding in ['utf-8', 'utf-16-le', 'utf-16-be', 'utf-32-le', 'utf-32-be']:
                for suffix in ['.txt', '.DICT.YAML']:
                    path = Path(directory) / ('码表' + str(len(requests)) + suffix)
                    data = prefix + content.encode(encoding)
                    path.write_bytes(data)
                    payload_hash.update(data)
                    requests.append(dict(file=str(path)))
        compare(requests)
        count = len(requests)
    print(json.dumps(dict(byte_cases=len(blobs), file_cases=count, file_parity=True,
                          byte_trace_sha256=byte_hash, file_content_sha256=payload_hash.hexdigest())))


if __name__ == '__main__':
    main()
