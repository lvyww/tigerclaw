"""Select suffix-closed context thresholds by count error, never accuracy."""
import argparse
import csv
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('histogram', type=Path)
parser.add_argument('output', type=Path)
args = parser.parse_args()
with args.histogram.open() as stream:
    rows = list(csv.DictReader(stream, delimiter='\t'))
assert [int(row['rank']) for row in rows] == list(range(len(rows)))
histograms = [[int(row[f'order{n}']) for row in rows] for n in range(2, 6)]
targets = [7959327, 69562625, 10273459, 8415769]
limits = [len(rows) - 1, 1024, 128, 128]


def error(order, threshold):
    return ((histograms[order][threshold] - targets[order]) / targets[order]) ** 2


states = {k: (error(3, k), (k,)) for k in range(limits[3] + 1)}
for order in (2, 1, 0):
    next_states, best = {}, None
    for k in range(limits[order] + 1):
        if k in states and (best is None or states[k] < best):
            best = states[k]
        if best is not None:
            next_states[k] = (error(order, k) + best[0], (k,) + best[1])
    states = next_states
objective, thresholds = min(states.values())
actual = [histograms[i][k] for i, k in enumerate(thresholds)]
record = dict(target_counts=targets, thresholds=thresholds, actual_counts=actual,
              relative_errors=[a / t - 1 for a, t in zip(actual, targets)],
              objective=objective,
              selection='minimum sum squared relative count error with nonincreasing thresholds; no accuracy data')
args.output.write_text(json.dumps(record, indent=2) + '\n')
print(json.dumps(record, indent=2))
