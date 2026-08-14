#!/usr/bin/env python3
"""Compare TigerClaw's trigram with Rime grammar scoring offline."""

from __future__ import annotations

import argparse
import json
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

from rime_gram_model import RimeBGCAndBGWModel, TrigramAndBGWModel
from kneser_ney_model import KneserNeyLanguageModel
from sentence_variable_decoder import decode_code_lattice, parse_shortest_code_index
from test_sentence_ngram import CharacterLanguageModel, ExperimentError


ARCHIVE_ROOT = Path("/mnt/c/Archive/tigerclaw_sentence_ml")
DEFAULT_MODEL = ARCHIVE_ROOT / "baseline" / "ngram-20000.json.gz"
DEFAULT_CASES = ARCHIVE_ROOT / "baseline" / "tiger-sentence-test-1000-pools.json"
DEFAULT_BGC = Path("/mnt/c/Users/yc/Downloads/zh-hans-t-essay-bgc.gram")
DEFAULT_BGW = Path("/mnt/c/Users/yc/Downloads/zh-hans-t-essay-bgw.gram")
DEFAULT_KNESER_NEY = (
    ARCHIVE_ROOT
    / "trainer_v2"
    / "full-kn-m30-r20-p050-w025"
    / "sentence-ngram-v2.bin"
)
DEFAULT_LEXICON = (
    Path(__file__).resolve().parents[1]
    / "release_arm64"
    / "码表"
    / "虎整句"
    / "常用字词.txt"
)
KNOWN_CASE = {
    "text": "不带一丝矫揉造作",
    "source": "handcrafted-regression",
    "code": "cblwfiiisotmpuitqdujw",
}
_WORKER_STATE: Dict[str, object] = {}


def rank_of(text: str, beam: Sequence[object]) -> Optional[int]:
    return next(
        (rank for rank, item in enumerate(beam, 1) if item.text == text),
        None,
    )


def summarize(ranks: Sequence[Optional[int]], seconds: float) -> Dict[str, float]:
    total = max(len(ranks), 1)
    valid = [rank for rank in ranks if rank is not None]
    result: Dict[str, float] = {
        "cases": len(ranks),
        "recalled": len(valid),
        "recall": len(valid) / total,
        "mrr": sum(1.0 / rank for rank in valid) / total,
        "seconds": seconds,
        "milliseconds_per_case": seconds * 1000.0 / total,
    }
    for cutoff in (1, 5, 10, 50, 200, 2000):
        result[f"top_{cutoff}"] = sum(
            rank is not None and rank <= cutoff for rank in ranks
        ) / total
    return result


def load_cases(path: Path, maximum: int, include_known: bool) -> List[Dict[str, str]]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as ex:
        raise ExperimentError(f"读取评测候选池失败: {ex}") from ex
    if not isinstance(payload, list):
        raise ExperimentError("评测候选池根节点不是数组。")
    cases: List[Dict[str, str]] = []
    for value in payload:
        if not isinstance(value, dict):
            continue
        text = value.get("text")
        code = value.get("code")
        if isinstance(text, str) and text and isinstance(code, str) and code:
            cases.append(
                {"text": text, "code": code, "source": str(value.get("source", ""))}
            )
            if maximum and len(cases) >= maximum:
                break
    if include_known and not any(value["text"] == KNOWN_CASE["text"] for value in cases):
        cases.append(dict(KNOWN_CASE))
    if not cases:
        raise ExperimentError("评测候选池没有包含text和code的有效用例。")
    return cases


def build_challenger(
    experiment: str,
    trigram: CharacterLanguageModel,
    bgc: Path,
    bgw: Path,
    bgc_weight: float,
    bgw_weight: float,
    gram_mode: str,
    kneser_ney: Path,
) -> object:
    if experiment == "kneser-ney":
        return KneserNeyLanguageModel(kneser_ney)
    if experiment == "trigram-bgw":
        return TrigramAndBGWModel(trigram, bgw, bgw_weight)
    return RimeBGCAndBGWModel(
        bgc,
        bgw,
        bgc_weight,
        bgw_weight,
        gram_mode,
    )


def initialize_worker(config: Dict[str, object]) -> None:
    trigram = CharacterLanguageModel.load(config["trigram"])
    _WORKER_STATE.update(config)
    _WORKER_STATE["index"] = parse_shortest_code_index(config["lexicon"])
    _WORKER_STATE["trigram_model"] = trigram
    _WORKER_STATE["challenger"] = build_challenger(
        config["experiment"],
        trigram,
        config["bgc"],
        config["bgw"],
        config["bgc_weight"],
        config["bgw_weight"],
        config["gram_mode"],
        config["kneser_ney"],
    )


def evaluate_worker_case(
    positioned_case: Tuple[int, Dict[str, str]],
) -> Dict[str, object]:
    position, case = positioned_case
    index = _WORKER_STATE["index"]
    trigram = _WORKER_STATE["trigram_model"]
    challenger = _WORKER_STATE["challenger"]
    beam_width = _WORKER_STATE["beam_width"]
    rank_penalty = _WORKER_STATE["rank_penalty"]
    challenger_key = _WORKER_STATE["challenger_key"]
    try:
        before = time.monotonic()
        trigram_result = decode_code_lattice(
            case["code"], index, trigram, beam_width, rank_penalty
        )
        trigram_seconds = time.monotonic() - before
        before = time.monotonic()
        challenger_result = decode_code_lattice(
            case["code"], index, challenger, beam_width, rank_penalty
        )
        challenger_seconds = time.monotonic() - before
    except ExperimentError as ex:
        return {
            "position": position,
            "skipped": {**case, "reason": str(ex)},
        }

    trigram_rank = rank_of(case["text"], trigram_result.beam)
    challenger_rank = rank_of(case["text"], challenger_result.beam)
    return {
        "position": position,
        "trigram_rank": trigram_rank,
        "challenger_rank": challenger_rank,
        "trigram_seconds": trigram_seconds,
        "challenger_seconds": challenger_seconds,
        "detail": {
            **case,
            "trigram_rank": trigram_rank,
            f"{challenger_key}_rank": challenger_rank,
            "trigram_top": trigram_result.beam[0].text,
            f"{challenger_key}_top": challenger_result.beam[0].text,
            "trigram_expanded": trigram_result.expanded_states,
            f"{challenger_key}_expanded": challenger_result.expanded_states,
        },
    }


def evaluate(args: argparse.Namespace) -> int:
    started = time.monotonic()
    cases = load_cases(args.cases, args.max_cases, not args.skip_known_case)
    challenger_key = args.experiment.replace("-", "_")
    trigram_ranks: List[Optional[int]] = []
    challenger_ranks: List[Optional[int]] = []
    details = []
    skipped = []
    trigram_seconds = 0.0
    challenger_seconds = 0.0
    if args.workers > 1:
        config = {
            "trigram": args.trigram,
            "lexicon": args.lexicon,
            "bgc": args.bgc,
            "bgw": args.bgw,
            "bgc_weight": args.bgc_weight,
            "bgw_weight": args.bgw_weight,
            "gram_mode": args.gram_mode,
            "kneser_ney": args.kneser_ney,
            "experiment": args.experiment,
            "challenger_key": challenger_key,
            "beam_width": args.beam_width,
            "rank_penalty": args.rank_penalty,
        }
        with ProcessPoolExecutor(
            max_workers=args.workers,
            initializer=initialize_worker,
            initargs=(config,),
        ) as executor:
            results = executor.map(
                evaluate_worker_case,
                enumerate(cases, 1),
                chunksize=args.worker_chunk_size,
            )
            for completed, result in enumerate(results, 1):
                if "skipped" in result:
                    skipped.append(result["skipped"])
                else:
                    trigram_ranks.append(result["trigram_rank"])
                    challenger_ranks.append(result["challenger_rank"])
                    trigram_seconds += result["trigram_seconds"]
                    challenger_seconds += result["challenger_seconds"]
                    details.append(result["detail"])
                if (
                    args.progress_interval
                    and completed % args.progress_interval == 0
                ):
                    print(f"completed={completed}/{len(cases)}", flush=True)
    else:
        index = parse_shortest_code_index(args.lexicon)
        trigram = CharacterLanguageModel.load(args.trigram)
        challenger = build_challenger(
            args.experiment,
            trigram,
            args.bgc,
            args.bgw,
            args.bgc_weight,
            args.bgw_weight,
            args.gram_mode,
            args.kneser_ney,
        )
        try:
            for position, case in enumerate(cases, 1):
                try:
                    before = time.monotonic()
                    trigram_result = decode_code_lattice(
                        case["code"], index, trigram, args.beam_width, args.rank_penalty
                    )
                    trigram_seconds += time.monotonic() - before

                    before = time.monotonic()
                    challenger_result = decode_code_lattice(
                        case["code"], index, challenger, args.beam_width, args.rank_penalty
                    )
                    challenger_seconds += time.monotonic() - before
                except ExperimentError as ex:
                    skipped.append({**case, "reason": str(ex)})
                    continue

                trigram_rank = rank_of(case["text"], trigram_result.beam)
                challenger_rank = rank_of(case["text"], challenger_result.beam)
                trigram_ranks.append(trigram_rank)
                challenger_ranks.append(challenger_rank)
                details.append(
                    {
                        **case,
                        "trigram_rank": trigram_rank,
                        f"{challenger_key}_rank": challenger_rank,
                        "trigram_top": trigram_result.beam[0].text,
                        f"{challenger_key}_top": challenger_result.beam[0].text,
                        "trigram_expanded": trigram_result.expanded_states,
                        f"{challenger_key}_expanded": challenger_result.expanded_states,
                    }
                )
                if args.progress_interval and position % args.progress_interval == 0:
                    print(f"completed={position}/{len(cases)}", flush=True)
        finally:
            challenger.close()

    report = {
        "version": 1,
        "cases_file": str(args.cases.resolve()),
        "lexicon": str(args.lexicon.resolve()),
        "trigram_model": str(args.trigram.resolve()),
        "bgc_model": str(args.bgc.resolve()),
        "bgw_model": str(args.bgw.resolve()),
        "kneser_ney_model": str(args.kneser_ney.resolve()),
        "beam_width": args.beam_width,
        "rank_penalty": args.rank_penalty,
        "bgc_weight": args.bgc_weight,
        "bgw_weight": args.bgw_weight,
        "gram_mode": args.gram_mode,
        "experiment": args.experiment,
        "workers": args.workers,
        "trigram": summarize(trigram_ranks, trigram_seconds),
        challenger_key: summarize(challenger_ranks, challenger_seconds),
        "top_1_changes": {
            f"{challenger_key}_wins": sum(
                challenger_rank == 1 and trigram_rank != 1
                for trigram_rank, challenger_rank in zip(
                    trigram_ranks, challenger_ranks
                )
            ),
            "trigram_wins": sum(
                trigram_rank == 1 and challenger_rank != 1
                for trigram_rank, challenger_rank in zip(
                    trigram_ranks, challenger_ranks
                )
            ),
        },
        "skipped": skipped,
        "details": details,
        "elapsed_seconds": time.monotonic() - started,
    }
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    print(
        json.dumps(
            {
                "trigram": report["trigram"],
                challenger_key: report[challenger_key],
                "top_1_changes": report["top_1_changes"],
                "skipped": len(skipped),
                "elapsed_seconds": report["elapsed_seconds"],
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    known = next(
        (item for item in details if item["source"] == "handcrafted-regression"),
        None,
    )
    if known is not None:
        print("known_case=" + json.dumps(known, ensure_ascii=False))
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trigram", type=Path, default=DEFAULT_MODEL)
    parser.add_argument("--bgc", type=Path, default=DEFAULT_BGC)
    parser.add_argument("--bgw", type=Path, default=DEFAULT_BGW)
    parser.add_argument("--kneser-ney", type=Path, default=DEFAULT_KNESER_NEY)
    parser.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON)
    parser.add_argument("--cases", type=Path, default=DEFAULT_CASES)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--experiment",
        choices=("bgc-bgw", "trigram-bgw", "kneser-ney"),
        default="bgc-bgw",
        help="直接用BGC+BGW替换三元，或在三元分数上叠加BGW。",
    )
    parser.add_argument("--max-cases", type=int, default=200)
    parser.add_argument("--beam-width", type=int, default=2000)
    parser.add_argument("--rank-penalty", type=float, default=0.03)
    parser.add_argument(
        "--bgc-weight",
        type=float,
        default=0.2,
        help="BGC权重；默认值来自200条调参集的粗略网格搜索。",
    )
    parser.add_argument(
        "--bgw-weight",
        type=float,
        help="BGW权重；三元+BGW默认0.02，直接BGC+BGW默认1.0。",
    )
    parser.add_argument(
        "--gram-mode",
        choices=("character", "boundary"),
        default="character",
        help="逐字累计全部搭配，或按Rime候选边界只查询一次。",
    )
    parser.add_argument("--progress-interval", type=int, default=20)
    parser.add_argument(
        "--workers",
        type=int,
        default=16,
        help="并行工作进程数，默认16；设为1可复现串行执行。",
    )
    parser.add_argument("--worker-chunk-size", type=int, default=8)
    parser.add_argument("--skip-known-case", action="store_true")
    args = parser.parse_args(argv)
    if args.bgw_weight is None:
        args.bgw_weight = 0.02 if args.experiment == "trigram-bgw" else 1.0
    if (
        args.max_cases < 0
        or args.beam_width <= 0
        or args.workers <= 0
        or args.worker_chunk_size <= 0
    ):
        parser.error("样本数、Beam宽度、进程数和任务块大小无效。")
    if args.bgc_weight < 0 or args.bgw_weight < 0:
        parser.error("BGC/BGW权重不能为负数。")
    if (
        args.experiment == "bgc-bgw"
        and args.bgc_weight == 0
        and args.bgw_weight == 0
    ):
        parser.error("BGC/BGW权重不能同时为0。")
    return args


if __name__ == "__main__":
    raise SystemExit(evaluate(parse_arguments()))
