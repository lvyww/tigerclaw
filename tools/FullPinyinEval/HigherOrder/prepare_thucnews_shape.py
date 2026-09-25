"""Freeze balanced THUCNews shape cases before model evaluation."""
import argparse
import hashlib
import json
import random
import re
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from sentence_variable_decoder import parse_shortest_code_index, encode_shortest_text


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--lexicon', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--per-category', type=int, default=5000)
    parser.add_argument('--seed', type=int, default=20260923)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    index = parse_shortest_code_index(args.lexicon)
    mapping = index.primary_code_by_char
    seen = set()
    all_rows = []
    statistics = {}
    for path in sorted(args.source.glob('THUCNews_*.jsonl')):
        category = path.stem.removeprefix('THUCNews_')
        rng = random.Random(f'{args.seed}:{category}')
        stats = Counter()
        reservoir = []
        with path.open(encoding='utf-8') as stream:
            for line_number, line in enumerate(stream, 1):
                record = json.loads(line)
                stats['records'] += 1
                content = record['content']
                eligible = []
                for match in re.finditer(r'[\u3400-\u4dbf\u4e00-\u9fff]+', content):
                    text = match.group()
                    stats['han_runs'] += 1
                    if not 4 <= len(text) <= 40:
                        stats['rejected_length'] += 1
                        continue
                    if any(c not in mapping for c in text):
                        stats['rejected_code'] += 1
                        continue
                    eligible.append((text, match.start(), match.end()))
                if not eligible:
                    stats['records_without_eligible_fragment'] += 1
                    continue
                text, start, end = rng.choice(eligible)
                if text in seen:
                    stats['duplicate_chosen_fragment'] += 1
                    continue
                seen.add(text)
                row = dict(source=category, source_file=path.name,
                           line_number=line_number, record_id=record.get('id'),
                           start=start, end=end, target=text,
                           code=encode_shortest_text(text, index))
                stats['eligible_unique_records'] += 1
                count = stats['eligible_unique_records']
                if len(reservoir) < args.per_category:
                    reservoir.append(row)
                else:
                    position = rng.randrange(count)
                    if position < args.per_category:
                        reservoir[position] = row
        assert len(reservoir) == args.per_category, (category, stats)
        reservoir.sort(key=lambda row: row['line_number'])
        for number, row in enumerate(reservoir, 1):
            row['id'] = f'{category}_{number}'
        all_rows.extend(reservoir)
        statistics[category] = dict(stats, sampled=len(reservoir), sha256=sha(path))
        print(category, dict(stats), flush=True)
    assert len(statistics) == 6
    cases = args.output / 'cases.tsv'
    cases.write_text(''.join('\t'.join(row[k] for k in ('id', 'source', 'code', 'target')) + '\n'
                             for row in all_rows), encoding='utf-8')
    with (args.output / 'provenance.jsonl').open('w', encoding='utf-8') as stream:
        for row in all_rows:
            stream.write(json.dumps(row, ensure_ascii=False) + '\n')
    manifest = dict(seed=args.seed, per_category=args.per_category, total=len(all_rows),
                    policy='Content only; contiguous Han runs of 4..40 characters; no joining across punctuation/numbers/Latin; one random eligible run per record; global text deduplication in sorted file order; reservoir sample per category',
                    encoding='Existing shortest primary character codes, minimum two letters; explicit rank marker when required',
                    training_overlap='Not audited; do not claim unseen or recent text',
                    statistics=statistics, cases_sha256=sha(cases),
                    lexicon=str(args.lexicon), lexicon_sha256=sha(args.lexicon),
                    script_sha256=sha(Path(__file__)))
    (args.output / 'dataset-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')


if __name__ == '__main__':
    main()
