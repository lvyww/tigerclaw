#!/usr/bin/env python3
"""Read-only A/B interim report while the independent Q4 stage continues."""
import argparse
import collections
import csv
import itertools
import json
from pathlib import Path

import numpy as np

from prepare_qwen_quick_eval import identity
from qwen_full_eval import SOURCES, replay_fusion, rows, save


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    args = parser.parse_args()
    work = args.work.resolve()
    output = work / 'interim-ab'
    output.mkdir(exist_ok=True)
    expected = json.loads((work / 'cases.json').read_text())
    assert collections.Counter(r['source'] for r in expected if not r['diagnostic']) == SOURCES
    for name in ('freeze-A', 'score-B'):
        assert json.loads((work / f'{name}-complete.json').read_text())['rows'] == len(expected)
    lifecycle = json.loads((work / 'score-B-lifecycle.json').read_text())
    assert lifecycle['normal_exit'] and lifecycle['exit_code'] == 0
    assert json.loads((work / 'compatibility-B.json').read_text())['pass']
    manifests = [json.loads((work / f'{name}-manifest.json').read_text()) for name in ('freeze-A', 'score-B')]
    for key in ('cases', 'core', 'tool', 'inputs', 'high_freq_limit', 'duplicate_single', 'learning', 'early_commit', 'policy'):
        assert manifests[0][key] == manifests[1][key], key
    counts = {source: collections.Counter() for source in [*SOURCES, 'all']}
    paired = {source: collections.Counter() for source in SOURCES}
    diagnostics = []
    with (output / 'changes-B-vs-A.jsonl').open('w') as changes, (output / 'changes-B-vs-A.csv').open('w', newline='', encoding='utf-8-sig') as csvfile:
        writer = csv.writer(csvfile)
        writer.writerow(['id', 'source', 'code', 'target', 'A', 'B', 'outcome', 'error'])
        for item, base, neural in itertools.zip_longest(expected, rows(work / 'freeze-A.jsonl'), rows(work / 'score-B.jsonl')):
            assert all(r is not None for r in (item, base, neural))
            assert item == base['item'] == neural['item']
            assert base['ranking'] == neural['ranking']
            pool, final = base['pool'], neural['final']
            a = pool[0]['text'] if pool else None
            b = final[0]['text'] if final else None
            error = neural['error']
            if not error and len(pool) > 1:
                for actual, cached in zip(final, replay_fusion(pool, base['ranking'], neural['scores'])):
                    assert actual['text'] == cached['text'] and abs(actual['fused'] - cached['fused']) < 1e-9
                assert [r['text'] for r in final[5:]] == [r['text'] for r in pool[5:]]
            else:
                assert error or a == b
            if item['diagnostic']:
                diagnostics.append(dict(item=item, pool=pool, scores=neural['scores'], final=final))
                continue
            target = item['text']
            correct_a, correct_b = a == target, b == target
            outcome = 'rescue' if correct_b and not correct_a else 'regress' if correct_a and not correct_b else 'both-wrong-change'
            for source in (item['source'], 'all'):
                c = counts[source]
                c.update(n=1, A=int(correct_a), B=int(correct_b), top5=int(any(r['text'] == target for r in pool[:5])),
                         errors=int(bool(error)), timeouts=int('TimeoutException' in (error or '')),
                         calls=int(bool(neural['scores'])), single=int(len(pool) == 1), empty=int(not pool),
                         changed=int(a != b), rescue=int(correct_b and not correct_a and not error),
                         regress=int(correct_a and not correct_b and not error))
            if not error:
                paired[item['source']][(correct_a, correct_b)] += 1
            if a != b or error:
                changes.write(json.dumps(dict(item=item, A=a, B=b, outcome=outcome, error=error,
                    pool=pool, ranking=base['ranking'], q8_scores=neural['scores'], final=final), ensure_ascii=False) + '\n')
                writer.writerow([item[k] for k in ('id', 'source', 'code', 'text')] + [a, b, outcome, error])
    assert counts['all']['n'] == 73129 and len(diagnostics) == 2
    rng = np.random.default_rng(20261004)
    draws = np.zeros(50000)
    n = 0
    for c in paired.values():
        size = sum(c.values())
        pos, neg = c[False, True], c[True, False]
        sample = rng.multinomial(size, [pos / size, neg / size, 1 - (pos + neg) / size], size=50000)
        draws += sample[:, 0] - sample[:, 1]
        n += size
    overall = counts['all']
    comparison = dict(paired_n=n, excluded_failures=73129-n, rescue=overall['rescue'], regress=overall['regress'],
        net=overall['rescue']-overall['regress'], delta_pp=(overall['rescue']-overall['regress'])*100/n,
        paired_bootstrap_95_pp=(np.quantile(draws, [.025, .975])*100/n).tolist())
    result = dict(complete_for_AB=True, C_included=False, counts=counts, comparison=comparison,
                  diagnostics=diagnostics, production_fusion_cache_replay=True,
                  inputs=[identity(work / name) for name in ('cases.json', 'freeze-A.jsonl', 'score-B.jsonl')])
    save(output / 'summary.json', result)
    lines = ['# 全量未重排与 Q8 汇总（A/B 已完成）', '',
        '仅使用已完成的 A/B 全量文件；不读取、改写或中断正在运行的 Q4。当前生产 C# 解码器及冻结日用配置，学习和提前上屏关闭。', '',
        '| 来源 | N | A 未重排 | B Q8 | 救回 | 改错 | 净变化 | 基础 Top-5 |',
        '|---|---:|---:|---:|---:|---:|---:|---:|']
    for source, c in counts.items():
        lines.append(f"| {source} | {c['n']} | {c['A']} ({c['A']/c['n']:.4%}) | {c['B']} ({c['B']/c['n']:.4%}) | {c['rescue']} | {c['regress']} | {c['rescue']-c['regress']:+d} | {c['top5']} ({c['top5']/c['n']:.4%}) |")
    lines += ['', f"Q8−未重排：{comparison['delta_pp']:+.4f} 个百分点，配对 bootstrap 95% 区间 [{comparison['paired_bootstrap_95_pp'][0]:+.4f}, {comparison['paired_bootstrap_95_pp'][1]:+.4f}]。",
        f"神经调用 {overall['calls']} 条，单候选 {overall['single']} 条，空候选 {overall['empty']} 条；全部计入分母。错误 {overall['errors']}，超时 {overall['timeouts']}；配对统计排除失败，不将回退当作成功重排。",
        f"共有 {overall['changed']} 条首选变化，全部保存至 changes-B-vs-A.csv/jsonl，包括两个版本都不正确的变化。",
        '全部行通过输入对应关系、冻结排序参数、独立缓存融合回放及后五项不变校验。Q8 兼容性与正常退出已通过。', '',
        '区间按来源分层、逐行配对重采样 50000 次，种子 20261004。完整保留 73129 行及其中的重复目标；历史训练重合、重复目标及反复实验的相关性未在区间中校正，不能当作独立泛化证据。Q4 尚未纳入本报告。']
    (output / 'REPORT.md').write_text('\n\n'.join(lines) + '\n')
    # Keep table rows contiguous in Markdown.
    text = (output / 'REPORT.md').read_text().replace('|\n\n|', '|\n|')
    (output / 'REPORT.md').write_text(text)
    print(json.dumps(dict(counts=counts, comparison=comparison), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
