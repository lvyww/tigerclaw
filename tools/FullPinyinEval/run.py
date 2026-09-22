"""WSL driver for the isolated Windows experiment; outputs stay outside Git."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import subprocess
import sys
import time
import hashlib
import os
import atexit
import uuid
import analyze as metrics


def win(path):
    return subprocess.check_output(["wslpath", "-w", str(Path(path).resolve())], text=True).strip()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("root", type=Path)
    p.add_argument("--exe", required=True, type=Path)
    p.add_argument("--table", required=True, type=Path)
    p.add_argument("--model", required=True, type=Path)
    p.add_argument("--host", required=True, type=Path)
    p.add_argument("--gguf", required=True, type=Path)
    p.add_argument("--jobs", type=int, default=12)
    p.add_argument("--qwen-workers", type=int, default=3)
    args = p.parse_args()
    if not 4 <= args.jobs <= 32:
        p.error("--jobs must be 4..32")
    if not 1 <= args.qwen_workers <= 8:
        p.error("--qwen-workers must be 1..8 (each scorer uses four threads)")
    root = args.root.resolve()
    scripts = Path(__file__).resolve().parent
    if (root / "STOP").exists():
        raise SystemExit("Remove the experiment STOP file to explicitly resume")
    monitor_stop = root / f"memory-monitor-{uuid.uuid4().hex}.stop"
    monitor = subprocess.Popen(["/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", win(scripts / "monitor.ps1"), "-ExperimentDirectory", win(root), "-HostExecutable", win(args.host),
        "-StopFile", win(monitor_stop)])
    def finish_monitor():
        monitor_stop.touch()
        monitor.wait(timeout=10)
    atexit.register(finish_monitor)

    def run(name, command):
        started = time.monotonic()
        print("START", name, flush=True)
        with (root / f"{name}.log").open("a") as log:
            log.write("\nCOMMAND " + json.dumps(list(map(str, command))) + "\n")
            log.flush()
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
        print("DONE", name, round(time.monotonic()-started, 1), "s", flush=True)

    # Separate processes avoid sharing a managed heap between wide Beam searches.
    # Shards depend only on source order, never on expected accuracy or difficulty.
    partdir = root / "parts"
    partdir.mkdir(exist_ok=True)
    source_lines = (root / "data/cases.jsonl").read_text().splitlines(keepends=True)
    for worker in range(args.jobs):
        target = partdir / f"cases-{worker}.jsonl"
        content = "".join(source_lines[worker::args.jobs])
        if target.exists() and target.read_text() != content:
            raise ValueError("Shard fingerprint changed; use a new experiment directory")
        target.write_text(content)

    def decode(name, beam, words, split):
        def shard(worker):
            output = partdir / f"{name}-{worker}.jsonl"
            run(f"parts/{name}-{worker}", [str(args.exe), "decode", win(args.table), win(args.model),
                       win(partdir / f"cases-{worker}.jsonl"), win(output), str(beam), "1", words, split])
            return output
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            paths = list(pool.map(shard, range(args.jobs)))
        rows = [r for path in paths for r in metrics.read(path)]
        metrics.validate(metrics.indexed(metrics.read(root / "data/cases.jsonl")), rows, split)
        output = root / f"{name}.jsonl"
        temporary = Path(str(output) + ".tmp")
        checksum = hashlib.sha256()
        with temporary.open("wb") as stream:
            for row in sorted(rows, key=lambda r: r["id"]):
                line = (json.dumps(row, ensure_ascii=False) + "\n").encode()
                checksum.update(line)
                stream.write(line)
            stream.flush()
            os.fsync(stream.fileno())
        expected = checksum.hexdigest()
        if metrics.digest(temporary) != expected:
            raise IOError("Merged file failed read-back checksum; shards are preserved")
        if output.exists() and metrics.digest(output) != expected:
            raise ValueError("Completed merged output changed; preserve and diagnose before rebuilding")
        os.replace(temporary, output)
        manifest = {str(path.relative_to(root)): json.loads(Path(str(path) + ".manifest.json").read_text()) for path in paths}
        manifest["mergedSha256"] = expected
        Path(str(output) + ".manifest.json").write_text(json.dumps(manifest, indent=2))

    def analyze(*flags):
        subprocess.run([sys.executable, str(scripts / "analyze.py"), str(root), *flags], check=True)

    def qwen(split, source):
        existing = sorted(root.glob(f"qwen-{split}-*.jsonl"))
        if existing:
            for path in existing:
                manifest = json.loads(Path(str(path) + ".manifest.json").read_text())
                if manifest["input"] != metrics.digest(source).upper() or manifest["host"] != metrics.digest(args.host).upper() or manifest["gguf"] != metrics.digest(args.gguf).upper():
                    raise ValueError("Existing Qwen results use different inputs or scorer")
            try:
                metrics.qwen_rows(existing, metrics.indexed(metrics.decode_rows(source)))
                print("ALREADY COMPLETE", "qwen-" + split, flush=True)
                return
            except ValueError as error:
                if str(error) != "Incomplete Qwen run":
                    raise
            if any(json.loads(Path(str(path) + ".manifest.json").read_text())["workers"] != args.qwen_workers for path in existing):
                raise ValueError("Resume an incomplete Qwen stage with its original worker count")
        with ThreadPoolExecutor(max_workers=args.qwen_workers) as pool:
            futures = [pool.submit(run, f"qwen-{split}-{i}", [str(args.exe), "qwen", win(source),
                       win(root / f"qwen-{split}-{i}.jsonl"), win(args.host), win(args.gguf), str(i), str(args.qwen_workers)])
                       for i in range(args.qwen_workers)]
            for future in futures:
                future.result()

    for beam in [200, 500, 1000, 2000]:
        decode(f"dev-{beam}", beam, "words", "dev")
    analyze("--select-beam")
    beam = json.loads((root / "beam-selection.json").read_text())["beam"]
    qwen("dev", root / f"dev-{beam}.jsonl")
    analyze("--select-alpha")  # Freeze alpha before accessing held-out results.
    decode("test-words", beam, "words", "test")
    decode("test-singles", beam, "singles", "test")
    qwen("test", root / "test-words.jsonl")
    # Measure incremental/backspace latency without other experiment workers.
    run("latency", [str(args.exe), "bench", win(args.table), win(args.model), win(root / "data/cases.jsonl"),
                    win(root / "latency.jsonl"), str(beam)])
    finish_monitor()  # Freeze the memory log before the report reads it.
    analyze()
    alpha = json.loads((root / "alpha-selection.json").read_text())["alpha"]
    subprocess.run([sys.executable, str(scripts / "fuse.py"), str(root / "test-words.jsonl"),
                    str(root / "test-fused.jsonl"), str(alpha),
                    *map(str, sorted(root.glob("qwen-test-*.jsonl")))], check=True)


if __name__ == "__main__":
    main()
