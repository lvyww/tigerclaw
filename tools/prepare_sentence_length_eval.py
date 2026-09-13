"""Deterministic, record-held-out 2..6 Han-character evaluation snippets."""
import argparse
import json
import random
import re
import hashlib
from pathlib import Path
from sentence_variable_decoder import parse_shortest_code_index, encode_shortest_text
from prepare_sentence_benchmark_cases import SOURCE_FIELDS, CORPUS_ROOT

parser = argparse.ArgumentParser()
parser.add_argument('--lexicon', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--per-source-length', type=int, default=2500)
parser.add_argument('--seed', type=int, default=20260908)
parser.add_argument('--exclude-cases', type=Path)
args = parser.parse_args()
index = parse_shortest_code_index(args.lexicon)
rng = random.Random(args.seed)
excluded = json.loads(args.exclude_cases.read_text(encoding='utf-8')) if args.exclude_cases else []
seen = {c['text'] for c in excluded}
cutoffs = {source: max((c['record'] for c in excluded if c['source'] == source), default=0)
           for source in SOURCE_FIELDS}
cases = []
for source, fields in SOURCE_FIELDS.items():
    if source == 'wiki':
        paths = sorted(p for p in (CORPUS_ROOT / 'wiki_zh_2019').rglob('wiki_*') if p.is_file())
    else:
        folder, filename = {
            'webtext': ('webtext2019zh', 'web_text_zh_valid.json'),
            'news': ('new2016zh', 'news2016zh_valid.json'),
            'baike': ('baike2018qa', 'baike_qa_valid.json'),
        }[source]
        paths = [CORPUS_ROOT / folder / filename]
    counts = dict.fromkeys(range(2, 7), 0)
    record_id = 0
    for path in paths:
        with path.open(encoding='utf-8') as stream:
            for line in stream:
                record_id += 1
                # Confirmation uses only records beyond the previous scan, not
                # another window from an already inspected record.
                if record_id <= cutoffs[source]:
                    continue
                # Exact complement of the production KN trainer's wiki rule.
                if source == 'wiki' and (record_id - 1) % 100 != 99:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                spans = []
                for field in fields:
                    value = record.get(field)
                    if isinstance(value, str):
                        spans.extend(re.findall(r'[\u3400-\u4dbf\u4e00-\u9fff]{2,}', value[:5000]))
                # One random continuous snippet per length per original record.
                for length in counts:
                    if counts[length] >= args.per_source_length:
                        continue
                    choices = [(span, i) for span in spans for i in range(len(span) - length + 1)]
                    rng.shuffle(choices)
                    for span, start in choices:
                        text = span[start:start + length]
                        if text in seen:
                            continue
                        try:
                            code = encode_shortest_text(text, index)
                        except Exception:
                            continue
                        seen.add(text)
                        counts[length] += 1
                        cases.append(dict(text=text, code=code, source=source, record=record_id, length=length))
                        break
                if all(n == args.per_source_length for n in counts.values()):
                    break
        if all(n == args.per_source_length for n in counts.values()):
            break
    print(source, counts, 'records_scanned', record_id, flush=True)
    if any(n != args.per_source_length for n in counts.values()):
        raise RuntimeError('Insufficient unique samples; do not silently shrink the denominator')
rng.shuffle(cases)
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(cases, ensure_ascii=False), encoding='utf-8')
args.output.with_name(args.output.stem + '-sampling.json').write_text(json.dumps(dict(
    seed=args.seed, per_source_length=args.per_source_length, previous_record_cutoffs=cutoffs,
    excluded_cases=str(args.exclude_cases) if args.exclude_cases else None,
    excluded_cases_sha256=hashlib.sha256(args.exclude_cases.read_bytes()).hexdigest() if args.exclude_cases else None,
    cases_sha256=hashlib.sha256(args.output.read_bytes()).hexdigest(), lexicon=str(args.lexicon),
    lexicon_sha256=hashlib.sha256(args.lexicon.read_bytes()).hexdigest()), indent=2), encoding='utf-8')
print('written', len(cases), args.output, flush=True)
