#!/usr/bin/env python3
"""Benchmark C# and Rust TigerClaw Core sentence n-gram latency on Windows."""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import statistics
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any, Sequence

from compare_core_key_trace import LineReader, parse_observation, send, start, stop


def percentile(values: Sequence[float], fraction: float) -> float:
    ordered = sorted(values)
    if not ordered:
        return 0.0
    return ordered[max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))]


def summary(values: Sequence[float]) -> dict[str, float | int]:
    return {
        "count": len(values),
        "mean_ms": round(statistics.fmean(values), 3) if values else 0.0,
        "p50_ms": round(percentile(values, 0.50), 3),
        "p95_ms": round(percentile(values, 0.95), 3),
        "max_ms": round(max(values, default=0.0), 3),
    }


def windows_path(path: Path) -> str:
    if os.name == "nt":
        return str(path)
    return subprocess.run(
        ["wslpath", "-w", str(path)], check=True, text=True, capture_output=True
    ).stdout.strip()


def prepare_root(root: Path, schema: Path, model: Path) -> None:
    schema_target = root / "码表" / "虎整句"
    schema_target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(schema, schema_target)
    model_target = root / "Models" / "sentence-ngram-v2.bin"
    model_target.parent.mkdir(parents=True, exist_ok=True)
    try:
        os.link(model, model_target)
    except OSError:
        shutil.copy2(model, model_target)
    config = """# TigerClaw n-gram latency benchmark
码表存储位置\t码表
当前码表\t虎整句
默认中文\t是
中文状态下使用英文标点\t否
shift切换中英文\t是
Ctrl+空格切换中英文\t是
回车清屏\t否
TAB清屏\t是
每页候选个数\t5
翻页键\t- =
分号次选\t是
引号三选\t是
/输出顿号\t是
隐藏候选\t否
候选窗显示编码\t是
空码自动清屏\t是
最大码长\t4
中英文不限长混合输入\t否
整句输入\t是
自动启用整句模式\t是
整句神经重排\t否
整句自动提前上屏\t否
最大码长无重自动上屏\t否
开启打字音效(娱乐)\t否
"""
    (root / "config.txt").write_text(config, encoding="utf-8", newline="\r\n")


def select_cases(pools_path: Path) -> list[dict[str, Any]]:
    pools = json.loads(pools_path.read_text(encoding="utf-8"))
    eligible = [
        item for item in pools
        if isinstance(item, dict)
        and isinstance(item.get("code"), str)
        and item["code"].isalpha()
        and item["code"].isascii()
    ]
    chosen: list[dict[str, Any]] = [
        {"text": "今天你们", "code": "jaefmjxj", "source": "handcrafted-regression"}
    ]
    for target in (16, 32, 48, 64):
        item = min(eligible, key=lambda value: abs(len(value["code"]) - target))
        chosen.append({
            "text": item.get("text", ""),
            "code": item["code"],
            "source": item.get("source", ""),
        })
        eligible.remove(item)
    return chosen


class Adapter:
    def __init__(self, label: str, command: Sequence[str], timeout: float) -> None:
        self.label = label
        self.timeout = timeout
        started = time.perf_counter()
        self.process, self.reader = start(command)
        self.sequence = 1
        observation, _ = self.exchange({"type": "query_state", "seq": self.sequence})
        self.startup_ms = (time.perf_counter() - started) * 1000.0
        if not isinstance(observation.get("snapshot"), dict):
            raise RuntimeError(f"{label} did not return a startup snapshot")

    def exchange(self, message: dict[str, Any]) -> tuple[dict[str, Any], float]:
        line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
        started = time.perf_counter()
        send(self.process, line)
        raw = self.reader.wait(self.timeout, self.label)
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        return parse_observation(raw, self.label), elapsed_ms

    def wait_idle(self) -> tuple[dict[str, Any], float]:
        return self.exchange({"_diff": "wait_idle"})

    def clear(self) -> None:
        self.exchange({"type": "composition_canceled"})
        self.wait_idle()

    def key(self, character: str, event_id: int) -> tuple[dict[str, Any], float]:
        self.sequence += 1
        return self.exchange({
            "type": "key",
            "seq": self.sequence,
            "client_session": "ngram-latency-benchmark",
            "event_id": f"{self.label}-{event_id}",
            "action": "down",
            "vk": ord(character.upper()),
            "scan": 0,
            "shift": False,
            "ctrl": False,
            "alt": False,
            "win": False,
            "capsLock": False,
            "numLock": True,
            "repeat": 1,
            "extended": False,
            "tsf_stage": "test_keydown",
        })

    def close(self) -> None:
        stop(self.process)


def type_burst(adapter: Adapter, code: str, event_seed: int) -> tuple[list[float], float, dict[str, Any]]:
    adapter.clear()
    key_ms: list[float] = []
    for index, character in enumerate(code):
        _, elapsed = adapter.key(character, event_seed + index)
        key_ms.append(elapsed)
    final, settle_ms = adapter.wait_idle()
    return key_ms, settle_ms, final


def type_paced(adapter: Adapter, code: str, event_seed: int) -> tuple[list[float], list[float], dict[str, Any]]:
    adapter.clear()
    key_ms: list[float] = []
    decode_ms: list[float] = []
    final: dict[str, Any] = {}
    for index, character in enumerate(code):
        _, elapsed = adapter.key(character, event_seed + index)
        key_ms.append(elapsed)
        final, elapsed = adapter.wait_idle()
        if index >= 4:
            decode_ms.append(elapsed)
    return key_ms, decode_ms, final


def benchmark_adapter(
    adapter: Adapter,
    cases: Sequence[dict[str, Any]],
    burst_rounds: int,
    paced_rounds: int,
) -> dict[str, Any]:
    # Fault in model pages and warm the decoder's fixed-size transition caches.
    for case_index, case in enumerate(cases):
        type_burst(adapter, case["code"], 1_000_000 + case_index * 1000)

    burst_results: list[dict[str, Any]] = []
    paced_results: list[dict[str, Any]] = []
    for case_index, case in enumerate(cases):
        burst_keys: list[float] = []
        burst_settle: list[float] = []
        burst_final: dict[str, Any] = {}
        for round_index in range(burst_rounds):
            keys, settle, burst_final = type_burst(
                adapter, case["code"], 2_000_000 + case_index * 100_000 + round_index * 1000
            )
            burst_keys.extend(keys)
            burst_settle.append(settle)
        burst_results.append({
            **case,
            "code_length": len(case["code"]),
            "key_response": summary(burst_keys),
            "settle": summary(burst_settle),
            "top_candidates": burst_final.get("snapshot", {}).get("candidates", [])[:5],
        })

        paced_keys: list[float] = []
        paced_decodes: list[float] = []
        paced_final_append: list[float] = []
        paced_final: dict[str, Any] = {}
        for round_index in range(paced_rounds):
            keys, decodes, paced_final = type_paced(
                adapter, case["code"], 4_000_000 + case_index * 100_000 + round_index * 1000
            )
            paced_keys.extend(keys)
            paced_decodes.extend(decodes)
            if decodes:
                paced_final_append.append(decodes[-1])
        paced_results.append({
            **case,
            "code_length": len(case["code"]),
            "key_response": summary(paced_keys),
            "all_incremental_decodes": summary(paced_decodes),
            "final_append_decode": summary(paced_final_append),
            "top_candidates": paced_final.get("snapshot", {}).get("candidates", [])[:5],
        })

    return {
        "startup_ms": round(adapter.startup_ms, 3),
        "burst": burst_results,
        "paced": paced_results,
    }


def ratio(rust: float, csharp: float) -> float | None:
    return round(rust / csharp, 3) if csharp > 0 else None


def comparisons(csharp: dict[str, Any], rust: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    for scenario, metric in (("burst", "settle"), ("paced", "final_append_decode")):
        for left, right in zip(csharp[scenario], rust[scenario]):
            result.append({
                "scenario": scenario,
                "code_length": left["code_length"],
                "code": left["code"],
                "csharp_p50_ms": left[metric]["p50_ms"],
                "rust_p50_ms": right[metric]["p50_ms"],
                "rust_over_csharp": ratio(right[metric]["p50_ms"], left[metric]["p50_ms"]),
                "candidates_equal": left["top_candidates"] == right["top_candidates"],
            })
    return result


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csharp", type=Path, required=True)
    parser.add_argument("--rust", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    parser.add_argument("--pools", type=Path, required=True)
    parser.add_argument("--burst-rounds", type=int, default=10)
    parser.add_argument("--paced-rounds", type=int, default=5)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    for path in (args.csharp, args.rust, args.model, args.schema, args.pools):
        if not path.exists():
            parser.error(f"not found: {path}")
    if min(args.burst_rounds, args.paced_rounds) < 1:
        parser.error("round counts must be positive")

    cases = select_cases(args.pools)
    temporary = tempfile.TemporaryDirectory(prefix="tigerclaw-ngram-bench-", dir=str(Path.cwd()))
    work_root = Path(temporary.name)
    csharp_root, rust_root = work_root / "csharp", work_root / "rust"
    prepare_root(csharp_root, args.schema.resolve(), args.model.resolve())
    prepare_root(rust_root, args.schema.resolve(), args.model.resolve())

    csharp_adapter: Adapter | None = None
    rust_adapter: Adapter | None = None
    try:
        # Start both before warmup so neither measured run includes process/model setup.
        csharp_adapter = Adapter(
            "C#",
            [str(args.csharp.resolve()), "--core-diff-stdio", windows_path(csharp_root.resolve())],
            args.timeout,
        )
        rust_adapter = Adapter(
            "Rust",
            [str(args.rust.resolve()), "--diff-stdio", "--root", windows_path(rust_root.resolve())],
            args.timeout,
        )
        print("benchmarking C#...", flush=True)
        csharp = benchmark_adapter(csharp_adapter, cases, args.burst_rounds, args.paced_rounds)
        print("benchmarking Rust...", flush=True)
        rust = benchmark_adapter(rust_adapter, cases, args.burst_rounds, args.paced_rounds)
    finally:
        if csharp_adapter:
            csharp_adapter.close()
        if rust_adapter:
            rust_adapter.close()
        temporary.cleanup()

    report = {
        "format": "tigerclaw.core.ngram-latency.v1",
        "model": str(args.model.resolve()),
        "model_bytes": args.model.stat().st_size,
        "schema": str(args.schema.resolve()),
        "neural_rerank": False,
        "early_commit": False,
        "burst_rounds": args.burst_rounds,
        "paced_rounds": args.paced_rounds,
        "cases": cases,
        "csharp": csharp,
        "rust": rust,
        "comparison": comparisons(csharp, rust),
    }
    text = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(text, encoding="utf-8")
    print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
