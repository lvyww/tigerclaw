"""Independent probability checks for the external weighted-difference pruner."""
import itertools
import math
from pathlib import Path
import subprocess
import sys
import tempfile

def read(path):
    data = {}
    order = 0
    headers = {}
    counts = {}
    ended = False
    for line in path.read_text().splitlines():
        if line.startswith('ngram '):
            n, size = line[6:].split('=')
            headers[int(n)] = int(size)
        if line.startswith('\\'):
            order = int(line[1:].split('-')[0]) if '-grams:' in line else 0
            ended |= line == '\\end\\'
            continue
        if not order or not line.strip():
            continue
        f = line.split()
        key = tuple(f[1:order+1])
        assert len(key) == order and key not in data
        data[key] = (float(f[0]), float(f[order+1]) if len(f) > order+1 else 0.)
        counts[order] = counts.get(order, 0) + 1
    assert ended and all(counts.get(n, 0) == size for n, size in headers.items())
    assert all(math.isfinite(x) for v in data.values() for x in v)
    return data

def prob(data, history, token):
    if history + (token,) in data:
        return 10 ** data[history + (token,)][0]
    if not history:
        return 10 ** data[('<unk>',)][0]
    return 10 ** data.get(history, (0, 0))[1] * prob(data, history[1:], token)

def test(executable):
    with tempfile.TemporaryDirectory(prefix='pruner-test-', dir='/home/yc/tmp/tiger500') as temp:
        root = Path(temp)
        vocab = ('<unk>', '<s>', '</s>', 'a', 'b')
        data = {(t,): (math.log10(p), 0.) for t, p in zip(vocab, (.001, .001, .098, .5, .4))}
        # Sparse conditional distributions with nonzero backoffs and distinct high orders.
        for n in range(2, 6):
            for history in itertools.product(('a', 'b'), repeat=n-1):
                p = .75 if history[0] == 'a' else .25
                lower = prob(data, history[1:], 'a')
                assert history in data
                data[history] = (data[history][0], math.log10((1-p)/(1-lower)))
                data[history + ('a',)] = (math.log10(p), 0.)
                # Keep all prefixes needed for the next order, with unchanged lower probability.
                data[history + ('b',)] = (math.log10(prob(data, history, 'b')), 0.)
        text = '\\data\\\n'
        for n in range(1, 6):
            text += f'ngram {n}={sum(len(k)==n for k in data)}\n'
        for n in range(1, 6):
            text += f'\n\\{n}-grams:\n'
            for k, (p, b) in sorted(data.items()):
                if len(k) == n:
                    text += f'{p:.12g}\t' + ' '.join(k) + (f'\t{b:.12g}' if n < 5 else '') + '\n'
        source = root / 'input.arpa'
        source.write_text(text + '\n\\end\\\n')
        original = read(source)
        checks = 0
        for name, threshold in [('identity', '-1'), ('pruned', '0.002')]:
            out = root / (name + '.arpa')
            subprocess.run([executable, '-abs=yes', '-t=' + threshold, str(source), str(out)], check=True)
            actual = read(out)
            if name == 'pruned':
                assert len(actual) < len(original)
            for n in range(5):
                for h in itertools.product(('a', 'b'), repeat=n):
                    assert abs(sum(prob(original, h, t) for t in vocab)-1) < 1e-8
                    assert abs(sum(prob(actual, h, t) for t in vocab)-1) < 5e-5
                    if name == 'identity':
                        for t in vocab:
                            assert abs(prob(actual, h, t)-prob(original, h, t)) < 5e-5
                    checks += 1
        print('PASS:', checks, 'context normalization checks; no-prune probability identity; real pruning; ARPA counts/finite scores')

if __name__ == '__main__':
    test(sys.argv[1])
