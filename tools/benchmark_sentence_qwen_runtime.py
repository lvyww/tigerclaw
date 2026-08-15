#!/usr/bin/env python3
"""Benchmark Qwen reranking as one real-time request per sentence."""

from __future__ import annotations

import argparse
import json
import math
import os
import time
from collections import defaultdict
from pathlib import Path
from typing import Dict, List, Optional, Sequence

import torch
from transformers import AutoTokenizer

from test_sentence_qwen import (
    default_model_path,
    encode_candidate,
    load_model,
    process_memory_megabytes,
    resolve_device,
    resolve_dtype,
    score_batch,
)


def percentile(values: Sequence[float], fraction: float) -> float:
    ordered = sorted(values)
    if not ordered:
        return 0.0
    index = max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))
    return ordered[index]


def summarize(values: Sequence[float]) -> Dict[str, float]:
    return {
        "count": len(values),
        "mean_ms": sum(values) / max(len(values), 1),
        "p50_ms": percentile(values, 0.50),
        "p95_ms": percentile(values, 0.95),
        "p99_ms": percentile(values, 0.99),
        "max_ms": max(values, default=0.0),
    }


def base_margin(case: dict) -> float:
    candidates = case.get("candidates", [])
    if len(candidates) < 2:
        return float("inf")
    ordered = sorted(candidates, key=lambda item: item["base_score"], reverse=True)
    return ordered[0]["base_score"] - ordered[1]["base_score"]


def pick_balanced(cases: Sequence[dict], count: int, used: set[str]) -> List[dict]:
    groups: Dict[str, List[dict]] = defaultdict(list)
    for case in cases:
        code = case.get("code", "")
        if code and code not in used:
            groups[case.get("source", "")].append(case)
    result: List[dict] = []
    sources = sorted(groups)
    while len(result) < count and sources:
        next_sources = []
        for source in sources:
            values = groups[source]
            while values and values[0].get("code", "") in used:
                values.pop(0)
            if values and len(result) < count:
                case = values.pop(0)
                used.add(case["code"])
                result.append(case)
            if values:
                next_sources.append(source)
        sources = next_sources
    if len(result) != count:
        raise ValueError(f"not enough cases: requested={count}, selected={len(result)}")
    return result


def select_cases(pools: Sequence[dict]) -> List[dict]:
    eligible = [
        case
        for case in pools
        if len(case.get("candidates", [])) >= 2 and case.get("code")
    ]
    used: set[str] = set()
    selected: List[dict] = []

    ambiguous = sorted(eligible, key=lambda case: (base_margin(case), len(case["code"])))
    for case in pick_balanced(ambiguous, 10, used):
        case = dict(case)
        case["benchmark_category"] = "ambiguous"
        selected.append(case)

    categories = (
        ("short", 20, lambda case: len(case["code"]) <= 20),
        ("ordinary", 40, lambda case: 20 < len(case["code"]) <= 40),
        ("long", 30, lambda case: len(case["code"]) > 40),
    )
    for name, count, predicate in categories:
        values = [case for case in eligible if predicate(case)]
        values.sort(key=lambda case: (len(case["code"]), case.get("source", "")))
        for case in pick_balanced(values, count, used):
            case = dict(case)
            case["benchmark_category"] = name
            selected.append(case)
    return selected


def prepare_request(tokenizer, model, case: dict, candidate_limit: int):
    texts = [item["text"] for item in case["candidates"][:candidate_limit]]
    encoded = []
    tokens = []
    for text in texts:
        sequence, visible_tokens = encode_candidate(
            tokenizer,
            model.config.bos_token_id,
            model.config.eos_token_id,
            text,
        )
        encoded.append(sequence)
        tokens.append(visible_tokens)
    return texts, encoded, tokens


def benchmark(args: argparse.Namespace) -> int:
    torch.set_num_threads(args.threads)
    torch.set_num_interop_threads(min(args.threads, 4))
    pools = json.loads(args.pools.read_text(encoding="utf-8"))
    cases = select_cases(pools)

    device, device_label = resolve_device(args.device)
    dtype = resolve_dtype(args.dtype)
    load_started = time.perf_counter()
    tokenizer = AutoTokenizer.from_pretrained(
        args.model, local_files_only=True, use_fast=True
    )
    model = load_model(args.model, device, device_label, dtype)
    if args.quantization == "dynamic-int8":
        if device_label != "cpu":
            raise ValueError("dynamic INT8 quantization only supports CPU")
        torch.backends.quantized.engine = args.quantized_engine
        model = torch.ao.quantization.quantize_dynamic(
            model,
            {torch.nn.Linear},
            dtype=torch.qint8,
            inplace=True,
        )
    model.eval()
    load_ms = (time.perf_counter() - load_started) * 1000.0
    memory_after_load = process_memory_megabytes()

    first_request_started = time.perf_counter()
    first = prepare_request(tokenizer, model, cases[0], args.candidate_limit)
    score_batch(model, tokenizer, device, first[1], first[0], first[2])
    first_request_ms = (time.perf_counter() - first_request_started) * 1000.0

    for case in cases[: args.warmup]:
        request = prepare_request(tokenizer, model, case, args.candidate_limit)
        score_batch(model, tokenizer, device, request[1], request[0], request[2])

    all_timings: List[float] = []
    category_timings: Dict[str, List[float]] = defaultdict(list)
    round_summaries = []
    for round_index in range(args.rounds):
        round_timings = []
        for case in cases:
            started = time.perf_counter()
            request = prepare_request(tokenizer, model, case, args.candidate_limit)
            score_batch(model, tokenizer, device, request[1], request[0], request[2])
            elapsed_ms = (time.perf_counter() - started) * 1000.0
            round_timings.append(elapsed_ms)
            all_timings.append(elapsed_ms)
            category_timings[case["benchmark_category"]].append(elapsed_ms)
        round_summaries.append(summarize(round_timings))
        print(
            f"round={round_index + 1}/{args.rounds} "
            f"p50={round_summaries[-1]['p50_ms']:.1f}ms "
            f"p95={round_summaries[-1]['p95_ms']:.1f}ms",
            flush=True,
        )

    memory_after_benchmark = process_memory_megabytes()
    report = {
        "version": 1,
        "model": str(args.model.resolve()),
        "pools": str(args.pools.resolve()),
        "device": device_label,
        "dtype": args.dtype,
        "quantization": args.quantization,
        "quantized_engine": (
            args.quantized_engine if args.quantization == "dynamic-int8" else ""
        ),
        "threads": torch.get_num_threads(),
        "candidate_limit": args.candidate_limit,
        "warmup_requests": args.warmup,
        "rounds": args.rounds,
        "case_count": len(cases),
        "load_ms": load_ms,
        "first_request_ms": first_request_ms,
        "memory_after_load_mib": memory_after_load,
        "memory_after_benchmark_mib": memory_after_benchmark,
        "overall": summarize(all_timings),
        "rounds_summary": round_summaries,
        "categories": {
            category: summarize(values)
            for category, values in sorted(category_timings.items())
        },
        "selected_cases": [
            {
                "text": case["text"],
                "code": case["code"],
                "source": case.get("source", ""),
                "category": case["benchmark_category"],
                "candidate_count": min(len(case["candidates"]), args.candidate_limit),
                "base_margin": base_margin(case),
            }
            for case in cases
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps({key: report[key] for key in (
        "load_ms",
        "first_request_ms",
        "memory_after_load_mib",
        "memory_after_benchmark_mib",
        "overall",
        "categories",
    )}, ensure_ascii=False, indent=2))
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
    parser.add_argument(
        "--quantization",
        choices=("none", "dynamic-int8"),
        default="none",
    )
    parser.add_argument(
        "--quantized-engine",
        choices=("x86", "onednn", "fbgemm"),
        default="x86",
    )
    parser.add_argument("--candidate-limit", type=int, default=5)
    parser.add_argument("--threads", type=int, default=max(os.cpu_count() or 1, 1))
    parser.add_argument("--warmup", type=int, default=20)
    parser.add_argument("--rounds", type=int, default=3)
    args = parser.parse_args(argv)
    if min(args.candidate_limit, args.threads, args.rounds) < 1 or args.warmup < 0:
        parser.error("invalid benchmark parameters")
    return args


if __name__ == "__main__":
    raise SystemExit(benchmark(parse_arguments()))
