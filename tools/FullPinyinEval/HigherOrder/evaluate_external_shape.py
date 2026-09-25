"""Evaluate frozen external cases with existing shape decoder/model adapters."""
import argparse
import concurrent.futures
import csv
import json
import os
import subprocess
from pathlib import Path
from prepare_thucnews_shape import sha


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--cases', type=Path, required=True)
    p.add_argument('--fixture', type=Path, required=True)
    p.add_argument('--model', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--tcs-reader-root', type=Path)
    p.add_argument('--trigram', action='store_true')
    p.add_argument('--mix-articles-model', type=Path)
    p.add_argument('--mix-articles-weight', type=float)
    p.add_argument('--mix-corpus-format', choices=('kenlm', 'tcs03'), default='kenlm')
    p.add_argument('--workers', type=int, default=8)
    p.add_argument('--native-lib', type=Path, help='Offline alternative shape5 ABI scorer')
    p.add_argument('--model-identity', type=Path, help='Manifest of descriptor-referenced model files')
    args = p.parse_args()
    if args.native_lib:
        assert args.model_identity and not args.tcs_reader_root and not args.trigram and not args.mix_articles_model
    if args.mix_articles_model:
        assert args.tcs_reader_root and not args.trigram
        assert args.mix_articles_weight is not None and 0 <= args.mix_articles_weight <= 1
    else:
        assert args.mix_articles_weight is None
    here = Path(__file__).resolve().parent
    args.output.mkdir(parents=True, exist_ok=True)
    lines = args.cases.read_text().splitlines()
    expected = {line.split('\t')[0]: line.split('\t')[1:] for line in lines}
    assert len(expected) == len(lines)
    env = dict(os.environ, SHAPE5_MODEL=str(args.model),
               SHAPE5_LIB=str(args.native_lib) if args.native_lib else '/home/yc/tmp/tiger-shape-direct5/shape5.so')
    launcher = ['lua']
    if args.trigram:
        assert not args.tcs_reader_root
        env.pop('SHAPE5_MODEL', None)
        env.pop('SHAPE5_LIB', None)
        assert sha(args.fixture / 'models/sentence-ngram-mobile.bin') == sha(args.model)
    if args.tcs_reader_root:
        env.update(SHAPE5_LIB='tcs03-offline', TCS03_READER_ROOT=str(args.tcs_reader_root))
        launcher.append(str(here / 'shape5_tcs03_adapter.lua'))
    if args.mix_articles_model:
        env.update(SHAPE5_LIB='articles-mix-offline', ARTICLES_MIX_MODEL=str(args.mix_articles_model),
                   ARTICLES_MIX_WEIGHT=str(args.mix_articles_weight),
                   CORPUS4_MIX_FORMAT=args.mix_corpus_format,
                   CORPUS4_NATIVE_LIB='/home/yc/tmp/tiger-shape-direct5/shape5.so')
        launcher = ['lua', str(here / 'shape5_articles_mix.lua')]
    manifest = dict(cases_sha256=sha(args.cases), total=len(lines),
                    model=str(args.model), model_sha256=sha(args.model),
                    model_bytes=args.model.stat().st_size,
                    decoder_sha256=sha(args.fixture / 'lua/tiger_sentence.lua'),
                    exporter_sha256=sha(here / 'export_shape_pool.lua'),
                    script_sha256=sha(Path(__file__)),
                    llm=False, learning=False, early_commit=False, production_changed=False)
    manifest['order'] = 3 if args.trigram else 5
    if args.native_lib:
        manifest['native_sha256'] = sha(args.native_lib)
        manifest['model_identity'] = json.loads(args.model_identity.read_text())
        manifest['model_identity_sha256'] = sha(args.model_identity)
    manifest['fixture_hashes'] = {str(path.relative_to(args.fixture)): sha(path)
                                for path in sorted(args.fixture.rglob('*'))
                                if path.is_file() and 'models' not in path.relative_to(args.fixture).parts}
    if args.tcs_reader_root:
        manifest['reader_sha256'] = sha(args.tcs_reader_root / 'lua/tiger_sentence_fivegram.lua')
    if args.mix_articles_model:
        manifest['mixture'] = dict(articles_model=str(args.mix_articles_model),
            articles_sha256=sha(args.mix_articles_model), articles_weight=args.mix_articles_weight,
            corpus_format=args.mix_corpus_format,
            adapter_sha256=sha(here / 'shape5_articles_mix.lua'),
            native_sha256=sha(Path(env['CORPUS4_NATIVE_LIB'])), method='per-token probability interpolation')
    (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2))
    with (args.output / 'validation.log').open('w') as log:
        subprocess.run(launcher + [str(here / 'test_shape5.lua'), str(args.fixture)],
                       env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    def worker(i):
        part = args.output / f'cases-{i}.tsv'
        part.write_text('\n'.join(lines[i::args.workers]) + '\n')
        with (args.output / f'worker-{i}.log').open('w') as log:
            subprocess.run(launcher + [str(here / 'export_shape_pool.lua'), str(args.fixture),
                           str(part), str(args.output / f'pool-{i}.tsv')],
                           env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        print('worker complete', i, flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
        list(executor.map(worker, range(args.workers)))
    groups = {}
    for i in range(args.workers):
        with (args.output / f'pool-{i}.tsv').open() as stream:
            for row in csv.DictReader(stream, delimiter='\t'):
                assert [row[k] for k in ('source', 'code', 'target')] == expected[row['id']]
                groups.setdefault(row['id'], []).append(row)
    assert groups.keys() == expected.keys()
    predictions = []
    for ident in expected:
        pool = sorted(groups[ident], key=lambda r: int(r['index']))
        source, code, target = expected[ident]
        predictions.append(dict(id=ident, source=source, code=code, target=target,
                                prediction=pool[0]['text'],
                                rank=next((int(r['index']) for r in pool if r['text'] == target), 0)))
    summary = {}
    for source in ['combined'] + sorted({r['source'] for r in predictions}):
        rows = [r for r in predictions if source == 'combined' or r['source'] == source]
        summary[source] = dict(n=len(rows), correct=sum(r['target'] == r['prediction'] for r in rows),
                               top5=sum(0 < r['rank'] <= 5 for r in rows),
                               top20=sum(0 < r['rank'] <= 20 for r in rows))
    with (args.output / 'predictions.jsonl').open('w') as stream:
        for row in predictions:
            stream.write(json.dumps(row, ensure_ascii=False) + '\n')
    (args.output / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2))
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
