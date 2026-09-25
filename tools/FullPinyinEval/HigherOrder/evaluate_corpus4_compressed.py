"""Evaluate a size-selected compressed corpus4 model against frozen predictions."""
import argparse, concurrent.futures, csv, hashlib, json, os, subprocess
from pathlib import Path


def sha(p):
    with p.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selected', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--method', default='corpus4_compressed')
    parser.add_argument('--extra-baseline', type=Path)
    parser.add_argument('--max-model-bytes', type=int, default=500_000_000)
    parser.add_argument('--mix-old-model', type=Path)
    parser.add_argument('--mix-old-weight', type=float)
    parser.add_argument('--mix-native', type=Path)
    parser.add_argument('--tcs-reader-root', type=Path)
    args = parser.parse_args()
    selected = json.loads(args.selected.read_text())
    model = Path(selected['model'])
    assert model.stat().st_size == selected['bytes'] <= args.max_model_bytes
    assert sha(model) == selected['sha256']
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    local = Path('/home/yc/tmp/tiger-shape-direct5')
    fixture = local / 'fixture'
    here = Path(__file__).resolve().parent
    cases = args.baseline / 'cases.tsv'
    old_manifest = json.loads((args.baseline / 'manifest.json').read_text())
    assert sha(cases) == old_manifest['cases_sha256']
    assert sha(fixture / 'lua/tiger_sentence.lua') == old_manifest['methods']['corpus4_fivegram']['decoder_sha256']
    assert sha(local / 'shape5.so') == old_manifest['native_sha256']
    assert sha(here / 'export_shape_pool.lua') == old_manifest['exporter_sha256']
    env = dict(os.environ, SHAPE5_MODEL=str(model), SHAPE5_LIB=str(local / 'shape5.so'))
    native = local / 'shape5.so'
    mix = None
    if args.mix_old_model is not None:
        assert args.mix_native and args.mix_old_weight is not None and 0 <= args.mix_old_weight <= 1
        native = args.mix_native
        env.update(SHAPE5_LIB=str(native), SHAPE5_OLD_MODEL=str(args.mix_old_model),
                   SHAPE5_OLD_WEIGHT=str(args.mix_old_weight))
        mix = dict(old_model=str(args.mix_old_model), old_sha256=sha(args.mix_old_model),
                   old_weight=args.mix_old_weight, formula='log(w*exp(old_logp)+(1-w)*exp(new_logp))')
    else:
        assert args.mix_native is None and args.mix_old_weight is None
    adapter = here / 'shape5_tcs03_adapter.lua' if args.tcs_reader_root else None
    launcher = ['lua']
    if adapter:
        assert args.mix_old_model is None
        env.update(SHAPE5_LIB='tcs03-offline', TCS03_READER_ROOT=str(args.tcs_reader_root))
        launcher.append(str(adapter))
    test = subprocess.run(launcher + [str(here / 'test_shape5.lua'), str(fixture)], env=env,
                          text=True, capture_output=True, check=True)
    (out / 'validation.log').write_text(test.stdout + test.stderr)
    print(test.stdout, flush=True)
    lines = cases.read_text().splitlines()
    def worker(i):
        part = out / f'cases-{i}.tsv'
        part.write_text('\n'.join(lines[i::8]) + '\n')
        with (out / f'worker-{i}.log').open('w') as f:
            subprocess.run(launcher + [str(here / 'export_shape_pool.lua'), str(fixture), str(part),
                            str(out / f'pool-{i}.tsv')], env=env, stdout=f, stderr=subprocess.STDOUT, check=True)
        print('worker complete', i, flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        list(executor.map(worker, range(8)))
    groups = {}
    for i in range(8):
        with (out / f'pool-{i}.tsv').open() as f:
            for r in csv.DictReader(f, delimiter='\t'):
                groups.setdefault(r['id'], []).append(r)
    rows = [json.loads(s) for s in (args.baseline / 'predictions.jsonl').read_text().splitlines()]
    assert len(rows) == len(groups) == 20000
    if args.extra_baseline:
        extra = {r['id']: r for r in (json.loads(line) for line in args.extra_baseline.read_text().splitlines())}
        assert len(extra) == len(rows)
        for r in rows:
            other = extra[r['id']]
            assert all(r[k] == other[k] for k in ('source', 'code', 'target'))
            for method, prediction in other['predictions'].items():
                if method in r['predictions']:
                    assert r['predictions'][method] == prediction
                    assert r['target_ranks'][method] == other['target_ranks'][method]
                else:
                    r['predictions'][method] = prediction
                    r['target_ranks'][method] = other['target_ranks'][method]
    name = args.method
    assert name not in rows[0]['predictions']
    baselines = list(rows[0]['predictions'])
    for r in rows:
        pool = groups[r['id']]
        assert all(pool[0][k] == r[k] for k in ('source', 'code', 'target'))
        r['predictions'][name] = pool[0]['text']
        r['target_ranks'][name] = next((int(c['index']) for c in pool if c['text'] == r['target']), 0)
    summary = {}
    for source in ('old', 'fresh', 'combined'):
        subset = [r for r in rows if source == 'combined' or r['source'] == source]
        stats = {'n': len(subset), 'methods': {}, 'comparisons': {}}
        for method in baselines + [name]:
            stats['methods'][method] = { 'correct': sum(r['predictions'][method] == r['target'] for r in subset),
                'top5': sum(0 < r['target_ranks'][method] <= 5 for r in subset),
                'top20': sum(0 < r['target_ranks'][method] <= 20 for r in subset)}
        for method in baselines:
            improved = sum(r['predictions'][name] == r['target'] and r['predictions'][method] != r['target'] for r in subset)
            regressed = sum(r['predictions'][name] != r['target'] and r['predictions'][method] == r['target'] for r in subset)
            stats['comparisons'][method] = dict(improved=improved, regressed=regressed, net=improved-regressed)
        summary[source] = stats
    with (out / 'predictions.jsonl').open('w') as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    for method in baselines:
        with (out / f'changes-vs-{method}.tsv').open('w') as f:
            writer = csv.writer(f, delimiter='\t')
            writer.writerow(('id', 'source', 'code', 'target', 'before', 'after', 'change'))
            for r in rows:
                before, after = r['predictions'][method], r['predictions'][name]
                if before != after:
                    writer.writerow((r['id'],r['source'],r['code'],r['target'],before,after,
                                     'improved' if after==r['target'] else 'regressed' if before==r['target'] else 'both_differ'))
    (out / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
    manifest = dict(selected=selected, baseline=str(args.baseline),
                    baseline_predictions_sha256=sha(args.baseline / 'predictions.jsonl'),
                    cases_sha256=sha(cases), native_sha256=sha(native), mixture=mix,
                    decoder_sha256=sha(fixture / 'lua/tiger_sentence.lua'),
                    script_sha256=sha(Path(__file__)), latency_evaluated=False,
                    production_changed=False, llm=False, learning=False)
    manifest['method'] = name
    if adapter:
        manifest.update(native_used=False, adapter_sha256=sha(adapter),
                        tcs_reader_sha256=sha(args.tcs_reader_root / 'lua/tiger_sentence_fivegram.lua'))
    if args.extra_baseline:
        manifest['extra_baseline'] = str(args.extra_baseline)
        manifest['extra_baseline_sha256'] = sha(args.extra_baseline)
    (out / 'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps(summary,ensure_ascii=False,indent=2),flush=True)


if __name__ == '__main__':
    main()
