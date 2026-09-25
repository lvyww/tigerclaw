"""Sample local article folders independently of model predictions."""
import argparse
import hashlib
import json
import os
import random
import re
from collections import Counter
from pathlib import Path
from prepare_thucnews_shape import sha, parse_shortest_code_index, encode_shortest_text


def paths(root):
    return sorted(Path(base) / name for base, _, names in os.walk(root)
                  for name in names if name.lower().endswith('.txt'))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, required=True)
    p.add_argument('--unpacked', type=Path, required=True)
    p.add_argument('--lexicon', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--quota', type=int, default=3000)
    p.add_argument('--seed', type=int, default=20260924)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    index = parse_shortest_code_index(args.lexicon)
    groups = {d.name: paths(d) for d in sorted(args.source.iterdir()) if d.is_dir()}
    groups['poetry_txt'] = paths(args.unpacked / 'poetry_txt')
    groups['名家散文27册'] = paths(args.unpacked / 'famous-essays')
    groups['root_txt'] = sorted(args.source.glob('*.txt'))
    rows, seen, file_manifest, stats_all = [], set(), {}, {}
    for category, files in sorted(groups.items()):
        rng = random.Random(f'{args.seed}:{category}')
        listing = '\n'.join(map(str, files))
        rng.shuffle(files)
        stats = Counter(available_files=len(files))
        chosen = []
        for file in files:
            if len(chosen) >= args.quota:
                break
            raw = file.read_bytes()
            encoding = 'utf-8-sig'
            try:
                text = raw.decode(encoding)
            except UnicodeDecodeError:
                encoding = 'gb18030'
                try:
                    text = raw.decode(encoding)
                except UnicodeDecodeError:
                    stats['decode_failed_files'] += 1
                    continue
            stats['scanned_files'] += 1
            file_manifest[str(file)] = dict(bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(), encoding=encoding)
            # News files contain many independent articles, one per line.
            per_file = []
            offset = 0
            for number, line in enumerate(text.splitlines(keepends=True), 1):
                start_offset = offset
                offset += len(line)
                value = line.strip()
                if (re.match(r'^(title|date|author|标题|日期|作者)\s*[:：]', value, re.I)
                        or value.startswith('◎')
                        or (number == 1 and value and value in file.stem)):
                    stats['metadata_lines_skipped'] += 1
                    continue
                candidates = []
                for match in re.finditer(r'[\u3400-\u4dbf\u4e00-\u9fff]+', line):
                    target = match.group()
                    stats['han_runs_scanned'] += 1
                    if not 4 <= len(target) <= 40:
                        stats['rejected_length'] += 1
                        continue
                    if any(c not in index.primary_code_by_char for c in target):
                        stats['rejected_code'] += 1
                        continue
                    candidates.append(dict(source=category, source_file=str(file), line_number=number,
                                           start=start_offset + match.start(), end=start_offset + match.end(),
                                           target=target))
                if category == 'daily_news_archive':
                    rng.shuffle(candidates)
                    per_file.extend(candidates[:3])
                else:
                    per_file.extend(candidates)
            rng.shuffle(per_file)
            accepted = 0
            for row in per_file:
                if len(chosen) >= args.quota or (category != 'daily_news_archive' and accepted >= 3):
                    break
                if row['target'] in seen:
                    stats['duplicate_candidate'] += 1
                    continue
                assert text[row['start']:row['end']] == row['target']
                seen.add(row['target'])
                row['code'] = encode_shortest_text(row['target'], index)
                chosen.append(row)
                accepted += 1
            if accepted:
                stats['sampled_files'] += 1
        for i, row in enumerate(chosen, 1):
            row['id'] = f'{category}_{i}'
        rows.extend(chosen)
        stats_all[category] = dict(stats, sampled=len(chosen), file_list_sha256=hashlib.sha256(listing.encode()).hexdigest())
        print(category, stats_all[category], flush=True)
    assert len(rows) == len({row['target'] for row in rows})
    cases = args.output / 'cases.tsv'
    cases.write_text(''.join('\t'.join(row[k] for k in ('id', 'source', 'code', 'target')) + '\n' for row in rows))
    with (args.output / 'provenance.jsonl').open('w') as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + '\n')
    (args.output / 'source-files.json').write_text(json.dumps(file_manifest, ensure_ascii=False, indent=2))
    meta = dict(total=len(rows), source=str(args.source), seed=args.seed, quota=args.quota,
                statistics=stats_all, cases_sha256=sha(cases), lexicon_sha256=sha(args.lexicon),
                policy='Sorted file paths, seeded file shuffle, shuffled eligible fragments; up to 3 per file and 3000 per source. Daily news: up to 3 per article line. Global exact text dedup. Contiguous Han runs 4..40 characters, no punctuation joining or traditional conversion. Explicit metadata lines and first-line title matching filename skipped. No prediction-based selection.',
                archive_policy='Use poetry_txt.7z text edition, omit duplicate poetry-master.zip; include famous-essays zip; scripts not executed',
                mean_characters=sum(len(r['target']) for r in rows)/len(rows),
                training_overlap='Not audited', provenance_offsets_verified=True,
                script_sha256=sha(Path(__file__)))
    (args.output / 'dataset-manifest.json').write_text(json.dumps(meta, ensure_ascii=False, indent=2) + '\n')
    print('TOTAL', len(rows), flush=True)


if __name__ == '__main__':
    main()
