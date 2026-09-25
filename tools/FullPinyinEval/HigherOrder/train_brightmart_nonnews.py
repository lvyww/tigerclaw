"""Isolated Brightmart training: exclude new2016zh, or select it with --news-only.

Original inputs are never edited. Resume only this captured input/configuration.
"""
import argparse
import concurrent.futures
import fcntl
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import time

from train_brightmart import inputs, preprocess
from train_articles import dump, sha
from build_articles_model import verified_copy, header, SCORE_LUA

HERE = Path(__file__).resolve().parent
SOURCE = Path('/mnt/c/Archive/Copus/brightmart_nlp_chinese_corpus')
KENLM = Path('/home/yc/tools/tigerclaw-brightmart/build/bin')
CONVERTER = Path('/home/yc/tmp/shape-mix-rime/converter/build_tcs_knm03_preserving')
QUANTIZER = Path('/home/yc/tmp/mainline-count-prune-20260924/requantize')
CASES = {
    'old10k': Path('/home/yc/tmp/mohu-v5-old10k-20260924/cases.tsv'),
    'articles': Path('/mnt/c/Archive/char5-corpus4_0-20260922/articles-comparison-20260923/cases.tsv'),
    'thucnews': Path('/mnt/c/Archive/char5-corpus4_0-20260922/thucnews-comparison-20260923/cases.tsv'),
}


def canonical_arpa(src, dst):
    counts, changed, section = [], 0, 0
    with src.open() as inp, dst.open('w') as out:
        for line in inp:
            if line.startswith('ngram '):
                counts.append(int(line.split('=')[1]))
            if line.startswith('\\') and '-grams:' in line:
                section = int(line.split('-')[0][1:])
            fields = line.split()
            if section == 1 and len(fields) in (2, 3) and fields[1] == '<s>':
                fields[0] = '0'
                line = '\t'.join(fields) + '\n'
                changed += 1
            out.write(line)
    assert len(counts) == 5 and changed == 1
    return counts


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--work', type=Path, required=True)
    p.add_argument('--archive', type=Path, required=True)
    p.add_argument('--news-only', action='store_true')
    a = p.parse_args()
    # Limit all descendants to the same twelve logical CPUs, including libraries
    # whose own pipeline helper threads are not governed by OpenMP.
    os.sched_setaffinity(0, sorted(os.sched_getaffinity(0))[:12])
    w = a.work
    w.mkdir(parents=True, exist_ok=True)
    lock = (w/'pipeline.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    dump(w/'pid.json', dict(pid=os.getpid(), start_ticks=Path(f'/proc/{os.getpid()}/stat').read_text().split()[21]))
    source_files = inputs(SOURCE, not a.news_only, a.news_only)
    assert source_files and all(('new2016zh' in x.relative_to(SOURCE).parts) == a.news_only for x, _ in source_files)
    config = dict(order=5, prune=[0, 0, 1, 1, 1], source=str(SOURCE), excluded_directory=None if a.news_only else 'new2016zh',
                  cases={name: dict(path=str(path), sha256=sha(path)) for name, path in CASES.items()},
                  policy='Existing Brightmart NFKC/HTML/URL/Han-run preprocessing; raw-field dedup; per-field segment dedup; 2..256 Han characters; no punctuation or whitespace joining. Deterministic 0.1% segment holdout. Additionally exclude exact targets of all three frozen evaluation sets from training and heldout. No fuzzy/substring overlap exclusion. Only source train files plus wiki; official valid/test files excluded.',
                  workers='11 preprocessing workers plus coordinator; 12 evaluation workers; process tree restricted to 12 logical CPUs; OMP_NUM_THREADS=12',
                  no_deployment=True)
    if a.news_only:
        config['only_directory'] = 'new2016zh'
        config['policy'] = config['policy'].replace('Only source train files plus wiki;', 'Only news2016zh_train.json title/content fields;')
    mp = w/'manifest.json'
    m = json.loads(mp.read_text()) if mp.exists() else dict(config=config, stages={}, started=time.time())
    assert m['config'] == config, 'Cannot resume a different configuration'
    dump(mp, m)
    def run(name, command):
        if name in m['stages']:
            return
        assert shutil.disk_usage(w).free > 30*1024**3, '30 GiB free-space reserve'
        cmd = list(map(str, command))
        dump(w/'progress.json', dict(stage=name, command=cmd, time=time.time()))
        print(name, flush=True)
        start = time.time()
        with (w/(name+'.log')).open('w') as log:
            subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, check=True,
                           env=dict(os.environ, OMP_NUM_THREADS='12', OPENBLAS_NUM_THREADS='1'))
        m['stages'][name] = dict(seconds=time.time()-start, command=cmd)
        dump(mp, m)
    try:
        if 'snapshot' not in m['stages']:
            dump(w/'progress.json', dict(stage='snapshot', time=time.time()))
            listing = []
            for src, fields in source_files:
                before = src.stat()
                dest = w/'source'/src.relative_to(SOURCE)
                dest.parent.mkdir(parents=True, exist_ok=True)
                identity = verified_copy(src, dest)
                after = src.stat()
                assert (before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns)
                listing.append(dict(path=str(src), relative_path=src.relative_to(SOURCE).as_posix(),
                                    fields=fields, mtime_ns=after.st_mtime_ns, **identity))
            dump(w/'source-manifest.json', listing)
            m['stages']['snapshot'] = dict(files=len(listing), bytes=sum(x['bytes'] for x in listing))
            dump(mp, m)
        corpus = w/'corpus'
        corpus.mkdir(exist_ok=True)
        temp = w/'kenlm-temp'
        temp.mkdir(exist_ok=True)
        if 'preprocess' not in m['stages']:
            dump(w/'progress.json', dict(stage='preprocess', time=time.time()))
            cm = dict(config=config, source_manifest_sha256=sha(w/'source-manifest.json'))
            preprocess(argparse.Namespace(source=w/'source', output=corpus, work=temp,
                       workers=11, reserve_gib=30, exclude_news=not a.news_only, only_news=a.news_only, exclude_cases=list(CASES.values())), cm)
            m['stages']['preprocess'] = cm['preprocess']
            m['corpus_files'] = {name: dict(bytes=(corpus/name).stat().st_size, sha256=sha(corpus/name))
                                 for name in ('train.tokens', 'heldout.jsonl')}
            dump(mp, m)
        run('lmplz', [KENLM/'lmplz', '-o', '5', '-S', '8G', '-T', temp,
                      '--text', corpus/'train.tokens', '--arpa', w/'char5.arpa', '--prune', '0', '0', '1', '1', '1'])
        if 'canonical' not in m['stages']:
            dump(w/'progress.json', dict(stage='canonical_arpa', time=time.time()))
            m['counts'] = canonical_arpa(w/'char5.arpa', w/'char5-tcs.arpa')
            m['stages']['canonical'] = dict(bos_context_sentinel=0)
            dump(mp, m)
        run('convert', [CONVERTER, w/'char5-tcs.arpa', w/'model-q16.bin', w/'tcs-temp', '--preserve-all'])
        run('quantize', [QUANTIZER, w/'model-q16.bin', w/'sentence-fivegram-mobile.bin'])
        for name in ('model-q16.bin', 'sentence-fivegram-mobile.bin'):
            assert header(w/name)['counts'] == m['counts']
        reader = w/'reader/lua'
        reader.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(HERE/'TcsQ8/tiger_sentence_fivegram.lua', reader/'tiger_sentence_fivegram.lua')
        run('validate', ['python3', HERE/'TcsQ8/validate.py', w/'model-q16.bin',
                         w/'sentence-fivegram-mobile.bin', w/'reader', w/'validation'])
        held = [json.loads(line) for line in (corpus/'heldout.jsonl').read_text().splitlines()]
        assert held
        (corpus/'heldout.tokens').write_text(''.join(r['tokens']+'\n' for r in held))
        (w/'score.lua').write_text(SCORE_LUA)
        run('heldout', ['luajit', w/'score.lua', HERE/'TcsQ8', w/'sentence-fivegram-mobile.bin',
                        corpus/'heldout.tokens', w/'heldout-scores.tsv'])
        scores = [tuple(map(float, line.split())) for line in (w/'heldout-scores.tsv').read_text().splitlines()]
        assert len(scores) == len(held) and all(math.isfinite(s) and n > 0 for s, n in scores)
        from TcsQ8.validate import Model
        model = Model(w/'sentence-fivegram-mobile.bin')
        vocab = set(model.tokens)
        model.data.close(); model.file.close()
        m['heldout'] = dict(rows=len(scores), perplexity=math.exp(-sum(s for s, n in scores)/sum(n for s, n in scores)),
                            oov=sum(t not in vocab for r in held for t in r['tokens'].split()))
        dump(mp, m)
        dump(w/'progress.json', dict(stage='evaluate', workers=12, time=time.time()))
        def evaluate(pair):
            name, cases = pair
            dest = w/'eval'/name
            if (dest/'summary.json').exists():
                return
            cmd = ['python3', str(HERE/'evaluate_external_shape.py'), '--cases', str(cases),
                   '--fixture', '/home/yc/tmp/tiger-shape-direct5/fixture', '--model', str(w/'sentence-fivegram-mobile.bin'),
                   '--output', str(dest), '--tcs-reader-root', str(w/'reader'), '--workers', '4']
            with (w/('eval-'+name+'.log')).open('w') as log:
                subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, check=True)
            print(name, (dest/'summary.json').read_text(), flush=True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
            list(pool.map(evaluate, CASES.items()))
        m['evaluations'] = {name: json.loads((w/'eval'/name/'summary.json').read_text()) for name in CASES}
        m['tools'] = {str(p): sha(p) for p in (Path(__file__), HERE/'train_brightmart.py', KENLM/'lmplz', CONVERTER, QUANTIZER)}
        m['finished'] = time.time()
        dump(mp, m)
        dump(w/'progress.json', dict(stage='training_and_evaluation_complete', time=time.time()))
        print('TRAINING AND EVALUATION COMPLETE; report/archive pending', flush=True)
    except BaseException as exc:
        dump(w/'progress.json', dict(stage='failed', error=str(exc), time=time.time()))
        raise


if __name__ == '__main__':
    main()
