#!/usr/bin/env python3
"""Use the production ONNX sidecar to rerank exported sentence candidate pools."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
from pathlib import Path
from typing import Dict, List, Optional, Sequence


def send(pipe, payload: dict) -> dict:
    pipe.write((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
    data = bytearray()
    while not data.endswith(b"\n"):
        chunk = pipe.read(1)
        if not chunk:
            raise RuntimeError("sidecar closed the pipe")
        data.extend(chunk)
    response = json.loads(data.decode("utf-8"))
    if not response.get("success"):
        raise RuntimeError(response.get("error", "sidecar request failed"))
    return response


def summarize(ranks: Sequence[Optional[int]]) -> Dict[str, float]:
    total = max(len(ranks), 1)
    result: Dict[str, float] = {
        "cases": len(ranks),
        "mrr": sum(1.0 / rank for rank in ranks if rank is not None) / total,
    }
    for cutoff in (1, 5, 10, 20):
        result[f"top_{cutoff}"] = sum(
            rank is not None and rank <= cutoff for rank in ranks
        ) / total
    return result


def evaluate(args: argparse.Namespace) -> int:
    pools = json.loads(args.pools.read_text(encoding="utf-8"))
    if args.max_cases:
        pools = pools[: args.max_cases]
    pipe_name = f"TigerClaw.Sentence.evaluate.{os.getpid()}"
    process = subprocess.Popen(
        [
            str(args.exe),
            "--parent-pid",
            str(os.getpid()),
            "--pipe",
            pipe_name,
            "--model",
            str(args.model),
            "--vocabulary",
            str(args.vocabulary),
        ]
    )
    pipe_path = rf"\\.\pipe\{pipe_name}"
    started = time.monotonic()
    try:
        deadline = time.monotonic() + 30.0
        while True:
            try:
                pipe = open(pipe_path, "r+b", buffering=0)
                break
            except FileNotFoundError:
                if process.poll() is not None:
                    raise RuntimeError(f"sidecar exited with {process.returncode}")
                if time.monotonic() >= deadline:
                    raise TimeoutError("timed out waiting for sidecar pipe")
                time.sleep(0.05)

        ranks = {weight: [] for weight in args.weights}
        details: List[dict] = []
        with pipe:
            provider = send(pipe, {"type": "hello", "seq": 1}).get("provider", "")
            for index, pool in enumerate(pools, 1):
                candidates = pool["candidates"][:20]
                response = send(
                    pipe,
                    {
                        "type": "rerank",
                        "seq": index + 1,
                        "generation": index,
                        "raw_code": pool.get("code", ""),
                        "candidates": [candidate["text"] for candidate in candidates],
                    },
                )
                neural_scores = response["scores"]
                case_detail = {
                    "text": pool["text"],
                    "code": pool.get("code", ""),
                    "source": pool.get("source", ""),
                }
                for weight in args.weights:
                    ordered = sorted(
                        zip(candidates, neural_scores),
                        key=lambda value: value[0]["base_score"] + weight * value[1],
                        reverse=True,
                    )
                    rank = next(
                        (
                            position
                            for position, (candidate, _) in enumerate(ordered, 1)
                            if candidate["text"] == pool["text"]
                        ),
                        None,
                    )
                    ranks[weight].append(rank)
                    case_detail[str(weight)] = {
                        "rank": rank,
                        "top": ordered[0][0]["text"],
                    }
                details.append(case_detail)
                if args.progress_interval and index % args.progress_interval == 0:
                    elapsed = time.monotonic() - started
                    print(f"completed={index}/{len(pools)} elapsed={elapsed:.1f}s", flush=True)

        baseline = ranks[0.0] if 0.0 in ranks else None
        weights = {}
        for weight, values in ranks.items():
            item = summarize(values)
            if baseline is not None:
                item["fixes_vs_base"] = sum(
                    rank == 1 and base_rank != 1
                    for base_rank, rank in zip(baseline, values)
                )
                item["regressions_vs_base"] = sum(
                    base_rank == 1 and rank != 1
                    for base_rank, rank in zip(baseline, values)
                )
            weights[str(weight)] = item
        report = {
            "version": 1,
            "provider": provider,
            "model": str(args.model.resolve()),
            "pools": str(args.pools.resolve()),
            "weights": weights,
            "details": details,
            "elapsed_seconds": time.monotonic() - started,
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(json.dumps(weights, ensure_ascii=False, indent=2))
        return 0
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=10)


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pools", type=Path, required=True)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--weights",
        type=float,
        nargs="+",
        default=(0.0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5),
    )
    parser.add_argument("--max-cases", type=int, default=0)
    parser.add_argument("--progress-interval", type=int, default=100)
    args = parser.parse_args(argv)
    if any(weight < 0 for weight in args.weights):
        parser.error("weights must not be negative")
    return args


if __name__ == "__main__":
    raise SystemExit(evaluate(parse_arguments()))
