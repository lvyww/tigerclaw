"""Select a log-score interpolation weight exclusively on the frozen dev split."""
from budget500 import O, rows, dump

base = {r['id']: {c['text']: c['score'] for c in r['candidates']} for r in rows(O/'dev3q8.jsonl')}
alphas = [i/8 for i in range(9)]
correct = {a: 0 for a in alphas}
count = 0
for r in rows(O/'dev-wd1e8-rerank.jsonl'):
    b = base[r['id']]
    assert set(b) == {c['text'] for c in r['candidates']}
    for a in alphas:
        first = min(r['candidates'], key=lambda c: (-(b[c['text']] + a*(c['score']-b[c['text']])), -c['frequency'], c['text'].encode('utf-16-be')))
        correct[a] += first['text'] == r['text'] and r['consumed'] == len(r['code'])
    count += 1
assert count == 1981
best = max(alphas, key=lambda a: (correct[a], -a))
report = {'n': count, 'correct': correct, 'selectedAlpha': best,
          'rule': 'highest dev exact Top1, tie lower fivegram weight; score=(1-alpha)*trigram+alpha*pruned-fivegram',
          'model': 'wd1e8', 'totalBytes': 466340652 if best else 227091255}
dump(O/'fusion-dev-selection.json', report)
print(report)
