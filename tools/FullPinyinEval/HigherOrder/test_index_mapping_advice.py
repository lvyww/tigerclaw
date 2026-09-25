"""Real-model score parity and kernel mapping evidence for index I/O hints."""
import argparse
import json
import mmap
from pathlib import Path
import random
import struct
import subprocess
import time


def mappings(pid, source):
    result = []
    for line in Path(f'/proc/{pid}/smaps').read_text().splitlines():
        if '-' in line.split(' ', 1)[0]:
            current = None
            if str(source) in line and line.endswith(('.klm',)):
                current = {'mapping': line}
                result.append(current)
        elif current is not None and line.startswith(('FilePmdMapped:', 'VmFlags:')):
            key, value = line.split(':', 1)
            current[key] = value.strip()
    return result


def main():
    p = argparse.ArgumentParser()
    p.add_argument('old', type=Path); p.add_argument('new', type=Path)
    p.add_argument('output', type=Path); p.add_argument('sources', nargs='+', type=Path)
    a = p.parse_args(); a.old.chmod(0o755); results = {}
    for source in a.sources:
        vocab = (source/'vocab.txt').read_text().splitlines()
        rng = random.Random(20260925); samples = []; contexts = []
        for n in range(1, 6):
            with (source/f'{n}.bin').open('rb') as file:
                data = mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ)
                count = len(data)//24
                for i in sorted({0, count-1, *[rng.randrange(count) for _ in range(128)]}):
                    key = struct.unpack_from('<'+'H'*n, data, i*24)
                    samples.append(key)
                    if n == 4: contexts.append(key)
                data.close()
            samples += [tuple(rng.randrange(len(vocab)) for _ in range(n)) for _ in range(64)]
        samples += [key+(rng.randrange(len(vocab)),) for key in contexts]
        requests = ''.join('\t'.join(vocab[i] for i in key)+'\n' for key in samples)
        baseline = subprocess.run([str(a.old), 'score', str(source)], input=requests,
                                  capture_output=True, text=True, check=True).stdout
        with subprocess.Popen([str(a.new), 'score', str(source)], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as child:
            deadline = time.monotonic()+120
            while True:
                if child.poll() is not None: raise RuntimeError(child.stderr.read())
                evidence = mappings(child.pid, source)
                if len(evidence) >= 2 and all('nh' in x.get('VmFlags', '').split() and
                                              'rr' in x.get('VmFlags', '').split() and
                                              x.get('FilePmdMapped') == '0 kB' for x in evidence): break
                if time.monotonic() >= deadline: raise RuntimeError(('mapping hints not applied', evidence))
                time.sleep(.1)
            output, errors = child.communicate(requests)
            assert child.returncode == 0, errors
        assert output == baseline, ('score output changed', source)
        results[str(source)] = dict(samples=len(samples),score_text_identical=True,
                                   no_file_huge_pages=True,random_read_hint=True,mappings=evidence)
        print(source, len(samples), 'scores identical; no huge pages; random read', flush=True)
    a.output.write_text(json.dumps(results, indent=2))


if __name__ == '__main__': main()
