#!/usr/bin/env python3
"""One fixed, cache-only long-sentence additive lambda hypothesis; no fitting."""
import argparse
import collections
import csv
import itertools
import json
import math
from pathlib import Path

import numpy as np

from prepare_qwen_quick_eval import identity
from qwen_full_eval import SOURCES, replay_fusion, rows, save


def main():
    p = argparse.ArgumentParser()
    p.add_argument('work',type=Path)
    p.add_argument('--long-lambda',type=float,required=True)
    p.add_argument('--model',choices=('q8','q4'),default='q8')
    a = p.parse_args()
    assert math.isfinite(a.long_lambda) and a.long_lambda >= 0
    work = a.work.resolve()
    label = 'B' if a.model == 'q8' else 'C'
    score_file = f'score-{label}.jsonl'
    out = work / (a.model + '-long-lambda-' + str(a.long_lambda).replace('.','_'))
    out.mkdir(exist_ok=True)
    cases = json.loads((work/'cases.json').read_text())
    for phase in ('freeze-A',f'score-{label}'):
        assert json.loads((work/f'{phase}-complete.json').read_text())['rows'] == len(cases)
    counts = {s:collections.Counter() for s in [*SOURCES,'all']}
    paired = {s:collections.Counter() for s in SOURCES}
    with (out/'predictions.jsonl').open('w') as predictions, (out/'changes-vs-current.csv').open('w',newline='',encoding='utf-8-sig') as changes:
        writer = csv.writer(changes)
        writer.writerow(['id','source','code','target','ngram_top','current_fusion_top','new_top','outcome','error'])
        for item,base,row in itertools.zip_longest(cases,rows(work/'freeze-A.jsonl'),rows(work/score_file)):
            assert all(x is not None for x in (item,base,row)) and item == base['item'] == row['item']
            assert base['ranking'] == row['ranking']
            pool, original, scores = base['pool'],base['ranking'],row['scores']
            ng = pool[0]['text'] if pool else None
            current = row['final'][0]['text'] if row['final'] else None
            eligible = original['policy'] == 'shared-additive' and original['base_top_length'] > 6 and len(pool) > 1
            ranking = dict(original)
            if eligible:
                ranking['additive_weight'] = a.long_lambda
            new_top, new_final = ng, []
            if row['error']:
                new_top = None
            elif len(pool) > 1:
                baseline = replay_fusion(pool,original,scores)
                for actual,expected in zip(row['final'],baseline):
                    assert actual['text'] == expected['text'] and abs(actual['fused']-expected['fused']) < 1e-9
                new_final = replay_fusion(pool,ranking,scores)
                new_top = new_final[0]['text']
                if not eligible or a.long_lambda == original['additive_weight']:
                    assert new_top == current
            predictions.write(json.dumps(dict(item=item,ngram=ng,current=current,new=new_top,
                eligible=eligible,error=row['error'],ranking=ranking,reranked=new_final),ensure_ascii=False)+'\n')
            if item['diagnostic']:
                continue
            correct_ng,correct_current,correct_new = (x == item['text'] for x in (ng,current,new_top))
            error = row['error']
            for source in (item['source'],'all'):
                counts[source].update(n=1,A=int(correct_ng),current=int(correct_current),new=int(correct_new),
                    eligible=int(eligible),changed=int(current!=new_top),errors=int(bool(error)),
                    rescue=int(correct_new and not correct_current and not error),regress=int(correct_current and not correct_new and not error))
            if not error:
                paired[item['source']][correct_current,correct_new] += 1
            if current != new_top or error:
                outcome = 'rescue' if correct_new and not correct_current else 'regress' if correct_current and not correct_new else 'both-wrong-change'
                writer.writerow([item[k] for k in ('id','source','code','text')]+[ng,current,new_top,outcome,error])
    assert {s:c['n'] for s,c in counts.items() if s != 'all'} == SOURCES
    assert counts['all']['n'] == 73129
    prior = json.loads((work/'interim-ab/summary.json').read_text())['counts']
    assert all(counts[s]['A'] == prior[s]['A'] for s in counts)
    if label == 'B':
        assert all(counts[s]['current'] == prior[s]['B'] for s in counts)
    else:
        full = json.loads((work/'summary.json').read_text())['accuracy']
        assert all(counts[s]['current'] == full[s]['correct'][label] for s in counts)
    rng = np.random.default_rng(20261004)
    samples = np.zeros(50000)
    n = 0
    for c in paired.values():
        size = sum(c.values()); pos,neg = c[False,True],c[True,False]
        draws = rng.multinomial(size,[pos/size,neg/size,1-(pos+neg)/size],size=50000)
        samples += draws[:,0]-draws[:,1]; n += size
    total = counts['all']
    comparison = dict(n=n,rescue=total['rescue'],regress=total['regress'],net=total['rescue']-total['regress'],
        delta_pp=(total['rescue']-total['regress'])*100/n,paired_bootstrap_95_pp=(np.quantile(samples,[.025,.975])*100/n).tolist())
    result = dict(model=a.model,long_lambda=a.long_lambda,scope='only shared-additive branch with base winner longer than six text elements; all convex alphas and short additive weights unchanged',
        counts=counts,comparison=comparison,new_model_calls=0,production_modified=False,
        inputs=[identity(work/name) for name in ('cases.json','freeze-A.jsonl',score_file)])
    save(out/'summary.json',result)
    lines = [f'# {a.model.upper()} 长句加法 λ={a.long_lambda} 缓存试算','',
        f'只将基础首选超过六字的加法分支改为 BaseScore + {a.long_lambda} × QwenScore。2～6字首选的候选凸组合 alpha 完全保持原值（包括范围外候选的回退 alpha），一字首选的加法权重也不变。前五候选与生产比较器不变；无模型重跑，无生产修改。','',
        '| 来源 | N | 未重排 | 当前融合 | 新 λ | 救回 | 改错 | 净变化 |','|---|---:|---:|---:|---:|---:|---:|---:|']
    for source,c in counts.items():
        lines.append(f"| {source} | {c['n']} | "+' | '.join(f"{c[k]} ({c[k]/c['n']:.4%})" for k in ('A','current','new'))+f" | {c['rescue']} | {c['regress']} | {c['rescue']-c['regress']:+d} |")
    lines += ['',f"适用新权重 {total['eligible']} 条，首选变化 {total['changed']} 条，错误 {total['errors']} 条。两组诊断保留但不计准确率。",
        f"相对当前融合：{comparison['delta_pp']:+.4f} 个百分点；配对 bootstrap 95% 区间 [{comparison['paired_bootstrap_95_pp'][0]:+.4f}, {comparison['paired_bootstrap_95_pp'][1]:+.4f}]。",
        '完整逐条试算保存在 predictions.jsonl，全部首选变化保存在 changes-vs-current.csv；原始文件不改写。',
        '这是已查看历史集上的固定假设探索，不是独立验证或最优权重证明。区间按来源分层、逐行配对重采样50000次，种子20261004；重复目标及训练重合相关性未校正。原始 A/B/C 实验结果仍保留冻结权重。']
    (out/'REPORT.md').write_text('\n'.join(lines)+'\n')
    print(json.dumps(dict(counts=counts,comparison=comparison),ensure_ascii=False,indent=2))


if __name__ == '__main__':
    main()
