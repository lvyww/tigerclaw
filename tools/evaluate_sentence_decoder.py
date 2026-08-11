#!/usr/bin/env python3
"""批量评测二码整句字符模型和大词频联合重排。"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path
from typing import Dict, List, Optional, Sequence

from test_sentence_ngram import (
    CharacterLanguageModel,
    ExperimentError,
    WordFrequencyModel,
    decode_sentence,
    encode_text,
    parse_lexicon,
    rerank_with_words,
    resolve_candidates,
)


HANDCRAFTED_CASES = (
    "今天早上我吃了两个面包三根油条",
    "我今天早上吃了面包",
    "明天下午我们一起吃饭",
    "这个问题应该怎么解决",
    "今天下午我们一起去公园",
    "我刚才已经吃过饭了",
    "明天可能会下大雨",
    "这个办法应该可以解决问题",
    "他买了三个苹果和两瓶牛奶",
    "请帮我打开这个文件",
    "周末我们准备出去玩",
    "电脑突然没有反应了",
    "这件事情以后再说",
    "我不知道应该怎么办",
)


def load_cases(
    path: Path, maximum: int, include_handcrafted: bool
) -> List[Dict[str, str]]:
    cases = (
        [{"text": text, "source": "handcrafted"} for text in HANDCRAFTED_CASES]
        if include_handcrafted
        else []
    )
    grouped: Dict[str, List[Dict[str, str]]] = {}
    with path.open("r", encoding="utf-8") as stream:
        for line in stream:
            value = json.loads(line)
            if isinstance(value, dict) and isinstance(value.get("text"), str):
                source = str(value.get("source", ""))
                grouped.setdefault(source, []).append({"text": value["text"], "source": source})
    positions = {source: 0 for source in grouped}
    while len(cases) < maximum:
        added = False
        for source, values in grouped.items():
            position = positions[source]
            if position < len(values) and len(cases) < maximum:
                cases.append(values[position])
                positions[source] += 1
                added = True
        if not added:
            break
    return cases[:maximum]


def rank_summary(ranks: Sequence[Optional[int]]) -> Dict[str, float]:
    valid = [rank for rank in ranks if rank is not None]
    result: Dict[str, float] = {"cases": len(ranks), "ranked": len(valid)}
    for cutoff in (1, 5, 10, 50, 200, 500, 2000):
        result[f"top_{cutoff}"] = sum(
            rank is not None and rank <= cutoff for rank in ranks
        ) / max(len(ranks), 1)
    result["mrr"] = sum(1.0 / rank for rank in valid) / max(len(ranks), 1)
    return result


def evaluate(args: argparse.Namespace) -> int:
    started = time.monotonic()
    language_model = CharacterLanguageModel.load(args.model)
    word_model = WordFrequencyModel.load(
        args.word_frequency, args.min_word_frequency, args.max_word_length
    )
    neural_scorer = None
    if args.neural_checkpoint is not None:
        from sentence_neural_reranker import NeuralSentenceScorer

        neural_scorer = NeuralSentenceScorer(
            args.neural_checkpoint, args.vocabulary, args.neural_device
        )
    lexicon = parse_lexicon(args.lexicon)
    cases = load_cases(args.cases, args.max_cases, not args.skip_handcrafted)
    character_ranks: List[Optional[int]] = []
    joint_ranks: List[Optional[int]] = []
    neural_ranks: List[Optional[int]] = []
    details = []
    exported_pools = []
    skipped = []
    for case in cases:
        text = case["text"]
        try:
            codes = encode_text(text, lexicon)
            candidate_sets = resolve_candidates(codes, lexicon)
        except ExperimentError as ex:
            skipped.append({**case, "reason": str(ex)})
            continue
        beam = decode_sentence(
            language_model, candidate_sets, args.beam_width, args.rank_penalty
        )
        character_rank = next(
            (rank for rank, item in enumerate(beam, 1) if item.text == text), None
        )
        ranked = rerank_with_words(beam, word_model, args.word_weight)
        joint_rank = next(
            (
                rank
                for rank, item in enumerate(ranked, 1)
                if item.beam_item.text == text
            ),
            None,
        )
        neural_rank = None
        neural_top = None
        if neural_scorer is not None:
            neural_pool = ranked[: args.neural_candidates]
            neural_scores = neural_scorer.score(
                [item.beam_item.text for item in neural_pool], args.neural_batch_size
            )
            neural_ranked = sorted(
                zip(neural_pool, neural_scores),
                key=lambda value: (
                    value[0].combined_score + args.neural_weight * value[1]
                ),
                reverse=True,
            )
            neural_top = neural_ranked[0][0].beam_item.text
            neural_rank = next(
                (
                    rank
                    for rank, (item, _) in enumerate(neural_ranked, 1)
                    if item.beam_item.text == text
                ),
                None,
            )
        character_ranks.append(character_rank)
        joint_ranks.append(joint_rank)
        if neural_scorer is not None:
            neural_ranks.append(neural_rank)
        details.append(
            {
                **case,
                "character_rank": character_rank,
                "joint_rank": joint_rank,
                "character_top": beam[0].text,
                "joint_top": ranked[0].beam_item.text,
                "neural_rank": neural_rank,
                "neural_top": neural_top,
            }
        )
        if args.pool_output is not None:
            exported_pools.append(
                {
                    **case,
                    "candidates": [
                        {
                            "text": item.beam_item.text,
                            "base_score": item.combined_score,
                        }
                        for item in ranked[: args.pool_size]
                    ],
                }
            )
        if len(details) % args.progress_interval == 0:
            print(f"completed={len(details)}/{len(cases)}", flush=True)

    report = {
        "version": 1,
        "model": str(args.model.resolve()),
        "word_frequency": str(args.word_frequency.resolve()),
        "beam_width": args.beam_width,
        "word_weight": args.word_weight,
        "character": rank_summary(character_ranks),
        "joint": rank_summary(joint_ranks),
        "skipped": skipped,
        "details": details,
        "elapsed_seconds": time.monotonic() - started,
    }
    if neural_scorer is not None:
        report["neural"] = rank_summary(neural_ranks)
        report["neural_weight"] = args.neural_weight
        report["neural_candidates"] = args.neural_candidates
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if args.pool_output is not None:
        args.pool_output.parent.mkdir(parents=True, exist_ok=True)
        args.pool_output.write_text(
            json.dumps(exported_pools, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
    summary_keys = ["character", "joint"]
    if "neural" in report:
        summary_keys.append("neural")
    summary_keys.extend(("skipped", "elapsed_seconds"))
    print(json.dumps({key: report[key] for key in summary_keys}, ensure_ascii=False, indent=2))
    return 0


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--word-frequency", type=Path, required=True)
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--lexicon", type=Path, default=None)
    parser.add_argument("--max-cases", type=int, default=200)
    parser.add_argument("--beam-width", type=int, default=2000)
    parser.add_argument("--rank-penalty", type=float, default=0.03)
    parser.add_argument("--word-weight", type=float, default=1.4)
    parser.add_argument("--min-word-frequency", type=int, default=300)
    parser.add_argument("--max-word-length", type=int, default=6)
    parser.add_argument("--progress-interval", type=int, default=20)
    parser.add_argument("--pool-output", type=Path)
    parser.add_argument("--pool-size", type=int, default=200)
    parser.add_argument("--skip-handcrafted", action="store_true")
    parser.add_argument("--neural-checkpoint", type=Path)
    parser.add_argument("--vocabulary", type=Path)
    parser.add_argument("--neural-device", choices=("cpu", "directml"), default="cpu")
    parser.add_argument("--neural-weight", type=float, default=0.5)
    parser.add_argument("--neural-candidates", type=int, default=200)
    parser.add_argument("--neural-batch-size", type=int, default=32)
    args = parser.parse_args(argv)
    if args.lexicon is None:
        from test_sentence_ngram import DEFAULT_LEXICON

        args.lexicon = DEFAULT_LEXICON
    if args.neural_checkpoint is not None and args.vocabulary is None:
        parser.error("指定 --neural-checkpoint 时必须同时指定 --vocabulary")
    return args


if __name__ == "__main__":
    raise SystemExit(evaluate(parse_arguments()))
