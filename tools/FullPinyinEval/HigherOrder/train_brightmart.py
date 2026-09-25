"""Isolated, resumable Brightmart -> TigerClaw character-token KenLM training.

Run inside a systemd user service; never changes installed models or schemas.
Completed stages are checkpointed; interrupted preprocessing restarts that stage.
"""
import argparse
import concurrent.futures as futures
import ctypes
import hashlib
import html
import fcntl
import json
import math
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import time
import unicodedata

CJK = re.compile(r'[\u3400-\u4dbf\u4e00-\u9fff]+')
TAGS = re.compile(r'<[^>]*>')
URLS = re.compile(r'https?://[^\s<>]+')


def dump(path, data):
    tmp = path.with_suffix(path.suffix + '.tmp')
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')
    tmp.replace(path)


def tokenize(batch):
    lines = []
    for value in batch:
        value = URLS.sub(' ', TAGS.sub(' ', unicodedata.normalize('NFKC', html.unescape(value))))
        seen = set()
        for match in CJK.finditer(value):
            text = match.group()
            for start in range(0, len(text), 256):
                sentence = text[start:start + 256]
                if len(sentence) < 2 or sentence in seen:
                    continue
                seen.add(sentence)
                tokens = ' '.join(sentence)
                # Text-based split, independent of source.
                heldout = int.from_bytes(hashlib.sha256(sentence.encode()).digest()[:8], 'big') % 1000 == 0
                lines.append((tokens, sentence, heldout))
    return lines


def inputs(source, exclude_news=False, only_news=False):
    if exclude_news and only_news:
        raise ValueError('Cannot both exclude and select only news')
    train = [(source/'baike2018qa/baike_qa_train.json', ['title', 'desc', 'answer']),
             (source/'new2016zh/news2016zh_train.json', ['title', 'content']),
             (source/'webtext2019zh/web_text_zh_train.json', ['title', 'content'])]
    train += [(p, ['text']) for p in sorted((source/'wiki_zh_2019/wiki_zh').glob('*/wiki_*'))]
    if exclude_news:
        train = [(p, fields) for p, fields in train if p.relative_to(source).parts[0] != 'new2016zh']
    if only_news:
        train = [(p, fields) for p, fields in train if p.relative_to(source).parts[0] == 'new2016zh']
    for path, _ in train:
        if not path.is_file():
            raise FileNotFoundError(path)
    return train


def preprocess(args, manifest):
    out = args.output
    database = args.work/'dedup.sqlite'
    # Only remove this pipeline's rebuildable, stage-local index.
    if database.exists():
        database.unlink()
    db = sqlite3.connect(database)
    db.execute('PRAGMA journal_mode=OFF')
    db.execute('PRAGMA synchronous=OFF')
    db.execute('PRAGMA cache_size=-65536')
    db.execute('CREATE TABLE seen (hash BLOB PRIMARY KEY) WITHOUT ROWID')
    counts = dict(records=0, unique_fields=0, duplicate_fields=0, train_segments=0,
                  train_tokens=0, heldout_excluded=0, heldout_saved=0, input_bytes=0,
                  evaluation_segments_excluded=0)
    excluded = set()
    for path in getattr(args, 'exclude_cases', []):
        for line in path.read_text().splitlines():
            excluded.add(line.split('\t')[3])
    heldout_seen = set()
    last = time.monotonic()
    def batches():
        batch = []
        size = 0
        for path, fields in inputs(args.source, getattr(args, 'exclude_news', False), getattr(args, 'only_news', False)):
            print('reading', path, flush=True)
            with path.open('rb') as stream:
                for row in stream:
                    data = json.loads(row)
                    counts['records'] += 1
                    counts['input_bytes'] += len(row)
                    for field in fields:
                        value = data.get(field, '')
                        if not isinstance(value, str) or not value.strip():
                            continue
                        value = value.strip()
                        digest = hashlib.sha256(value.encode()).digest()
                        if db.execute('INSERT OR IGNORE INTO seen VALUES (?)', (digest,)).rowcount == 0:
                            counts['duplicate_fields'] += 1
                            continue
                        counts['unique_fields'] += 1
                        batch.append(value)
                        size += len(value)
                    if size >= 32768:
                        yield batch
                        batch, size = [], 0
                    if counts['records'] % 10000 == 0:
                        db.commit()
        if batch:
            yield batch
    train_tmp, hold_tmp = out/'train.tokens.partial', out/'heldout.jsonl.partial'
    with train_tmp.open('w') as train, hold_tmp.open('w') as hold, futures.ProcessPoolExecutor(
            max_workers=args.workers) as pool:
        for result in pool.map(tokenize, batches(), buffersize=args.workers * 2):
            for tokens, sentence, heldout in result:
                if sentence in excluded:
                    counts['evaluation_segments_excluded'] += 1
                    continue
                if heldout:
                    counts['heldout_excluded'] += 1
                    if len(heldout_seen) < 20000 and sentence not in heldout_seen:
                        heldout_seen.add(sentence)
                        hold.write(json.dumps(dict(text=sentence, tokens=tokens), ensure_ascii=False) + '\n')
                        counts['heldout_saved'] += 1
                else:
                    train.write(tokens + '\n')
                    counts['train_segments'] += 1
                    counts['train_tokens'] += tokens.count(' ') + 1
            if time.monotonic() - last >= 15:
                check_space(args)
                dump(out/'progress.json', dict(stage='tokenizing', updated=time.time(), **counts))
                print(json.dumps(counts), flush=True)
                last = time.monotonic()
    db.commit()
    db.close()
    if counts['train_tokens'] < 1000:
        raise RuntimeError('Insufficient training data')
    train_tmp.replace(out/'train.tokens')
    hold_tmp.replace(out/'heldout.jsonl')
    manifest['preprocess'] = counts
    dump(out/'manifest.json', manifest)


def check_space(args):
    for path in [args.output, args.work, Path('/mnt/c')]:
        free = shutil.disk_usage(path).free
        if free < args.reserve_gib * 1024**3:
            raise RuntimeError(f'Free-space guard: {path} has {free / 1024**3:.1f} GiB; reserve {args.reserve_gib} GiB')


def command(args, name, cmd):
    check_space(args)
    print(name, cmd, flush=True)
    dump(args.output/'progress.json', dict(stage=name, updated=time.time(), command=cmd))
    with (args.output/(name + '.log')).open('w') as log:
        child = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)
        try:
            while child.poll() is None:
                time.sleep(5)
                check_space(args)
            if child.returncode:
                raise RuntimeError(f'{name} failed with exit {child.returncode}; see {name}.log')
        finally:
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()


def validate(args, model):
    lib = ctypes.CDLL(str(args.scorer))
    lib.ho_load.argtypes = [ctypes.c_char_p]
    lib.ho_load.restype = ctypes.c_void_p
    lib.ho_order.argtypes = [ctypes.c_void_p]
    lib.ho_order.restype = ctypes.c_uint
    lib.ho_score.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.POINTER(ctypes.c_uint)]
    lib.ho_score.restype = ctypes.c_double
    lib.ho_free.argtypes = [ctypes.c_void_p]
    handle = lib.ho_load(os.fsencode(model))
    if not handle or lib.ho_order(handle) != 5:
        raise RuntimeError('Runtime scorer failed to load order-5 model')
    score_sum, tokens, oov, rows = 0.0, 0, 0, 0
    try:
        with (args.output/'heldout.jsonl').open() as stream:
            for line in stream:
                text = json.loads(line)['tokens']
                missing = ctypes.c_uint()
                value = lib.ho_score(handle, text.encode(), ctypes.byref(missing))
                if not math.isfinite(value):
                    raise RuntimeError('Non-finite heldout score')
                score_sum += value
                tokens += len(text.split()) + 1  # EOS included by scorer.
                oov += missing.value
                rows += 1
    finally:
        lib.ho_free(handle)
    if not rows:
        raise RuntimeError('No heldout sentences')
    with model.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return dict(order=5, bytes=model.stat().st_size, sha256=digest, heldout_rows=rows,
                heldout_oov=oov, perplexity=10 ** (-score_sum / tokens))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--bin', type=Path, required=True)
    parser.add_argument('--scorer', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=4)
    parser.add_argument('--reserve-gib', type=int, default=30)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    args.work.mkdir(parents=True, exist_ok=True)
    lock = (args.output/'pipeline.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    manifest_path = args.output/'manifest.json'
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else dict(
        created=time.time(), source=str(args.source), token_format='single Han character; no pinyin', purpose='虎整句形码五阶实验', order=5,
        smoothing='modified Kneser-Ney', prune=[0, 0, 1, 1, 1], quantization_bits=8,
        heldout='SHA256(normalized sentence) first 8 bytes modulo 1000 == 0; all occurrences excluded',
        official_valid_test='Not used for training',
        files=[dict(path=str(p), bytes=p.stat().st_size, mtime_ns=p.stat().st_mtime_ns, fields=f) for p, f in inputs(args.source)])
    if manifest['token_format'] != 'single Han character; no pinyin' or manifest['source'] != str(args.source):
        raise RuntimeError('Cannot resume a different corpus/token format')
    dump(manifest_path, manifest)
    try:
        if 'preprocess' not in manifest:
            preprocess(args, manifest)
        arpa = args.output/'char5.arpa'
        if not manifest.get('arpa_complete'):
            command(args, 'train', [str(args.bin/'lmplz'), '-o', '5', '-S', '8G', '-T', str(args.work),
                                   '--text', str(args.output/'train.tokens'), '--arpa', str(arpa) + '.partial',
                                   '--prune', '0', '0', '1', '1', '1'])
            Path(str(arpa) + '.partial').replace(arpa)
            manifest['arpa_complete'] = True
            dump(manifest_path, manifest)
        model = args.output/'char5-q8.klm'
        if not manifest.get('binary_complete'):
            command(args, 'quantize', [str(args.bin/'build_binary'), '-T', str(args.work), '-S', '4G',
                                      '-q', '8', '-b', '8', '-a', '64', 'trie', str(arpa), str(model) + '.partial'])
            Path(str(model) + '.partial').replace(model)
            manifest['binary_complete'] = True
            dump(manifest_path, manifest)
        manifest['validation'] = validate(args, model)
        manifest['finished'] = time.time()
        dump(manifest_path, manifest)
        dump(args.output/'progress.json', dict(stage='complete', updated=time.time(), **manifest['validation']))
        print('COMPLETE', json.dumps(manifest['validation']), flush=True)
    except BaseException as error:
        dump(args.output/'progress.json', dict(stage='failed', updated=time.time(), error=str(error)))
        raise


if __name__ == '__main__':
    main()
