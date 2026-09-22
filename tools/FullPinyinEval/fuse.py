"""Materialize ranked Top-10; optional explicit n-gram fallback for manual use."""
import argparse
import json
import math
from pathlib import Path
from analyze import indexed, read, qwen_rows


def ranked(row, scores, alpha):
    candidates = row["candidates"][:10]
    if scores is not None and (len(scores) != len(candidates) or any(not math.isfinite(s) for s in scores)):
        raise ValueError("Qwen score count differs from candidate count")
    result = [dict(text=c["text"], ngramRank=i + 1, ngramScore=c["score"],
                   qwenScore=None if scores is None else scores[i],
                   fusedScore=c["score"] if scores is None else (1-alpha)*c["score"]+alpha*scores[i],
                   segments=c["segments"]) for i, c in enumerate(candidates)]
    return sorted(result, key=lambda c: -c["fusedScore"])


def main():
    p = argparse.ArgumentParser()
    p.add_argument("decode", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("alpha", type=float)
    p.add_argument("qwen", type=Path, nargs="*")
    p.add_argument("--allow-fallback", action="store_true")
    args = p.parse_args()
    if not 0 <= args.alpha <= 1:
        p.error("alpha must be between 0 and 1")
    decode = indexed(read(args.decode))
    if args.allow_fallback:
        qwen = {}
        for path in args.qwen:
            for row in read(path):
                if row["error"] is None:
                    if row["id"] in qwen:
                        raise ValueError("Duplicate successful Qwen result")
                    qwen[row["id"]] = row
    else:
        qwen = qwen_rows(args.qwen, decode)
    with args.output.open("w", encoding="utf-8") as output:
        for i, row in decode.items():
            scores = qwen[i]["scores"] if i in qwen and len(row["candidates"]) > 1 else None
            value = dict(id=i, code=row["code"], tail=row["tail"], alpha=args.alpha,
                         fallback=i not in qwen, candidates=ranked(row, scores, args.alpha))
            output.write(json.dumps(value, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
