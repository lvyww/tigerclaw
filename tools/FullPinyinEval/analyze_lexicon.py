"""Strict paired analysis for a dictionary-only full-pinyin ablation."""
import argparse
import json
from pathlib import Path
import analyze as m


def legal_output(text, code, entries, max_word):
    """Exact text+raw reachability, independent of Beam and expected annotation."""
    reachable = {(0, 0)}
    for at in range(len(text)):
        positions = [raw for pos, raw in reachable if pos == at]
        for end in range(at + 1, min(len(text), at + max_word) + 1):
            for spelling in entries.get(text[at:end], ()):
                for raw in positions:
                    if code.startswith(spelling, raw):
                        reachable.add((end, raw + len(spelling)))
    return (len(text), len(code)) in reachable


def coverage(root, cases):
    path = root / 'coverage.json'
    table = root / 'wanxiang-fullpinyin.txt'
    identity = dict(table=m.digest(table), cases=m.digest(root / 'data/cases.jsonl'))
    if path.exists():
        saved = json.loads(path.read_text())
        assert saved['sha256'] == identity
        return saved
    entries = set()
    with table.open() as stream:
        for line in stream:
            text, code, _ = line.rstrip().split('\t')
            entries.add((text, code))
    result = {}
    for i, row in cases.items():
        text, syllables = row['text'], row['syllables']
        positions = {0}
        for end in range(1, len(text) + 1):
            if any((text[start:end], ''.join(syllables[start:end])) in entries
                   for start in tuple(positions)):
                positions.add(end)
        result[i] = dict(covered=len(text) in positions,
                         directEntry=(text, row['code']) in entries,
                         singleCovered=all((c, s) in entries for c, s in zip(text, syllables)))
    saved = dict(sha256=identity, rows=result)
    path.write_text(json.dumps(saved, ensure_ascii=False, indent=2))
    return saved


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('root', type=Path)
    p.add_argument('baseline', type=Path)
    p.add_argument('wanxiang', type=Path)
    p.add_argument('--baseline-table', type=Path)
    p.add_argument('--coverage-only', action='store_true')
    args = p.parse_args()
    root, base = args.root, args.baseline
    cases = m.indexed(m.read(root / 'data/cases.jsonl'))
    cov = coverage(root, cases)
    if args.coverage_only:
        print({k: sum(x[k] for i, x in cov['rows'].items() if cases[i]['split'] == 'test')
               for k in ['covered', 'singleCovered', 'directEntry']})
        return
    assert m.digest(root / 'data/cases.jsonl') == m.digest(base / 'data/cases.jsonl')
    new = m.validate(cases, m.decode_rows(root / 'test-words.jsonl'), 'test')
    old = m.validate(cases, m.decode_rows(base / 'test-words.jsonl'), 'test')
    exp = json.loads((root / 'experiment.json').read_text())
    merged = json.loads((root / 'test-words.jsonl.manifest.json').read_text())
    assert merged.pop('mergedSha256') == m.digest(root / 'test-words.jsonl')
    for meta in merged.values():
        for key in ['table', 'model', 'executable']:
            assert meta[key].lower() == exp[key]
        assert meta['beam'] == 200 and meta['words'] and meta['split'] == 'test'
    saved_base = json.loads((base / 'report.json').read_text())
    assert exp['model'] == saved_base['fingerprints']['decoder']['model'].lower()
    assert exp['executable'] == saved_base['fingerprints']['decoder']['executable'].lower()
    def summarize(rows):
        chars = sum(len(r['text']) for r in rows.values())
        errors = sum(m.edit_distance(r['text'], r['candidates'][0]['text'] if r['candidates'] else '')
                     for r in rows.values())
        return dict(**m.accuracy(list(rows.values())),
                    correct=sum(r['rank'] == 1 for r in rows.values()),
                    count=len(rows), characterErrorRate=errors / chars,
                    empty=sum(not r['candidates'] for r in rows.values()),
                    incomplete=sum(bool(r['tail']) for r in rows.values()))
    rescued, regressed = [], []
    original_entries = {}
    if args.baseline_table:
        assert m.digest(args.baseline_table) == saved_base['fingerprints']['decoder']['table'].lower()
        for line in args.baseline_table.read_text(encoding='utf-8-sig').splitlines():
            text, code, _ = line.split()
            original_entries.setdefault(text, set()).add(code)
    max_word = max(map(len, original_entries), default=0)
    score_delta = 0.0
    changed = 0
    for i, row in new.items():
        old_text = old[i]['candidates'][0]['text'] if old[i]['candidates'] else ''
        new_text = row['candidates'][0]['text'] if row['candidates'] else ''
        changed += old_text != new_text
        detail = dict(id=i, text=row['text'], code=row['code'], old=old_text, new=new_text,
                      oldRank=old[i]['rank'], newRank=row['rank'], coverage=cov['rows'][i])
        scores = {c['text']: c['score'] for c in old[i]['candidates']}
        for candidate in row['candidates']:
            if candidate['text'] in scores:
                score_delta = max(score_delta, abs(candidate['score'] - scores[candidate['text']]))
        if row['rank'] == 1 and old[i]['rank'] != 1:
            rescued.append(detail)
        elif old[i]['rank'] == 1 and row['rank'] != 1:
            delta = row['candidates'][0]['score'] - old[i]['candidates'][0]['score']
            detail['winnerScoreDifference'] = delta
            detail['reason'] = ('higher_scoring_competitor' if delta > 1e-8 else
                                'score_tie' if abs(delta) <= 1e-8 else 'search_loss')
            if original_entries:
                detail['newWinnerLegalInOldTable'] = legal_output(new_text, row['code'], original_entries, max_word)
            regressed.append(detail)
    latency, old_latency = m.read(root / 'latency.jsonl'), m.read(base / 'latency.jsonl')
    assert [(r['id'], r['direction'], r['keys']) for r in latency] == [
        (r['id'], r['direction'], r['keys']) for r in old_latency]
    def lat(rows):
        return {d: m.quantiles([r['ms'] for r in rows if r['direction'] == d])
                for d in ['append', 'backspace']}
    wx = json.loads((args.wanxiang / 'comparison.json').read_text())['wanxiang']
    memory = m.read(root / 'memory.jsonl')
    assert memory and all(x['component'] == 'decoder-client' for x in memory)
    report = dict(new=summarize(new), baseline=summarize(old), wanxiang=wx,
                  changedFirst=changed, rescued=rescued, regressed=regressed,
                  sharedCandidateMaxScoreDifference=score_delta,
                  regressionReasons={k: sum(x['reason'] == k for x in regressed)
                                     for k in ['higher_scoring_competitor', 'score_tie', 'search_loss']},
                  coverage={k: sum(cov['rows'][i][k] for i in new)
                            for k in ['covered', 'singleCovered', 'directEntry']},
                  newLatency=lat(latency), oldLatency=lat(old_latency),
                  singleProcessMemory=dict(peakWorkingSet=max(x['peakWorkingSet'] for x in memory),
                                           maxSampledPrivateBytes=max(x['privateBytes'] for x in memory)),
                  export=json.loads((root / 'wanxiang-fullpinyin.manifest.json').read_text()),
                  experiment=exp, qwenRun=False,
                  outputSha256={n: m.digest(root / n) for n in
                                ['test-words.jsonl', 'latency.jsonl', 'coverage.json']})
    assert report['new']['correct'] - report['baseline']['correct'] == len(rescued) - len(regressed)
    (root / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    lines = ['# m5 全拼原型更换万象词库（2026-09-21）', '',
             '8,019 条冻结测试句，原始编码、m5 模型、解码器二进制、Beam 200、每字奖励与排序方式保持相同。',
             '仅把虎爪拼音反查表替换成万象 Base v18.0.8 主字词库。按用户要求，本轮不测 Qwen。', '',
             '| 配置 | 首选 | Top-5 | Top-10 | Top-50 | 字符错误率↓ |',
             '|---|---:|---:|---:|---:|---:|']
    for label, r in [('原反查表 + m5', report['baseline']), ('万象词库 + m5', report['new'])]:
        lines.append(f'| {label} | ' + ' | '.join(f'{r[k]:.3%}' for k in
                     ['top1', 'top5', 'top10', 'top50', 'characterErrorRate']) + ' |')
    lines += [f"| 万象原方案 + 自带 LTS 语法模型 | {wx['top1']:.3%} | — | — | — | {wx['characterErrorRate']:.3%} |", '',
              f'更换词库后救回 {len(rescued)} 条、退化 {len(regressed)} 条，净变化 {len(rescued)-len(regressed):+d} 条；首选发生变化 {changed} 条。',
              '万象方案的后续菜单通常是局部词语，不将其召回与完整句子 Top-10 混算。', '',
              '## 逐键延迟（相同 Windows C# 解码器、相同 100 句，均不含 Qwen）', '',
              '| 词库 | 追加 p50 / p95 / p99 / 最慢 ms | 回删 p50 / p95 / p99 / 最慢 ms |',
              '|---|---:|---:|']
    for label, r in [('原反查表', report['oldLatency']), ('万象词库', report['newLatency'])]:
        values = [' / '.join(f'{r[d][k]:.2f}' for k in ['p50','p95','p99','max'])
                  for d in ['append', 'backspace']]
        lines.append(f'| {label} | {values[0]} | {values[1]} |')
    lines += ['', '## 词库转换与边界', '',
              f"- 当前实验进程单线程逐键测试的峰值工作集约 {report['singleProcessMemory']['peakWorkingSet']/1024**3:.2f} GiB，采样最大私有内存 {report['singleProcessMemory']['maxSampledPrivateBytes']/1024**3:.2f} GiB。这是现有 C# 实验 Trie/对象分配的成本，不能代表万象自身的内存需求。",
              '- 导出全部 16 个主词库 import_tables，包括单字、基础词、长词及专门词库；不加入英文、简码或混合编码表。',
              '- 去声调，ü 转 v，nve/lve 对齐冻结输入的 nue/lue；不派生简拼或模糊音。',
              '- 同字词同编码取最大频次，得到 2,202,473 条：49,353 条单字、2,153,120 条词组。原反查表 65,120 条。',
              '- 排除 15 条非拼音编码和 4 条非纯汉字正文；229 条负权重按现有解码器规则置零，353 条缺失权重及 1 条异常权重也置零。完整记录见导出 manifest。',
              '- 字词频次只用于模型同分时排序，未引入万象的词频评分或语法模型，也未训练新模型。',
              f"- 新表按冻结注音切分覆盖 {report['coverage']['covered']}/8019 句，单字覆盖 {report['coverage']['singleCovered']}/8019；其中 {report['coverage']['directEntry']} 条参考整句本身是新表直接词条。", '',
              '词库扩大同时改变读音覆盖和搜索路径；不能把差异全归于词频或模型参数。',
              '准确率按与唯一参考文本完全一致计算；注音仅抽检，并未证明语料与词库、模型训练数据独立。',
              '延迟不含启动／载入词库时间、前端 UI 和应用插入。完整候选与救回／退化案例在 test-words.jsonl、report.json。', '']
    lines += ['## 退化原因核查', '',
              f"新旧共有候选的最大 n-gram 分差：{score_delta:.9g}。",
              f"{report['regressionReasons']['higher_scoring_competitor']} 条退化中，新错误首选的模型分高于原正确首选；",
              f"{report['regressionReasons']['score_tie']} 条是模型同分，{report['regressionReasons']['search_loss']} 条是正确答案因搜索变化落后／丢失。"]
    if original_entries:
        lines.append(f"退化例中新错误首选有 {sum(not x['newWinnerLegalInOldTable'] for x in regressed)} 条在旧表中没有完整合法编码路径，其余在旧表中原本可表达。")
    lines += ['', '万象词库版本来源：https://github.com/amzxyz/rime-wanxiang/releases/tag/v18.0.8', '']
    (root / 'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps({k: report[k] for k in ['new','baseline','coverage','newLatency','oldLatency']}, indent=2))
    print('rescued', len(rescued), 'regressed', len(regressed))


if __name__ == '__main__':
    main()
