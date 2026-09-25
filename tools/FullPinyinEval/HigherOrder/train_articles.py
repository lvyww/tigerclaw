"""Prepare a reproducible, isolated Articles character LM corpus from snapshots.

No installation writes. Entire previously sampled Articles files are excluded,
as are their exact normalized segments wherever duplicated. Document-hash dev/test
splits are frozen before training. All counts refer to this captured snapshot.
"""
import argparse
from collections import Counter
import hashlib
import html
import json
from pathlib import Path
import re
import sqlite3
import tarfile
import time
import unicodedata
import zipfile

HAN = re.compile(r'[\u3400-\u4dbf\u4e00-\u9fff]+')
META = re.compile(r'^(title|date|author|标题|日期|作者)\s*[:：]', re.I)
URL = re.compile(r'https?://[^\s<>]+')
TAG = re.compile(r'<[^>]*>')
SKIP = {'en', 'ying', 'ping', 'poetry_txt'}


def digest(data):
    return hashlib.sha256(data).digest()


def sha(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def dump(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + '.partial')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')
    temporary.replace(path)


def normalized(raw, name):
    if raw.startswith((b'\xff\xfe', b'\xfe\xff')):
        text, encoding = raw.decode('utf-16'), 'utf-16'
    else:
        try:
            text, encoding = raw.decode('utf-8-sig'), 'utf-8-sig'
        except UnicodeDecodeError:
            text, encoding = raw.decode('gb18030'), 'gb18030'
    text = URL.sub(' ', TAG.sub(' ', unicodedata.normalize('NFKC', html.unescape(text))))
    segments, seen = [], set()
    for number, line in enumerate(text.splitlines(), 1):
        value = line.strip()
        if META.match(value) or value.startswith('◎') or (number == 1 and value and value in Path(name).stem):
            continue
        for match in HAN.finditer(line):
            run = match.group()
            for start in range(0, len(run), 256):
                segment = run[start:start + 256]
                if len(segment) >= 2 and segment not in seen:
                    segments.append(segment)
                    seen.add(segment)
    return segments, encoding


def document_split(doc_hash, reserved=False):
    # Split identifiers also define precedence when a duplicate is reserved.
    if reserved:
        return 3
    bucket = int.from_bytes(doc_hash[:8], 'big') % 1000
    return 2 if bucket < 10 else 1 if bucket < 20 else 0


def documents(args):
    with tarfile.open(args.snapshot, 'r|') as archive:
        for member in archive:
            name = member.name.removeprefix('./')
            if member.isfile() and name.lower().endswith('.txt') and name.split('/')[0] not in SKIP:
                with archive.extractfile(member) as f:
                    yield name, f.read()
    # Only the text edition of poetry is included, never poetry-master.zip.
    for path in sorted(args.poetry.rglob('*.txt')):
        yield 'poetry_txt/' + path.relative_to(args.poetry).as_posix(), path.read_bytes()
    with zipfile.ZipFile(args.essays) as archive:
        for name in sorted(archive.namelist()):
            if name.lower().endswith('.txt'):
                yield 'famous-essays/' + name, archive.read(name)


def reserved_names(provenance):
    names = set()
    for line in provenance.read_text().splitlines():
        row = json.loads(line)
        name = row['source_file']
        marker = '/unpacked/' if '/unpacked/' in name else '/Copus/articles/'
        names.add(name.split(marker, 1)[1])
    return names


def prepare(args):
    args.output.mkdir(parents=True, exist_ok=True)
    complete = args.output / 'corpus-manifest.json'
    if complete.exists():
        raise RuntimeError('Corpus already finalized; choose a new output for another snapshot')
    database = args.output / 'split-index.sqlite'
    if database.exists():
        raise RuntimeError('Partial corpus index exists; inspect it before restarting')
    db = sqlite3.connect(database)
    db.execute('PRAGMA journal_mode=OFF')
    db.execute('PRAGMA synchronous=OFF')
    db.execute('PRAGMA cache_size=-131072')
    db.execute('CREATE TABLE docs (hash BLOB PRIMARY KEY, split INTEGER NOT NULL) WITHOUT ROWID')
    db.execute('CREATE TABLE held (hash BLOB PRIMARY KEY, split INTEGER NOT NULL, text TEXT, source TEXT) WITHOUT ROWID')
    db.execute('CREATE TABLE emitted (hash BLOB PRIMARY KEY) WITHOUT ROWID')
    reserved = reserved_names(args.provenance)
    seen_reserved, counts, by_source = set(), Counter(), {}
    started, last = time.time(), time.monotonic()
    with (args.output / 'source-files.jsonl').open('w') as listing:
        for name, raw in documents(args):
            stats = by_source.setdefault(name.split('/')[0] if '/' in name else 'root_txt', Counter())
            counts['files'] += 1
            counts['input_bytes'] += len(raw)
            stats['files'] += 1
            stats['bytes'] += len(raw)
            entry = dict(name=name, bytes=len(raw), sha256=digest(raw).hex())
            try:
                segments, encoding = normalized(raw, name)
            except UnicodeDecodeError:
                counts['decode_failures'] += 1
                entry['excluded'] = 'decode failure'
                listing.write(json.dumps(entry, ensure_ascii=False) + '\n')
                continue
            doc_hash = digest('\n'.join(segments).encode())
            split = document_split(doc_hash, name in reserved)
            if name in reserved:
                seen_reserved.add(name)
            entry.update(encoding=encoding, normalized_sha256=doc_hash.hex(), initial_split=split, segments=len(segments))
            listing.write(json.dumps(entry, ensure_ascii=False) + '\n')
            counts['segments_before_document_dedup'] += len(segments)
            db.execute('INSERT INTO docs VALUES (?,?) ON CONFLICT(hash) DO UPDATE SET split=max(split,excluded.split)', (doc_hash, split))
            if split:
                for segment in segments:
                    db.execute('INSERT INTO held VALUES (?,?,?,?) ON CONFLICT(hash) DO UPDATE SET split=max(split,excluded.split)',
                               (digest(segment.encode()), split, segment, name))
            if counts['files'] % 1000 == 0:
                db.commit()
            if time.monotonic() - last > 15:
                dump(args.output / 'progress.json', dict(stage='partition', **counts))
                print('partition', dict(counts), flush=True)
                last = time.monotonic()
    db.commit()
    if reserved - seen_reserved:
        dump(args.output / 'missing-reserved-files.json', sorted(reserved - seen_reserved))
        raise RuntimeError(f'{len(reserved - seen_reserved)} reserved source files missing in snapshot')
    with (args.output / 'train.tokens.partial').open('w') as train:
        for name, raw in documents(args):
            try:
                segments, _ = normalized(raw, name)
            except UnicodeDecodeError:
                continue
            doc_hash = digest('\n'.join(segments).encode())
            split, = db.execute('SELECT split FROM docs WHERE hash=?', (doc_hash,)).fetchone()
            if split:
                counts['reserved_files'] += 1
                continue
            if db.execute('INSERT OR IGNORE INTO emitted VALUES (?)', (doc_hash,)).rowcount == 0:
                counts['duplicate_training_documents'] += 1
                continue
            stats = by_source.setdefault(name.split('/')[0] if '/' in name else 'root_txt', Counter())
            for segment in segments:
                if db.execute('SELECT 1 FROM held WHERE hash=?', (digest(segment.encode()),)).fetchone():
                    counts['heldout_duplicate_segments_excluded'] += 1
                    continue
                train.write(' '.join(segment) + '\n')
                counts['train_segments'] += 1
                counts['train_tokens'] += len(segment)
                stats['train_tokens'] += len(segment)
            counts['unique_training_documents'] += 1
            if counts['unique_training_documents'] % 1000 == 0:
                db.commit()
            if time.monotonic() - last > 15:
                dump(args.output / 'progress.json', dict(stage='write_training', **counts))
                print('write_training', dict(counts), flush=True)
                last = time.monotonic()
    db.commit()
    (args.output / 'train.tokens.partial').replace(args.output / 'train.tokens')
    for split, name in [(1, 'dev'), (2, 'test')]:
        rows = db.execute('SELECT text,source FROM held WHERE split=? ORDER BY hash LIMIT 20000', (split,))
        with (args.output / f'{name}.tokens').open('w') as tokens, (args.output / f'{name}.jsonl').open('w') as meta:
            for text, source in rows:
                tokens.write(' '.join(text) + '\n')
                meta.write(json.dumps(dict(text=text, source=source), ensure_ascii=False) + '\n')
                counts[f'{name}_rows'] += 1
    counts['held_unique_segments'] = db.execute('SELECT count(*) FROM held').fetchone()[0]
    db.close()
    assert counts['train_tokens'] > 100000 and counts['dev_rows'] > 100 and counts['test_rows'] > 100
    manifest = dict(source_snapshot=str(args.snapshot), snapshot_sha256=sha(args.snapshot),
        essays_sha256=sha(args.essays), provenance_sha256=sha(args.provenance), reserved_source_files=len(reserved),
        counts=counts, by_source=by_source, started=started, finished=time.time(),
        policy='NFKC/HTML decoding, Han runs 2..256, no punctuation joining, no traditional conversion or pinyin. Per-document segment dedup; normalized document dedup. SHA256 document split modulo 1000: 0..9 test,10..19 dev,otherwise train. Prior Articles sampled files excluded. All exact held segments excluded from all training documents. Shared held segments use external > test > dev precedence; at most 20000 rows per new split by hash order. en/ying/ping/scripts excluded; poetry text edition once; famous essays TXT included.',
        overlap_limit='Exact document/segment exclusion does not detect near-duplicates or common substrings inside longer segments; older mainline training overlap is not audited.',
        files={name: dict(bytes=(args.output/name).stat().st_size,sha256=sha(args.output/name))
               for name in ('train.tokens','dev.tokens','test.tokens','dev.jsonl','test.jsonl','source-files.jsonl')})
    dump(complete, manifest)
    dump(args.output / 'progress.json', dict(stage='corpus_complete', **counts))
    print(json.dumps(manifest, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--poetry', type=Path, required=True)
    parser.add_argument('--essays', type=Path, required=True)
    parser.add_argument('--provenance', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    prepare(parser.parse_args())
