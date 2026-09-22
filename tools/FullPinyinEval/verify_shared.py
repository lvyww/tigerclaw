#!/usr/bin/env python3
"""Verify the extracted decoder against frozen exact-input Top-50 evidence."""
import argparse
import hashlib
import json
from pathlib import Path

def load(path):
    rows = {}
    with path.open(encoding="utf-8-sig") as stream:
        for line in stream:
            row = json.loads(line)
            if row["id"] in rows: raise ValueError("duplicate id")
            rows[row["id"]] = {
                **{field: row[field] for field in ["id", "code", "text", "consumed", "tail", "rank"]},
                "candidates": [{"text": c["text"], "score": c["score"],
                    "segments": hashlib.sha256(json.dumps(c["segments"], sort_keys=True, ensure_ascii=False).encode()).hexdigest()}
                    for c in row["candidates"]],
            }
    return rows

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("baseline", type=Path); p.add_argument("actual", type=Path)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    baseline, actual = load(args.baseline), load(args.actual)
    if len(baseline) != 8019 or baseline.keys() != actual.keys(): raise AssertionError("Expected exactly the same 8019 test records")
    maximum_error = 0.0
    for key, b in baseline.items():
        a = actual[key]
        for field in ["code", "text", "consumed", "tail", "rank"]:
            if a[field] != b[field]: raise AssertionError((key, field))
        if [c["text"] for c in a["candidates"]] != [c["text"] for c in b["candidates"]]: raise AssertionError((key, "candidate order"))
        for x, y in zip(a["candidates"], b["candidates"]):
            error = abs(x["score"] - y["score"])
            maximum_error = max(maximum_error, error)
            if error > 1e-8: raise AssertionError((key, "score", error))
            if x["segments"] != y["segments"]: raise AssertionError((key, "pronunciation/segmentation path"))
    def sha(path):
        with path.open("rb") as f: return hashlib.file_digest(f, "sha256").hexdigest()
    result = dict(passed=True, records=len(actual), top50_exact=True, pronunciation_paths_exact=True,
                  maximum_score_error=maximum_error, baseline_sha256=sha(args.baseline), actual_sha256=sha(args.actual))
    with args.output.open("x", encoding="utf-8") as f: json.dump(result, f, indent=2)
    print(json.dumps(result))
if __name__ == "__main__": main()
