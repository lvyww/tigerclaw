"""Record-grouped offline Qwen calibration; runtime features never use target length."""
import argparse
import hashlib
import json
from pathlib import Path
import numpy as np

p = argparse.ArgumentParser()
p.add_argument('directory', type=Path)
a = p.parse_args()
cases = json.loads((a.directory / 'cases.json').read_text())
rows = sorted((json.loads(line) for f in a.directory.glob('rows-*.jsonl')
               for line in f.open(encoding='utf-8')), key=lambda r: r['id'])
assert len(rows) == 50000 and [r['id'] for r in rows] == list(range(50000))
n = len(rows)
base = np.full((n, 5), -np.inf)
neural = np.zeros((n, 5))
length = np.zeros((n, 5), dtype=int)
target = np.full(n, -1)
original = np.zeros(n, dtype=int)
old_weight = np.zeros(n)
truth_length = np.array([r['length'] for r in rows])
train = np.array([int.from_bytes(hashlib.sha256(
    f"qwen-calibration-v1:{c['source']}:{c['record']}".encode()).digest()[:8], 'big') % 10 < 7
    for c in cases])
for i, r in enumerate(rows):
    assert 'scoring_policy' not in r, 'This calibration requires the original additive-score baseline'
    for j, c in enumerate(r['pool'][:5]):
        base[i, j] = c['score']
        length[i, j] = len(c['text'])
        if c['text'] == r['text']:
            target[i] = j
        if c['text'] == r['qwen_top']:
            original[i] = j
    neural[i, :len(r['qwen_scores'])] = r['qwen_scores']
    old_weight[i] = r['weight']

# Check that score-first replay agrees with the actual production comparator.
# Fail closed if lexicon priority/tie rules make the stored features insufficient.
assert np.all(np.argmax(base, axis=1) == 0), 'Base comparator not score-first'
assert np.all(np.argmax(base + old_weight[:, None] * neural, axis=1) == original), 'Production replay differs'
bucket = np.clip(length[:, 0], 2, 6) - 2
supported = (length[:, 0] >= 2) & (length[:, 0] <= 6)
mixed = np.any((length != length[:, :1]) & np.isfinite(base), axis=1)
grid = np.round(np.arange(0, 1.201, .05), 2)

def predict(weights, correction=0):
    w = np.asarray(weights)[bucket]
    w = np.where(supported, w, old_weight)
    return np.argmax(base + w[:, None] * (neural + correction * (length - length[:, :1])), axis=1)

def fit(correction):
    weights = []
    for b in range(5):
        m = train & supported & (bucket == b)
        scores = [(int(np.sum(np.argmax(base[m] + w * (neural[m] + correction *
                    (length[m] - length[m, :1])), axis=1) == target[m])), -w, w) for w in grid]
        weights.append(max(scores)[2])
    hits = int(np.sum((predict(weights, correction) == target) & train))
    return hits, weights

plain_hits, plain_weights = fit(0)
trials = [(fit(float(c)), float(c)) for c in np.arange(0, 16.01, .5)]
(best_hits, best_weights), correction = max(trials, key=lambda x: (x[0][0], -x[1]))
policies = {'off': ([0]*5, 0), 'length_weights': (plain_weights, 0),
            'length_corrected': (best_weights, correction)}
report = {'split': 'SHA256 source+record, 70% train / 30% validation; seed qwen-calibration-v1',
          'grid': grid.tolist(), 'correction_grid': [0, 16, .5], 'policies': {},
          'train_trials': [{'correction': c, 'hits': fit_result[0], 'weights': fit_result[1]}
                           for fit_result, c in trials]}
# Choose a family using grouped cross-validation INSIDE the training partition.
# Every candidate of one input shares one weight, including unequal lengths.
fold = np.array([int.from_bytes(hashlib.sha256(
    f"qwen-inner-v1:{c['source']}:{c['record']}".encode()).digest()[:8], 'big') % 5 for c in cases])
groups = {'constant': np.zeros(n, dtype=int), 'length': bucket,
          'mixed': mixed.astype(int), 'length_mixed': bucket * 2 + mixed}
predictions = np.array([np.argmax(base + w * neural, axis=1) for w in grid])
predictions[:, ~supported] = original[~supported]
correct_grid = predictions == target
def fit_groups(group, mask):
    return [int(np.argmax(np.sum(correct_grid[:, mask & (group == g)], axis=1)))
            for g in range(int(group.max()) + 1)]
cv = {}
for family, group in groups.items():
    hits = 0
    for f in range(5):
        selected = np.array(fit_groups(group, train & (fold != f)))
        mask = train & (fold == f)
        hits += int(np.sum(correct_grid[selected[group[mask]], np.flatnonzero(mask)]))
    selected = np.array(fit_groups(group, train))
    cv[family] = dict(hits=hits, parameters=len(selected), weights=grid[selected].tolist())
family = max(cv, key=lambda k: (cv[k]['hits'], -cv[k]['parameters']))
group = groups[family]
selected_prediction = np.argmax(base + np.where(supported, np.array(cv[family]['weights'])[group],
                                                old_weight)[:, None] * neural, axis=1)
report['inner_cv'] = cv
report['selected_family'] = family
(a.directory/'calibration-predictions.json').write_text(json.dumps(selected_prediction.tolist()), encoding='utf-8')
print('INNER CV', cv, 'SELECTED', family, flush=True)
policies['selected'] = (cv[family]['weights'], 0)
for name, spec in [('current', None)] + list(policies.items()):
    predicted = (np.zeros(n, dtype=int) if name == 'off' else selected_prediction if name == 'selected'
                 else original if spec is None else predict(*spec))
    good = predicted == target
    old_good = original == target
    result = {'weights': None if spec is None else spec[0], 'correction': None if spec is None else spec[1]}
    for part, mask in [('train', train), ('validation', ~train), ('all', np.ones(n, dtype=bool))]:
        result[part] = []
        for category, m in [('total', mask), ('mixed', mask & mixed), ('same', mask & ~mixed)] + [
                (str(k), mask & (truth_length == k)) for k in range(2, 7)]:
            fixes = int(np.sum(m & good & ~old_good))
            regressions = int(np.sum(m & ~good & old_good))
            count = int(m.sum())
            delta = (fixes - regressions) / count
            se = np.sqrt(((fixes + regressions) / count - delta**2) / count)
            result[part].append(dict(category=category, n=count, correct=int(np.sum(m & good)),
                                     accuracy=float(good[m].mean()), fixes=fixes, regressions=regressions,
                                     delta_95ci_pp=[100*(delta-1.96*se),100*(delta+1.96*se)]))
    report['policies'][name] = result
    print(name, 'weights', result['weights'], 'correction', result['correction'], flush=True)
    for part in ('train','validation'):
        print(part, result[part][:3], flush=True)
(a.directory/'calibration.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
