#!/usr/bin/env python3
"""使用 Windows/DirectML 对 WSL 导出的整句候选池进行神经重排评测。"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path
from typing import Dict, List, Optional, Sequence

from sentence_neural_reranker import NeuralSentenceScorer


def summary(ranks: Sequence[Optional[int]]) -> Dict[str, float]:
    result: Dict[str, float] = {"cases": len(ranks)}
    for cutoff in (1, 5, 10, 50, 200):
        result[f"top_{cutoff}"] = sum(
            rank is not None and rank <= cutoff for rank in ranks
        ) / max(len(ranks), 1)
    result["mrr"] = sum(1.0 / rank for rank in ranks if rank is not None) / max(
        len(ranks), 1
    )
    return result


def evaluate(args: argparse.Namespace) -> int:
    started = time.monotonic()
    pools = json.loads(args.pools.read_text(encoding="utf-8"))
    scorer = NeuralSentenceScorer(
        args.checkpoint, args.vocabulary, args.device
    )
    ranks = {weight: [] for weight in args.weights}
    details = []
    for index, pool in enumerate(pools, 1):
        candidates = pool["candidates"]
        neural_scores = scorer.score(
            [candidate["text"] for candidate in candidates], args.batch_size
        )
        case_detail = {"text": pool["text"], "source": pool.get("source", "")}
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
        if index % args.progress_interval == 0:
            print(f"completed={index}/{len(pools)}", flush=True)
    report = {
        "version": 1,
        "checkpoint": str(args.checkpoint.resolve()),
        "pools": str(args.pools.resolve()),
        "weights": {str(weight): summary(values) for weight, values in ranks.items()},
        "details": details,
        "elapsed_seconds": time.monotonic() - started,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(report["weights"], ensure_ascii=False, indent=2))
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pools", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device", choices=("cpu", "directml"), default="directml")
    parser.add_argument("--weights", type=float, nargs="+", default=(0.1, 0.25, 0.5, 0.75, 1.0))
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--progress-interval", type=int, default=20)
    return parser.parse_args(argv)


if __name__ == "__main__":
    raise SystemExit(evaluate(parse_arguments()))
