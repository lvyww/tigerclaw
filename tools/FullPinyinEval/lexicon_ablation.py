"""Fixed-parameter lexicon replacement experiment; no dev/test retuning."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import uuid
import analyze as metrics
from run import win


def write_rows(path, rows):
    temporary = path.with_suffix(path.suffix + '.tmp')
    expected = hashlib.sha256()
    with temporary.open('wb') as stream:
        for row in rows:
            line = (json.dumps(row, ensure_ascii=False) + '\n').encode()
            expected.update(line)
            stream.write(line)
        stream.flush()
        os.fsync(stream.fileno())
    assert metrics.digest(temporary) == expected.hexdigest()
    if path.exists() and metrics.digest(path) != expected.hexdigest():
        raise ValueError(f'existing output changed: {path}')
    os.replace(temporary, path)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('stage', choices=['decode', 'qwen', 'bench'])
    p.add_argument('root', type=Path)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--model', type=Path, required=True)
    p.add_argument('--host', type=Path, required=True)
    p.add_argument('--gguf', type=Path, required=True)
    args = p.parse_args()
    root, base = args.root.resolve(), args.baseline.resolve()
    root.mkdir(exist_ok=True)
    table = root / 'wanxiang-fullpinyin.txt'
    exe = root / 'bin-final/TigerClaw.Core.Tests.exe'
    shutil.copytree(base / 'bin-final', exe.parent, dirs_exist_ok=True)
    (root / 'data').mkdir(exist_ok=True)
    data = root / 'data/cases.jsonl'
    if data.exists() and metrics.digest(data) != metrics.digest(base / 'data/cases.jsonl'):
        raise ValueError('frozen input changed')
    shutil.copy2(base / 'data/cases.jsonl', data)
    fingerprints = dict(table=metrics.digest(table), model=metrics.digest(args.model),
                        cases=metrics.digest(data), executable=metrics.digest(exe.with_suffix('.dll')),
                        host=metrics.digest(args.host), gguf=metrics.digest(args.gguf),
                        beam=200, alpha=.45, processes=4, threadsPerDecodeProcess=3)
    manifest_path = root / 'experiment.json'
    if manifest_path.exists() and json.loads(manifest_path.read_text()) != fingerprints:
        raise ValueError('experiment fingerprint changed')
    manifest_path.write_text(json.dumps(fingerprints, indent=2))
    cases = metrics.indexed(metrics.read(data))

    def run(name, command):
        started = time.monotonic()
        print('START', name, flush=True)
        with (root / (name + '.log')).open('a') as log:
            log.write('\nCOMMAND ' + json.dumps(list(map(str, command))) + '\n')
            log.flush()
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        print('DONE', name, round(time.monotonic() - started, 1), 's', flush=True)

    if args.stage == 'decode':
        parts = root / 'parts'
        parts.mkdir(exist_ok=True)
        values = list(cases.values())
        def shard(worker):
            source, output = parts / f'cases-{worker}.jsonl', parts / f'test-{worker}.jsonl'
            write_rows(source, values[worker::4])
            run(f'parts/decode-{worker}', [str(exe), 'decode', win(table), win(args.model),
                win(source), win(output), '200', '3', 'words', 'test'])
            return output
        with ThreadPoolExecutor(max_workers=4) as pool:
            paths = list(pool.map(shard, range(4)))
        # Validate using stripped rows, then stream full traces for bounded memory.
        rows = [r for path in paths for r in metrics.decode_rows(path)]
        metrics.validate(cases, rows, 'test')
        write_rows(root / 'test-words.jsonl',
                   (json.loads(line) for path in paths for line in path.open()))
        metadata = {str(path.relative_to(root)): json.loads(Path(str(path)+'.manifest.json').read_text())
                    for path in paths}
        metadata['mergedSha256'] = metrics.digest(root / 'test-words.jsonl')
        (root / 'test-words.jsonl.manifest.json').write_text(json.dumps(metadata, indent=2))
        print(json.dumps(metrics.accuracy(rows)), flush=True)
    elif args.stage == 'qwen':
        decode = metrics.indexed(metrics.decode_rows(root / 'test-words.jsonl'))
        metrics.validate(cases, list(decode.values()), 'test')
        old_decode = metrics.indexed(metrics.decode_rows(base / 'test-words.jsonl'))
        old_paths = sorted(base.glob('qwen-test-*.jsonl'))
        for path in old_paths:
            m = json.loads(Path(str(path) + '.manifest.json').read_text())
            for key in ['host', 'gguf', 'executable']:
                assert m[key].lower() == fingerprints[key]
            assert m['input'].lower() == metrics.digest(base / 'test-words.jsonl')
        old_qwen = metrics.qwen_rows(old_paths, old_decode)
        # Reuse only the EXACT ordered text batch with identical scorer/model.
        # Never mix individual cached scores across batches: rounding may differ.
        cache = {tuple(c['text'] for c in row['candidates'][:10]): old_qwen[i]
                 for i, row in old_decode.items()}
        cached, pending = [], []
        for i, row in decode.items():
            key = tuple(c['text'] for c in row['candidates'][:10])
            if key in cache:
                item = dict(cache[key])
                item['id'] = i
                item['reusedFromBaselineId'] = cache[key]['id']
                cached.append(item)
            else:
                pending.append(row)
        write_rows(root / 'qwen-cached.jsonl', cached)
        source = root / 'qwen-changed-input.jsonl'
        write_rows(source, pending)
        print(f'Qwen exact-batch reused={len(cached)}, recompute={len(pending)}', flush=True)
        def worker(i):
            out = root / f'qwen-changed-{i}.jsonl'
            run(f'qwen-changed-{i}', [str(exe), 'qwen', win(source), win(out),
                                    win(args.host), win(args.gguf), str(i), '4'])
            return out
        with ThreadPoolExecutor(max_workers=4) as pool:
            paths = list(pool.map(worker, range(4)))
        qwen = metrics.qwen_rows([root / 'qwen-cached.jsonl', *paths], decode)
        write_rows(root / 'qwen-all.jsonl', [qwen[i] for i in decode])
        (root / 'qwen-reuse.json').write_text(json.dumps(dict(
            exactBatchesReused=len(cached), recomputed=len(pending),
            baseline=str(base), sourceSha256=metrics.digest(source)), indent=2))
        score = metrics.winners(decode, qwen, .45)
        print('fixed-alpha fusedTop1', sum(score[i] == cases[i]['text'] for i in score) / len(score))
    else:
        stop = root / f'monitor-{uuid.uuid4().hex}.stop'
        monitor = subprocess.Popen([
            '/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe',
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            win(Path(__file__).with_name('monitor.ps1')),
            '-ExperimentDirectory', win(root), '-HostExecutable', win(args.host),
            '-StopFile', win(stop)])
        try:
            run('latency', [str(exe), 'bench', win(table), win(args.model), win(data),
                            win(root / 'latency.jsonl'), '200'])
        finally:
            stop.touch()
            monitor.wait(timeout=15)


if __name__ == '__main__':
    main()
