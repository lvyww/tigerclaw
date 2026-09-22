"""Export the pinned Wanxiang main dictionary as full-pinyin experiment edges.

No corpus annotation, frequency retuning, abbreviations or fuzzy spelling. Source
files remain untouched. Duplicate (text, code) rows retain the maximum weight,
matching the experiment's existing Lexicon loader.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import unicodedata


def syllable(raw):
    # Preserve umlaut before removing tone combining marks (nǚ -> nv, not nu).
    raw = raw.lower().translate(str.maketrans('üǖǘǚǜ', 'vvvvv'))
    value = ''.join(c for c in unicodedata.normalize('NFD', raw)
                    if unicodedata.category(c) != 'Mn')
    if not re.fullmatch('[a-z]+', value):
        raise ValueError(raw)
    return {'nve': 'nue', 'lve': 'lue'}.get(value, value)


def han(text):
    return bool(text) and all(c == '〇' or '\u3400' <= c <= '\u9fff' or
                             '\uf900' <= c <= '\ufaff' or
                             0x20000 <= ord(c) <= 0x323AF for c in text)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--tokens', type=Path, help='Export aligned joint tokens from the source syllables for the runtime')
    args = p.parse_args()
    if args.output.exists():
        p.error('output already exists; use a fresh experiment path')
    if args.tokens and args.tokens.exists():
        p.error('token output already exists')
    entry = args.source / 'wanxiang.dict.yaml'
    names = re.findall(r'^  - (dicts/\S+)', entry.read_text(), re.M)
    if not names:
        raise ValueError('missing import_tables')
    rows, counts, exclusions, hashes = {}, Counter(), [], {entry.name: sha(entry)}
    annotations = {}
    for name in names:
        path = args.source / (name + '.dict.yaml')
        hashes[str(path.relative_to(args.source))] = sha(path)
        body = False
        with path.open(encoding='utf-8-sig') as stream:
            for line_number, line in enumerate(stream, 1):
                if line.strip() == '...':
                    body = True
                    continue
                if not body or not line.strip() or line.startswith('#'):
                    continue
                counts['sourceRows'] += 1
                fields = line.rstrip('\r\n').split('\t')
                reason = None
                if len(fields) < 2:
                    raise ValueError(f'bad row {path}:{line_number}')
                if not han(fields[0]):
                    reason = 'non_han_text'
                try:
                    readings = [syllable(s) for s in fields[1].split()]
                    code = ''.join(readings)
                    if not code:
                        reason = 'empty_code'
                    if args.tokens and len(readings) != len(fields[0]):
                        reason = 'unaligned_source_syllables'
                except ValueError:
                    reason = 'non_pinyin_code'
                if reason:
                    counts[reason] += 1
                    exclusions.append(dict(file=name, line=line_number, reason=reason, row=line.rstrip()))
                    continue
                try:
                    frequency = int(fields[2]) if len(fields) >= 3 and fields[2] else 0
                except ValueError:
                    # Weight affects only equal-score ties in the fixed decoder.
                    frequency = 0
                    counts['invalidWeightZeroTieBreakOnly'] += 1
                    exclusions.append(dict(file=name, line=line_number,
                                           reason='retained_with_zero_invalid_weight', row=line.rstrip()))
                if not -2147483648 <= frequency <= 2147483647:
                    raise ValueError(f'frequency overflow {path}:{line_number}')
                if frequency < 0:
                    counts['negativeWeightClampedLikeExistingLoader'] += 1
                    frequency = 0
                if len(fields) < 3 or not fields[2]:
                    counts['missingWeightZeroTieBreakOnly'] += 1
                key = fields[0], code
                if key in rows:
                    counts['duplicateNormalizedRows'] += 1
                if args.tokens and (key not in rows or frequency > rows[key]):
                    annotations[key] = tuple(c + '/' + {'nue': 'nve', 'lue': 'lve'}.get(py, py)
                                             for c, py in zip(fields[0], readings))
                rows[key] = max(frequency, rows.get(key, 0))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('w', encoding='utf-8', newline='\n') as stream:
        for (text, code), frequency in rows.items():
            stream.write(f'{text}\t{code}\t{frequency}\n')
    counts['exportedRows'] = len(rows)
    counts['singleCharacterRows'] = sum(len(text) == 1 for text, _ in rows)
    counts['wordRows'] = counts['exportedRows'] - counts['singleCharacterRows']
    report = dict(sources=hashes, outputSha256=sha(args.output), counts=dict(counts),
                  exporterSha256=sha(Path(__file__)), exclusions=exclusions,
                  policy='All imported main dictionaries; pure Han; strip tone; v=umlaut; nve/lve=nue/lue; max duplicate weight; no abbreviation/fuzzy derivation')
    if args.tokens:
        args.tokens.parent.mkdir(parents=True, exist_ok=True)
        with args.tokens.open('w', encoding='utf-8') as stream:
            stream.write('[\n')
            for index, ((text, code), tokens) in enumerate(annotations.items()):
                if index: stream.write(',\n')
                stream.write(json.dumps(dict(text=text, code=code, tokens=tokens), ensure_ascii=False, separators=(',', ':')))
            stream.write('\n]\n')
        report.update(tokensSha256=sha(args.tokens), tokenPolicy='Source per-character syllables; no inferred pronunciation; unaligned rows excluded; maximum-weight pronunciation wins duplicates')
    args.output.with_suffix('.manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    print(json.dumps(dict(counts), ensure_ascii=False))


if __name__ == '__main__':
    main()
