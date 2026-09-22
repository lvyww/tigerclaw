"""Compare complete frozen signatures, or summarize serial benchmark CSVs."""
import argparse
import csv
import json
import math
import re
from pathlib import Path


def records(path):
    return [line for line in Path(path).read_text(encoding="utf-8").splitlines()
            if line and not line.startswith("#")]


def benchmark(path):
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    if not lines[-1].startswith("# private_bytes="):
        raise ValueError(f"Incomplete benchmark: {path}")
    rows = list(csv.DictReader(records(path)))
    warm = [row for row in rows if row["round"] in ("1", "2")]
    if len(rows) != 345 or len(warm) != 230:
        raise ValueError(f"Wrong sample count: {path}")
    times = sorted(sum(float(row[key]) for key in ("decode_ms", "rank_ms", "menu_ms"))
                   for row in warm)
    return {
        "file": str(path), "hot_keys": len(warm),
        "load_ms": float(re.search(r"load_ms=([\d.]+)", lines[0])[1]),
        "median_ms": (times[114] + times[115]) / 2,
        "p95_ms": times[math.ceil(len(times) * .95) - 1],
        "maximum_ms": times[-1],
        "private_bytes": int(re.search(r"private_bytes=(\d+)", lines[-1])[1]),
    }


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("mode", choices=("compare", "bench"))
parser.add_argument("files", nargs="+")
args = parser.parse_args()
if args.mode == "compare":
    if len(args.files) != 2:
        parser.error("compare requires baseline and optimized TSVs")
    before, after = map(records, args.files)
    mismatches = [i + 1 for i, (a, b) in enumerate(zip(before, after)) if a != b]
    valid = len(before) == len(after) == 8241 and not mismatches
    print(json.dumps({"baseline": len(before), "optimized": len(after),
                      "mismatch_rows": mismatches, "passed": valid}, indent=2))
    raise SystemExit(0 if valid else 1)
print(json.dumps([benchmark(path) for path in args.files], indent=2))
