"""Paired report for Sogou's real-key, fast/slow black-box collection."""
import argparse
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from analyze import edit_distance


def read(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    args = parser.parse_args()
    root = args.root
    cases = {c['id']: c for c in read(root / 'cases.jsonl')}
    attempts = {}
    fast = {}
    final = {}
    last = None
    events = []
    for filename in ['calibration.jsonl', 'results.jsonl', 'resume.jsonl']:
        path = root / filename
        if not path.exists():
            continue
        for line in path.read_text().splitlines(keepends=True):
            if not line.endswith('\n'):
                continue
            row = json.loads(line)
            events.append(row)
            if row['event'] == 'result':
                last = row
            elif row['event'] == 'decision':
                assert last and last['id'] == row['id']
                c = cases[row['id']]
                assert last['code'] == c['code']
                assert row['correct'] == (last['text'] == c['text'])
                if last['interrupted']:
                    continue
                attempts[(row['id'], row['stage'])] = dict(last, decision=row)
                if row['stage'] == 'fast':
                    fast[row['id']] = attempts[(row['id'], row['stage'])]
                if row['stage'] == 'slow' or row['valid'] and row['correct']:
                    final[row['id']] = attempts[(row['id'], row['stage'])]
    ids = list(final)

    def metric(texts, validity=None):
        n = len(ids)
        correct = sum(texts[i] == cases[i]['text'] and (validity is None or validity[i]) for i in ids)
        errors = sum(edit_distance(cases[i]['text'], texts[i]) for i in ids)
        chars = sum(len(cases[i]['text']) for i in ids)
        return dict(count=n, correct=correct, accuracy=correct/n if n else None,
                    characterErrorRate=errors/chars if chars else None)

    metrics = {'sogouAdaptive': metric({i: final[i]['text'] for i in ids},
                                     {i: final[i]['decision']['valid'] for i in ids})}
    paired = root.parent / 'joint-qwen-wetype-1050-20260921'
    assert (root/'cases.jsonl').read_bytes() == (paired/'cases.jsonl').read_bytes()
    wetype = {r['id']: r for r in read(paired/'wetype.jsonl')}
    for i in ids:
        assert wetype[i]['code'] == cases[i]['code']
    metrics['wetypeAdaptive'] = metric({i: wetype[i]['adaptiveText'] for i in ids})
    for name, filename in [('jointBeam200', 'ngram.jsonl'), ('jointBeam200Qwen', 'fused.jsonl')]:
        rows = {r['id']: r for r in read(paired/filename)}
        for i in ids:
            assert rows[i]['code'] == cases[i]['code']
        metrics[name] = metric({i: rows[i]['candidates'][0]['text'] for i in ids})
    frost = root.parent/'frost-1050-20260921'
    for name, filename in [('frost', 'test.jsonl'), ('wanxiang', 'wanxiang-paired.jsonl')]:
        rows = {r['id']: r for r in read(frost/filename)}
        for i in ids:
            assert rows[i]['code'] == cases[i]['code']
        metrics[name] = metric({i: rows[i]['candidates'][0]['text'] for i in ids},
                              {i: rows[i]['candidates'][0]['boundary'] == f"0:{len(cases[i]['code'])}" for i in ids})
    report = dict(completed=len(ids), planned=len(cases), complete=len(ids)==len(cases),
                  fastAttempts=len(fast), fastCorrect=sum(r['decision']['valid'] and r['decision']['correct'] for r in fast.values()),
                  slowAttempts=sum(stage=='slow' for i, stage in attempts),
                  slowRescued=sum(r['decision']['stage']=='slow' and r['decision']['valid'] and r['decision']['correct'] for r in final.values()),
                  slowTextChanged=sum(r['text'] != fast[i]['text'] for (i, stage), r in attempts.items() if stage=='slow' and i in fast),
                  invalidFinal=sum(not r['decision']['valid'] for r in final.values()),
                  rawVerified=sum(r['rawVerified'] for r in final.values()),
                  lastEvent=events[-1] if events else None, metrics=metrics,
                  hashes={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [root/'cases.jsonl', root/'calibration.jsonl', root/'results.jsonl', root/'resume.jsonl'] if p.exists()})
    (root/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    (root/'selected.jsonl').write_text(''.join(json.dumps(final[i],ensure_ascii=False)+'\n' for i in ids))
    lines = [f'# 搜狗拼音配对黑盒测试：已完成 {len(ids)}/{len(cases)} 句', '',
             '搜狗 16.8.0.4914，当前个人设置、在线服务和学习状态未隔离。快测 30ms/键＋等待 1s；错误再以 100ms/键＋等待 3s 复核。', '',
             '**这是两阶段上屏文本通过率，非统一速度的一次准确率。搜狗未暴露可读组合文本，rawVerified 保持 false；不能证明内部消费了全部编码。valid 仅表示按键序列发送完成、无焦点中断且得到非空上屏文本。**', '',
             '参考文本仅用于评分和复核决策；通过真实 SendInput 输入字母和空格，每句开始按 Escape 清理本控件残留组合。最初五句校准直接纳入正式结果，不重复测试。', '',
             '完成 891 句后因切换对话窗口暂停；窗口回到前台后 profile/焦点检查仍未恢复，因此停止原宿主，重新激活搜狗并只继续剩余 159 句。首句从未完成的慢速复核阶段继续，其余为快测。原日志和 resume.jsonl 均保留。', '',
             '| 方案（相同已完成 ID） | 正确/总数 | 通过率 | CER |', '|---|---:|---:|---:|']
    labels={'sogouAdaptive':'搜狗，两阶段上屏','wetypeAdaptive':'微信，两阶段复核','jointBeam200':'联合 Beam200','jointBeam200Qwen':'联合 Beam200＋Qwen','frost':'白霜','wanxiang':'万象'}
    if ids:
        for name, m in metrics.items():
            lines.append(f"| {labels[name]} | {m['correct']}/{m['count']} | {m['accuracy']:.3%} | {m['characterErrorRate']:.3%} |")
    lines += ['',f"快测：{report['fastCorrect']}/{len(fast)}；慢测 {report['slowAttempts']} 句，救回 {report['slowRescued']} 句，文本变化 {report['slowTextChanged']} 句。最终无效记录 {report['invalidFinal']}。", '',
              '未完成时仅比较相同已完成 ID，不将子集成绩与原 1,050 句整体成绩直接比较。冻结注音与训练语料互斥的既有局限仍然适用。']
    (root/'REPORT.md').write_text('\n'.join(lines)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k not in ['hashes','lastEvent']},ensure_ascii=False))


if __name__ == '__main__':
    main()
