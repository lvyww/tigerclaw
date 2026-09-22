"""Align existing dictionary entries with readings; never re-annotate candidates.

Use the original single-character codes first. Only entries without a complete
alignment consult the model's observed readings. Fail on ambiguity or gaps.
"""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path


def raw_spelling(value):
    return {'lve': 'lue', 'nve': 'nue'}.get(value, value)


def model_spelling(value):
    return {'lue': 'lve', 'nue': 'nve'}.get(value, value)


def align(text, code, readings):
    paths = [('', [])]
    for char in text:
        paths = [(done + py, path + [py]) for done, path in paths
                 for py in sorted(readings[char]) if code.startswith(done + py)]
    return [path for done, path in paths if done == code]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('table', type=Path)
    p.add_argument('arpa', type=Path)
    p.add_argument('output', type=Path)
    args = p.parse_args()
    rows = [l.split() for l in args.table.read_text(encoding='utf-8-sig').splitlines() if l.strip()]
    readings, observed = defaultdict(set), defaultdict(set)
    for text, code, _ in rows:
        if len(text) == 1:
            readings[text].add(code)
    vocab = set()
    with args.arpa.open() as stream:
        for line in stream:
            if line.startswith('\\2-grams:'):
                break
            fields = line.split()
            if len(fields) > 1 and '/' in fields[1] and fields[1] != '</s>':
                char, py = fields[1].rsplit('/', 1)
                vocab.add(fields[1])
                observed[char].add(raw_spelling(py))
    annotations, fallback, oov = [], [], []
    for text, code, _ in rows:
        paths = align(text, code, readings)
        if not paths:
            paths = align(text, code, observed)
            fallback.append((text, code, paths))
        if len(paths) != 1:
            raise ValueError((text, code, paths))
        tokens = [char + '/' + model_spelling(py) for char, py in zip(text, paths[0])]
        annotations.append(dict(text=text, code=code, tokens=tokens))
        oov.extend(token for token in tokens if token not in vocab)
    args.output.mkdir(exist_ok=True)
    target = args.output / 'tokens.json'
    content = json.dumps(annotations, ensure_ascii=False)
    if target.exists() and target.read_text() != content:
        raise ValueError('frozen token map differs')
    target.write_text(content)
    manifest = dict(tableSha256=hashlib.sha256(args.table.read_bytes()).hexdigest(), rows=len(rows),
                    wordAlignmentFallback=fallback, oovTokenTypes=sorted(set(oov)), oovTokenOccurrences=len(oov))
    (args.output / 'tokens.manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
    print('rows', len(rows), 'fallback', fallback, 'OOV types', len(set(oov)))


if __name__ == '__main__':
    main()
