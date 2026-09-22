#!/usr/bin/env python3
"""Compare default spelling with the frozen exact decoder; never overwrite evidence."""
import argparse
import hashlib
import json
import statistics
from pathlib import Path


def records(path):
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            row = json.loads(line)
            candidates = row["candidates"]
            # RawEnds/SpellingPenalty are extra spelling metadata. Compare the
            # original semantic path separately from that representation.
            paths = [[{k: s[k] for k in ("code", "text", "start", "end", "tokens")}
                      for s in c["segments"]] for c in candidates]
            yield {k: row[k] for k in ("id", "code", "text", "rank", "ms")} | {
                "texts": [c["text"] for c in candidates],
                "scores": [c["score"] for c in candidates],
                "paths": hashlib.sha256(json.dumps(paths, ensure_ascii=False, sort_keys=True).encode()).hexdigest(),
            }


def metrics(rows):
    times = sorted(r["ms"] for r in rows)
    return {
        "top1": sum(r["rank"] == 1 for r in rows),
        "top10": sum(0 < r["rank"] <= 10 for r in rows),
        "top50": sum(0 < r["rank"] <= 50 for r in rows),
        "median_ms": statistics.median(times),
        "p95_ms": times[int(len(times) * .95)],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exact", type=Path)
    parser.add_argument("expanded", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    baseline = {}
    for row in records(args.exact):
        if row["id"] in baseline:
            raise ValueError("Duplicate exact record")
        baseline[row["id"]] = row
    seen, actual, changes = set(), [], []
    same_lists = same_paths = rescued = regressed = 0
    max_error = 0.0
    for row in records(args.expanded):
        key = row["id"]
        if key in seen:
            raise ValueError("Duplicate expanded record")
        seen.add(key)
        before = baseline[key]
        if (before["text"], before["code"]) != (row["text"], row["code"]):
            raise ValueError("Input mismatch")
        same_lists += before["texts"] == row["texts"]
        same_paths += before["paths"] == row["paths"]
        if before["texts"] == row["texts"]:
            max_error = max(max_error, max((abs(a - b) for a, b in zip(before["scores"], row["scores"])), default=0))
        if before["texts"][:1] != row["texts"][:1]:
            changes.append({"id": key, "code": row["code"], "target": row["text"],
                            "exact": before["texts"][:1], "expanded": row["texts"][:1]})
        rescued += before["rank"] != 1 and row["rank"] == 1
        regressed += before["rank"] == 1 and row["rank"] != 1
        actual.append({k: row[k] for k in ("rank", "ms")})
    if seen != baseline.keys() or len(seen) != 8019:
        raise ValueError("Expected the same 8019 test records")
    def digest(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()
    report = {
        "records": len(seen), "exact": metrics(list(baseline.values())), "expanded": metrics(actual),
        "identical_top50_lists": same_lists, "identical_semantic_paths": same_paths,
        "maximum_score_error_on_identical_lists": max_error,
        "rescued": rescued, "regressed": regressed, "changed_top1": len(changes), "changes": changes,
        "exact_sha256": digest(args.exact), "expanded_sha256": digest(args.expanded),
        "timing_note": "Whole-input decodes with concurrent workers; not typing latency or a speed comparison.",
    }
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
