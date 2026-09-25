"""Compare real source-model accelerators with an independent raw-record oracle."""
import argparse
import json
from pathlib import Path
import random
import subprocess
from audit_mixture_budget import Model


def main():
    p = argparse.ArgumentParser()
    p.add_argument('executable', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('sources', nargs='+', type=Path)
    a = p.parse_args()
    results = {}
    for source in a.sources:
        m = Model(source)
        rng = random.Random(20260925)
        samples = []
        for n in range(1, 6):
            samples += [m.record(n, i)[0] for i in sorted({0, m.counts[n]-1, *[rng.randrange(m.counts[n]) for _ in range(128)]})]
            samples += [tuple(rng.randrange(len(m.vocab)) for _ in range(n)) for _ in range(64)]
        samples += [m.record(4, rng.randrange(m.counts[4]))[0] + (rng.randrange(len(m.vocab)),) for _ in range(128)]
        requests = ''.join('\t'.join(m.vocab[i] for i in key)+'\n' for key in samples)
        result = subprocess.run([str(a.executable), 'score', str(source)], input=requests,
                                text=True, capture_output=True, check=True)
        actual = list(map(float, result.stdout.splitlines()))
        assert len(actual) == len(samples)
        error = max(abs(value-m.extended(key)) for key, value in zip(samples, actual))
        assert error < 1e-10, (source, error)
        masses = {}
        for text in ('', '游戏', '你好', '𠀀'):
            history = tuple(m.ids.get(c, m.unk) for c in text)
            mass = sum(10**m.extended(history+(i,)) for i, word in enumerate(m.vocab) if word != '<s>')
            assert abs(mass-1) < 3e-5, (source, text, mass)
            masses[text] = mass-1
        results[str(source)] = dict(samples=len(samples), max_abs_log10_error=error,
                                    extended_conditional_mass_errors=masses)
        for mapping in m.data.values():
            if hasattr(mapping, 'close'): mapping.close()
        for file in m.files: file.close()
        print(source, results[str(source)], flush=True)
    a.output.write_text(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
