#!/usr/bin/env python3
"""Build the length-balanced end-to-end early-commit evaluation set."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Dict, Iterable, List


ARCHIVE_ROOT = Path("/mnt/c/Archive/tigerclaw_sentence_ml/baseline")
DEFAULT_SHORT = ARCHIVE_ROOT / "tiger-sentence-short-1-6-cases.json"
DEFAULT_LONG = ARCHIVE_ROOT / "tiger-sentence-validation-2000-cases.json"
DEFAULT_OUTPUT = ARCHIVE_ROOT / "tiger-sentence-early-commit-eval-v1.json"
PER_BUCKET = 600
REGRESSIONS = (
    {
        "text": "新人上午来面试",
        "code": "iejryfenahbmsp",
        "source": "handcrafted-regression",
    },
    {
        "text": "左手匕首",
        "code": "nuusvbbhoi",
        "source": "handcrafted-regression",
    },
    {
        "text": "有一些人在这里看东西",
        "code": "nvfisvmjrngvduqryxvx",
        "source": "handcrafted-regression",
    },
)


def load_cases(path: Path) -> List[Dict[str, str]]:
    values = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(values, list):
        raise ValueError(f"测试集根节点不是数组: {path}")
    return [
        value
        for value in values
        if isinstance(value, dict)
        and isinstance(value.get("text"), str)
        and isinstance(value.get("code"), str)
    ]


def take(
    cases: Iterable[Dict[str, str]],
    low: int,
    high: int | None,
    per_bucket: int,
) -> List[Dict[str, str]]:
    selected = []
    for item in cases:
        length = len(item["code"])
        if length < low or (high is not None and length > high):
            continue
        selected.append(item)
        if len(selected) == per_bucket:
            break
    if len(selected) != per_bucket:
        raise ValueError(f"编码长度 {low}..{high or '∞'} 只有 {len(selected)} 条")
    return selected


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--short", type=Path, default=DEFAULT_SHORT)
    parser.add_argument("--long", type=Path, default=DEFAULT_LONG)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--per-bucket", type=int, default=PER_BUCKET)
    args = parser.parse_args()

    if args.per_bucket <= 0:
        parser.error("--per-bucket 必须大于 0")

    short_cases = load_cases(args.short)
    long_cases = load_cases(args.long)
    combined = (
        take(short_cases, 5, 8, args.per_bucket)
        + take(short_cases, 9, 12, args.per_bucket)
        + take(short_cases, 13, 18, args.per_bucket)
        + take(long_cases, 19, 30, args.per_bucket)
        + take(long_cases, 31, None, args.per_bucket)
        + list(REGRESSIONS)
    )
    unique: List[Dict[str, str]] = []
    seen = set()
    for item in combined:
        key = (item["text"], item["code"])
        if key in seen:
            continue
        seen.add(key)
        unique.append(item)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(unique, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"wrote {len(unique)} cases to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
