"""Independent exhaustive toy oracles for union, normalization, KL and budgets."""
import argparse
import itertools
import json
import math
from pathlib import Path
import random
import struct
import subprocess
import tempfile

REC = struct.Struct('<5HHfff')


def score(rows, key):
    if key in rows:
        return rows[key][0]
    assert len(key) > 1, key
    return rows.get(key[:-1], (0, 0))[1] + score(rows, key[1:])


def write_arpa(path, rows):
    with path.open('w') as f:
        f.write('\\data\\\n')
        for n in range(1, 6):
            f.write(f'ngram {n}={sum(len(k)==n for k in rows)}\n')
        for n in range(1, 6):
            f.write(f'\n\\{n}-grams:\n')
            for k, (p, b) in sorted(rows.items(), reverse=True):
                if len(k) == n:
                    f.write(f'{p:.10g}\t'+ ' '.join(k) + (f'\t{b:.10g}' if n < 5 else '')+'\n')
        f.write('\n\\end\\\n')


def toy(words, seed):
    rng = random.Random(seed)
    outputs = ['<unk>', '</s>', *words]
    mass = [rng.uniform(.1, 1) for _ in outputs]
    rows = {(w,): (math.log10(p/sum(mass)), 0) for w, p in zip(outputs, mass)}
    rows[('<s>',)] = (0, 0)
    for n in range(2, 6):
        contexts = sorted(k for k in rows if len(k) == n-1 and k[-1] not in ('</s>', '<unk>'))
        for h in contexts:
            distribution = [rng.uniform(.1, 1) for _ in outputs]
            distribution = [p/sum(distribution) for p in distribution]
            selected = rng.sample(range(len(outputs)), 2)
            used = sum(distribution[i] for i in selected)
            lower = sum(10**score(rows, h[1:]+(outputs[i],)) for i in selected)
            rows[h] = (rows[h][0], math.log10((1-used)/(1-lower)))
            for i in selected:
                rows[h+(outputs[i],)] = (math.log10(distribution[i]), 0)
    return rows


def read_model(path):
    vocab = (path/'vocab.txt').read_text().splitlines()
    rows, losses = {}, {}
    for n in range(1, 6):
        raw = (path/f'{n}.bin').read_bytes()
        assert len(raw) % REC.size == 0
        keys = []
        for r in REC.iter_unpack(raw):
            k = tuple(vocab[i] for i in r[:n])
            assert k not in rows
            rows[k] = r[6:8]
            losses[k] = r[8]
            keys.append(k)
        assert keys == sorted(keys)
    return vocab, rows, losses


def check_mass(vocab, rows):
    contexts = {(), ('<s>',), ('<s>', '<unk>')}
    contexts.update(k[:-1] for k in rows if len(k)>1)
    contexts.update(itertools.product(['a', 'b', 'c'], repeat=4))
    maximum = 0
    for h in contexts:
        total = sum(10**score(rows, h+(w,)) for w in vocab if w != '<s>')
        maximum = max(maximum, abs(total-1))
        assert abs(total-1) < 3e-6, (h, total)
    return maximum


def main():
    p = argparse.ArgumentParser()
    p.add_argument('pack', type=Path)
    p.add_argument('budget', type=Path)
    p.add_argument('output', type=Path)
    a = p.parse_args()
    def run(*args):
        subprocess.run([str(x) for x in args], check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory(prefix='mixture-budget-test-') as tmp:
        w = Path(tmp)
        for name, words, seed in [('a', ['a', 'b'], 52), ('b', ['b', 'c'], 61)]:
            write_arpa(w/f'{name}.arpa', toy(words, seed))
        run(a.pack, 'vocab', w/'a.arpa', w/'b.arpa', w/'vocab.txt')
        for name in ('a', 'b'):
            run(a.pack, 'pack', w/'vocab.txt', w/f'{name}.arpa', w/name, 1, 3)
            run(a.pack, 'pack', w/'vocab.txt', w/f'{name}.arpa', w/(name+'-parallel'), 2, 10000)
            for n in range(1, 6):
                assert (w/name/f'{n}.bin').read_bytes() == (w/(name+'-parallel')/f'{n}.bin').read_bytes()
        vocab, ar, _ = read_model(w/'a')
        _, br, _ = read_model(w/'b')
        def extended(rows, k):
            known = {x[0] for x in rows if len(x)==1}
            mapped = tuple(x if x in known else '<unk>' for x in k)
            return score(rows, mapped) - (math.log10(1+len(set(vocab)-known)) if mapped[-1]=='<unk>' else 0)
        probes = checked = 0
        for alpha in (.1, .25):
            out = w/f'mix-{alpha}'
            run(a.budget, 'mix', w/'a', w/'b', out, alpha, 2)
            same = w/f'mix-single-{alpha}'
            run(a.budget, 'mix', w/'a', w/'b', same, alpha, 1)
            for n in range(1, 6):
                assert (out/f'{n}.bin').read_bytes() == (same/f'{n}.bin').read_bytes()
            _, rows, loss = read_model(out)
            assert rows.keys() == ar.keys() | br.keys()
            check_mass(vocab, rows)
            for k, (p0, _) in rows.items():
                if k == ('<s>',):
                    continue
                expected = math.log10((1-alpha)*10**extended(ar, k) + alpha*10**extended(br, k))
                assert abs(p0-expected)<2e-6, (k, p0, expected)
                probes += 1
            # Brute full-vocabulary KL when deleting each individual record.
            for k in rows:
                if len(k)==1:
                    continue
                h = k[:-1]
                retained = {x[-1] for x in rows if len(x)==len(k) and x[:-1]==h and x!=k}
                p_sum = sum(10**rows[h+(t,)][0] for t in retained)
                q_sum = sum(10**score(rows, h[1:]+(t,)) for t in retained)
                bow = (1-p_sum)/(1-q_sum)
                kl = 0
                for t in vocab:
                    if t == '<s>':
                        continue
                    before = 10**score(rows, h+(t,))
                    after = before if t in retained else bow*10**score(rows, h[1:]+(t,))
                    kl += before*math.log(before/after)
                h_mass = sum(score(rows, h[:j]) for j in range(1,len(h)+1) if not (j==1 and h[0]=='<s>'))
                estimated = 0 if loss[k] == -math.inf else 10**(loss[k]-h_mass)
                assert abs(estimated-kl)<2e-6, (k, estimated, kl)
                checked += 1
            for budgets in ((3, 4, 4, 3), (0, 0, 0, 0), (10000,)*4):
                dest = w/f'pruned-{alpha}-{budgets[0]}'
                run(a.budget, 'prune', out, dest, *budgets)
                _, pruned, _ = read_model(dest)
                check_mass(vocab, pruned)
                for n in range(5, 1, -1):
                    mandatory = {k[:-1] for k in pruned if len(k)==n+1}
                    available = {k for k in rows if len(k)==n}
                    count = min(len(available), max(budgets[n-2],len(mandatory)))
                    candidates = sorted(available-mandatory,key=lambda k:(-loss[k],k))
                    chosen = mandatory | set(candidates[:count-len(mandatory)])
                    assert chosen == {k for k in pruned if len(k)==n}
                run(a.budget, 'export', dest, dest/'model.arpa')
        a.output.write_text(json.dumps(dict(passed=True,union_probability_probes=probes,
            independent_full_vocabulary_kl_checks=checked,thread_count_and_external_sort_byte_parity=True,
            exhaustive_budget_selection=True,zero_and_keep_all_budgets=True,
            normalization_and_prefix_closure=True),indent=2))
        print(a.output.read_text())


if __name__ == '__main__':
    main()
