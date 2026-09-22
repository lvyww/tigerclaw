import json
import os
import subprocess
from budget500 import O, E, B, REPO, rows, dump, local_input

selection = json.loads((O/'dev-selection.json').read_text())
cases = []
for r in rows(REPO/'next/_run/FullPinyin/youhua-issue/exact-200.jsonl'):
    cases.append({k: r[k] for k in ('id', 'code', 'text')})
cases.append({'id': 'feedback-now-youhua', 'text': '现在有话没地方说去', 'code': 'xianzaiyouhuameidifangshuoqu'})
for r in cases:
    r.update(split='test', source='User feedback; not part of dev/test metrics')
source = O/'feedback-cases.jsonl'
source.write_text(''.join(json.dumps(r, ensure_ascii=False)+'\n' for r in cases))
env = os.environ.copy()
env['LD_LIBRARY_PATH'] = str(E/'bin')
table = local_input(REPO/'release/拼音反查码表/拼音.txt')
tokens = local_input(B/'tokens.json')
report = {}
for name, model in [('q8', O/'models/joint3-q8.klm'), ('fusion', O/'models/joint3-q8.klm'), ('single', O/'models'/selection['singleModel'])]:
    env.pop('BUDGET_RERANK_MODEL', None)
    env.pop('BUDGET_RERANK_ALPHA', None)
    binary = E/'bench-bin/Joint.dll'
    if name == 'fusion':
        binary = O/'combo-v2-bin/Joint.dll'
        env['BUDGET_RERANK_MODEL'] = str(local_input(O/'models/wd1e8.klm'))
        env['BUDGET_RERANK_ALPHA'] = '0.625'
    out = O/(name+'-feedback.jsonl')
    with (O/'logs'/(name+'-feedback.log')).open('x') as log:
        subprocess.run([str(B/'dotnet/dotnet'), str(binary), 'decode', 'joint', str(local_input(model)),
                        str(table), str(tokens), str(local_input(source)), str(out), '0', '1'],
                       env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    report[name] = [{'target': r['text'], 'code': r['code'], 'top': r['candidates'][0]['text'] if r['candidates'] else '',
                     'rank': r['rank'], 'fullConsumption': r['consumed'] == len(r['code'])} for r in rows(out)]
dump(O/'feedback-report.json', report)
print(json.dumps(report, ensure_ascii=False, indent=2))
