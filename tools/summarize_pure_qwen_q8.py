#!/usr/bin/env python3
"""Pure Qwen total-log-probability reranking within the frozen n-gram top five."""
import argparse
import collections
import csv
import itertools
import json
import math
from pathlib import Path

import numpy as np

from prepare_qwen_quick_eval import identity
from qwen_full_eval import SOURCES, rows, save


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    args = parser.parse_args()
    work = args.work.resolve()
    output = work / 'pure-qwen-q8'
    output.mkdir(exist_ok=True)
    cases = json.loads((work / 'cases.json').read_text())
    for name in ('freeze-A', 'score-B'):
        assert json.loads((work / f'{name}-complete.json').read_text())['rows'] == len(cases)
    counts = {s:collections.Counter() for s in [*SOURCES,'all']}
    paired = {ref:{s:collections.Counter() for s in SOURCES} for ref in ('A','B')}
    changes = {ref:(output/f'changes-pure-vs-{ref}.csv').open('w',newline='',encoding='utf-8-sig') for ref in ('A','B')}
    writers = {ref:csv.writer(f) for ref,f in changes.items()}
    for writer in writers.values():
        writer.writerow(['id','source','code','target','ngram_top','fusion_top','pure_qwen_top','outcome','error'])
    try:
        with (output/'predictions.jsonl').open('w') as predictions:
            for item,base,neural in itertools.zip_longest(cases,rows(work/'freeze-A.jsonl'),rows(work/'score-B.jsonl')):
                assert all(x is not None for x in (item,base,neural))
                assert item == base['item'] == neural['item']
                pool,scores,error = base['pool'],neural['scores'],neural['error']
                a = pool[0]['text'] if pool else None
                b = neural['final'][0]['text'] if neural['final'] else None
                tied = False
                if error:
                    pure = None
                elif len(pool) <= 1:
                    assert not scores
                    pure = a
                else:
                    assert len(scores) == min(5,len(pool)) and all(math.isfinite(x) for x in scores)
                    # No base score, lexicon rank or candidate-length weight.
                    order = sorted(range(len(scores)),key=lambda i:(-scores[i],pool[i]['text'].encode('utf-16-be')))
                    pure = pool[order[0]]['text']
                    assert scores[order[0]] == max(scores)
                    tied = sum(x == max(scores) for x in scores) > 1
                tops = {'A':a,'B':b,'pure':pure}
                predictions.write(json.dumps(dict(item=item,**tops,error=error,top_score_tie=tied),ensure_ascii=False)+'\n')
                if item['diagnostic']:
                    continue
                correct = {k:v == item['text'] for k,v in tops.items()}
                for source in (item['source'],'all'):
                    counts[source].update(n=1,**{k:int(v) for k,v in correct.items()},
                        top5=int(any(c['text'] == item['text'] for c in pool[:5])),errors=int(bool(error)),
                        timeouts=int('TimeoutException' in (error or '')),ties=int(tied),single=int(len(pool)==1),empty=int(not pool))
                for ref in ('A','B'):
                    if not error:
                        paired[ref][item['source']][correct[ref],correct['pure']] += 1
                    if pure != tops[ref] or error:
                        outcome = 'rescue' if correct['pure'] and not correct[ref] else 'regress' if correct[ref] and not correct['pure'] else 'both-wrong-change'
                        writers[ref].writerow([item[k] for k in ('id','source','code','text')]+[a,b,pure,outcome,error])
    finally:
        for f in changes.values(): f.close()
    assert {s:c['n'] for s,c in counts.items() if s != 'all'} == SOURCES
    assert counts['all']['n'] == 73129
    previous = json.loads((work/'interim-ab/summary.json').read_text())['counts']
    assert all(counts[s][k] == previous[s][k] for s in counts for k in ('A','B','n','top5','errors'))
    comparisons = {}
    for ref in ('A','B'):
        rng = np.random.default_rng(20261004)
        samples = np.zeros(50000)
        rescue = regress = n = 0
        for c in paired[ref].values():
            size = sum(c.values()); pos,neg = c[False,True],c[True,False]
            sample = rng.multinomial(size,[pos/size,neg/size,1-(pos+neg)/size],size=50000)
            samples += sample[:,0]-sample[:,1]
            rescue += pos; regress += neg; n += size
        comparisons['pure_vs_'+ref] = dict(n=n,rescue=rescue,regress=regress,net=rescue-regress,
            delta_pp=(rescue-regress)*100/n,paired_bootstrap_95_pp=(np.quantile(samples,[.025,.975])*100/n).tolist())
    result = dict(counts=counts,comparisons=comparisons,model_calls=0,
        policy='highest raw BOS+text+EOS Qwen Q8 total log probability among frozen n-gram top five; exact ties UTF-16 Ordinal; no length normalization or lexicon-rank priority',
        inputs=[identity(work/name) for name in ('cases.json','freeze-A.jsonl','score-B.jsonl')])
    save(output/'summary.json',result)
    lines = ['# 纯 Qwen Q8 缓存排序评测','',
        '复用已完成的全部 73129 条 Q8 原始分数，无模型重跑。在 n-gram 的原始前五候选中只按 Qwen 的 BOS＋原文＋EOS 总对数概率降序排序，不加 n-gram 分，不保留码表 rank 优先，不做长度归一化。精确同分按 UTF-16 Ordinal；单候选保持原项，空候选仍计入分母。此口径不是 Qwen 独立生成，也不是全候选空间的准确率。','',
        '| 来源 | 条数 | 未重排 | 当前加权融合 | 纯 Qwen Q8 | 基础 Top-5 入池 |',
        '|---|---:|---:|---:|---:|---:|']
    for source,c in counts.items():
        lines.append(f"| {source} | {c['n']} | "+' | '.join(f"{c[k]} ({c[k]/c['n']:.4%})" for k in ('A','B','pure','top5'))+' |')
    lines += ['',f"错误 {counts['all']['errors']}，超时 {counts['all']['timeouts']}，首位精确同分 {counts['all']['ties']} 条。所有失败独立记录，不算作成功回退。",'']
    for name,r in comparisons.items():
        lines.append(f"- {name}：救回 {r['rescue']}，改错 {r['regress']}，净 {r['net']:+d}；差值 {r['delta_pp']:+.4f} 个百分点；配对 95% 区间 [{r['paired_bootstrap_95_pp'][0]:+.4f}, {r['paired_bootstrap_95_pp'][1]:+.4f}]。")
    lines += ['','全部逐条首选保留在 predictions.jsonl；两份 changes-pure-vs-A/B.csv 保存全部首选变化。原始候选、分数与模型仍保存在父目录，未被改写。',
        '配对区间按来源分层重采样50000次，种子20261004；历史数据、重复目标和训练重合的相关性未校正，不是独立泛化结论。没有调参、部署或中断Q4评测。']
    (output/'REPORT.md').write_text('\n'.join(lines)+'\n')
    print(json.dumps(dict(counts=counts,comparisons=comparisons),ensure_ascii=False,indent=2))


if __name__ == '__main__':
    main()
