#!/usr/bin/env python3
"""Score TigerClaw sentence candidate pools with an unquantized Qwen base model."""

from __future__ import annotations

import argparse
import json
import os
import time
from pathlib import Path
from typing import Dict, List, Optional, Sequence

import torch
from transformers import AutoTokenizer

from test_sentence_qwen import (
    default_model_path,
    encode_candidate,
    load_model,
    resolve_device,
    resolve_dtype,
    score_batch,
)


def summarize(ranks: Sequence[Optional[int]]) -> Dict[str, float]:
    total = max(len(ranks), 1)
    result: Dict[str, float] = {
        "cases": len(ranks),
        "mrr": sum(1.0 / rank for rank in ranks if rank is not None) / total,
    }
    for cutoff in (1, 3, 5, 10, 20):
        result[f"top_{cutoff}"] = sum(
            rank is not None and rank <= cutoff for rank in ranks
        ) / total
    return result


def rank_of(text: str, candidates: Sequence[dict], score_name: str) -> Optional[int]:
    ordered = sorted(candidates, key=lambda item: item[score_name], reverse=True)
    return next(
        (index for index, candidate in enumerate(ordered, 1) if candidate["text"] == text),
        None,
    )


def evaluate(args: argparse.Namespace) -> int:
    if args.threads:
        torch.set_num_threads(args.threads)
        torch.set_num_interop_threads(min(args.threads, 4))

    pools = json.loads(args.pools.read_text(encoding="utf-8"))
    if args.start_case:
        pools = pools[args.start_case :]
    if args.max_cases:
        pools = pools[: args.max_cases]
    if not pools:
        raise ValueError("candidate pool is empty")

    device, device_label = resolve_device(args.device)
    dtype = resolve_dtype(args.dtype)
    model_path = args.model.resolve()
    print(f"loading model={model_path} device={device_label} dtype={args.dtype}", flush=True)
    started = time.monotonic()
    tokenizer = AutoTokenizer.from_pretrained(
        model_path, local_files_only=True, use_fast=True
    )
    model = load_model(model_path, device, device_label, dtype)
    model.eval()
    bos_token_id = model.config.bos_token_id
    eos_token_id = model.config.eos_token_id
    if bos_token_id is None or eos_token_id is None:
        raise RuntimeError("model configuration has no BOS/EOS token")
    print(f"loaded elapsed={time.monotonic() - started:.1f}s", flush=True)

    scored_pools: List[dict] = []
    pending: List[tuple[int, int, str, list[int], tuple[str, ...]]] = []
    for pool_index, pool in enumerate(pools):
        candidates = [
            {"text": value["text"], "base_score": value["base_score"]}
            for value in pool["candidates"][: args.candidate_limit]
        ]
        scored_pools.append(
            {
                "text": pool["text"],
                "code": pool.get("code", ""),
                "source": pool.get("source", ""),
                "candidates": candidates,
            }
        )
        for candidate_index, candidate in enumerate(candidates):
            sequence, tokens = encode_candidate(
                tokenizer, bos_token_id, eos_token_id, candidate["text"]
            )
            pending.append(
                (pool_index, candidate_index, candidate["text"], sequence, tokens)
            )

    # Similar-length batches avoid spending most of the forward pass on padding.
    # Reordering is safe because every score is written back through its owner index.
    pending.sort(key=lambda item: len(item[3]))
    for start in range(0, len(pending), args.batch_size):
        batch = pending[start : start + args.batch_size]
        scores = score_batch(
            model,
            tokenizer,
            device,
            [item[3] for item in batch],
            [item[2] for item in batch],
            [item[4] for item in batch],
        )
        for (pool_index, candidate_index, _, _, _), score in zip(batch, scores):
            candidate = scored_pools[pool_index]["candidates"][candidate_index]
            candidate["qwen_total"] = score.total_log_probability
            candidate["qwen_token_average"] = score.average_token_log_probability
            candidate["qwen_character_average"] = score.average_character_log_probability
            candidate["qwen_token_count"] = score.token_count
        completed = min(start + len(batch), len(pending))
        if (
            args.progress_interval
            and completed % (args.progress_interval * args.candidate_limit)
            < len(batch)
        ):
            print(
                f"scored_candidates={completed}/{len(pending)} "
                f"elapsed={time.monotonic() - started:.1f}s",
                flush=True,
            )

    metrics = {}
    for score_name in (
        "base_score",
        "qwen_total",
        "qwen_token_average",
        "qwen_character_average",
    ):
        ranks = [
            rank_of(pool["text"], pool["candidates"], score_name)
            for pool in scored_pools
        ]
        metrics[score_name] = summarize(ranks)

    report = {
        "version": 1,
        "model": str(model_path),
        "pools": str(args.pools.resolve()),
        "device": device_label,
        "dtype": args.dtype,
        "threads": torch.get_num_threads(),
        "candidate_limit": args.candidate_limit,
        "start_case": args.start_case,
        "metrics": metrics,
        "cases": scored_pools,
        "elapsed_seconds": time.monotonic() - started,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(metrics, ensure_ascii=False, indent=2))
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pools", type=Path, required=True)
    parser.add_argument("--model", type=Path, default=default_model_path())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device", choices=("cpu", "directml"), default="cpu")
    parser.add_argument(
        "--dtype",
        choices=("float32", "float16", "bfloat16"),
        default="float32",
    )
    parser.add_argument("--candidate-limit", type=int, default=5)
    parser.add_argument("--batch-size", type=int, default=20)
    parser.add_argument("--threads", type=int, default=max(os.cpu_count() or 1, 1))
    parser.add_argument("--start-case", type=int, default=0)
    parser.add_argument("--max-cases", type=int, default=0)
    parser.add_argument("--progress-interval", type=int, default=10)
    args = parser.parse_args(argv)
    if min(args.candidate_limit, args.batch_size, args.threads) < 1:
        parser.error("candidate limit, batch size, and threads must be positive")
    if args.start_case < 0 or args.max_cases < 0:
        parser.error("case ranges must not be negative")
    return args


if __name__ == "__main__":
    raise SystemExit(evaluate(parse_arguments()))
