"""Compare isolated real-Rime output against the frozen full-pinyin experiment."""
import argparse
import json
from pathlib import Path
from analyze import read, indexed, quantiles, edit_distance, digest


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('tiger', type=Path)
    p.add_argument('wanxiang', type=Path)
    args = p.parse_args()
    t, w = args.tiger, args.wanxiang
    verification = json.loads((w / 'verification.json').read_text())
    assert verification['count'] == 100 and verification['candidateListsEqual']
    assert verification['grammarChangedFirst'] > 0
    cases = indexed(read(t / 'data/cases.jsonl'))
    expected = {i for i, c in cases.items() if c['split'] == 'test'}
    rows = indexed(read(w / 'test.jsonl'))
    fused = indexed(read(t / 'test-fused.jsonl'))
    assert set(rows) == set(fused) == expected
    old = json.loads((t / 'report.json').read_text())
    wins, losses, both, neither = [], [], 0, 0
    ranks, errors, full_counts, partial_first = [], 0, [], 0
    for i, row in rows.items():
        c = cases[i]
        assert row['code'] == c['code'] and row['candidates']
        candidates = row['candidates']
        for candidate in candidates:
            start, end = map(int, candidate['boundary'].split(':'))
            assert 0 <= start <= end <= len(c['code'])
        full = [x for x in candidates if x['boundary'] == f"0:{len(c['code'])}"]
        full_counts.append(len(full))
        first = candidates[0]['text']
        first_full = candidates[0]['boundary'] == f"0:{len(c['code'])}"
        partial_first += not first_full
        rank = next((j + 1 for j, x in enumerate(candidates)
                     if x['text'] == c['text'] and x['boundary'] == f"0:{len(c['code'])}"), 0)
        ranks.append(rank)
        errors += edit_distance(first, c['text'])
        wok = first_full and first == c['text']
        tok = fused[i]['candidates'][0]['text'] == c['text']
        example = dict(id=i, text=c['text'], code=c['code'], wanxiang=first,
                       tiger=fused[i]['candidates'][0]['text'])
        if wok and not tok:
            wins.append(example)
        elif tok and not wok:
            losses.append(example)
        elif wok:
            both += 1
        else:
            neither += 1
    wl = read(w / 'latency.jsonl')
    tl = read(t / 'latency.jsonl')
    assert [(r['id'], r['direction'], r['keys']) for r in wl] == [
        (r['id'], r['direction'], r['keys']) for r in tl]
    latency = {d: quantiles([r['ms'] for r in wl if r['direction'] == d])
               for d in ['append', 'backspace']}
    report = dict(count=len(rows),
                  wanxiang=dict(top1=sum(r == 1 for r in ranks) / len(rows),
                      visibleMenuExactRecall={f'top{k}': sum(0 < r <= k for r in ranks) / len(rows)
                                             for k in [5, 10, 50]},
                      characterErrorRate=errors / sum(len(cases[i]['text']) for i in rows),
                      partialFirst=partial_first, fullCandidatesInFirst50=quantiles(full_counts),
                      firstPageMs=quantiles([r['firstPageMs'] for r in rows.values()]),
                      incrementalMs=latency),
                  tiger=old['test'], tigerIncrementalMs=old['incrementalMs'],
                  bothCorrect=both, bothWrong=neither,
                  wanxiangOnlyCorrect=wins, tigerQwenOnlyCorrect=losses,
                  sha256={str(path): digest(path) for path in [t / 'data/cases.jsonl',
                      t / 'test-fused.jsonl', w / 'test.jsonl', w / 'latency.jsonl',
                      w / 'base.zip', w / 'user/wanxiang-lts-zh-hans.gram']})
    (w / 'comparison.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    pct = lambda n: f'{n * 100:.3f}%'
    lines = [
        '# 万象与虎爪离线全拼对比（2026-09-21）', '',
        '同一份冻结注音的 8,019 条测试句，直接输入相同的连续全拼，不提供词边界。',
        '整句正确要求首选完整消费输入、文字与参考完全相同；字符错误率为编辑距离总和／参考字符总数。', '',
        '| 配置 | 整句首选准确率 | 字符错误率（低优） |',
        '|---|---:|---:|',
        f"| 万象 Base v18.0.8 + LTS 简体语法模型 | {pct(report['wanxiang']['top1'])} | {pct(report['wanxiang']['characterErrorRate'])} |",
        f"| 虎爪全拼原型 m5，Beam 200 | {pct(old['test']['ngram']['top1'])} | {pct(old['test']['ngramCharacterErrorRate'])} |",
        f"| 虎爪全拼原型 m5 + Qwen Top-10，alpha 0.45 | {pct(old['test']['fusedTop1'])} | {pct(old['test']['fusedCharacterErrorRate'])} |", '',
        f'双方都对 {both} 条，都错 {neither} 条；万象单独正确 {len(wins)} 条，虎爪 Qwen 单独正确 {len(losses)} 条。',
        '逐条候选、两方向胜负案例、输入与产物 SHA256 见 comparison.json / test.jsonl。', '',
        '## 逐键响应（同样的 100 句、10,412 次追加／回删）', '',
        '| 测量 | 追加 p95 / p99 / 最慢 ms | 回删 p95 / p99 / 最慢 ms |',
        '|---|---:|---:|']
    for label, value in [('万象真实 librime + Lua 链', latency),
                         ('虎爪离线 n-gram 解码器', old['incrementalMs'])]:
        cells = [' / '.join(f'{value[d][k]:.2f}' for k in ['p95', 'p99', 'max'])
                 for d in ['append', 'backspace']]
        lines.append(f'| {label} | {cells[0]} | {cells[1]} |')
    lines += ['',
        '万象测量包含 process_key 与获取候选页，运行于 WSL ARM64 librime 1.16.1；虎爪为 Windows ARM64 C# 解码器。',
        '同一台 Snapdragon X2 Elite Extreme，但软件层级、操作系统不同；不能把此表当作应用界面延迟的严格横评。',
        '虎爪这一列不含 Qwen；已有 Qwen 每句 Top-10 重排 p95 约 2.08 秒（4 进程 × 4 线程），实际接入仍有明显等待成本。', '',
        '## 设置与核验', '',
        '- 官方 Base v18.0.8（commit 3de27bf2a4938be38cdeb05910953aa630deae8a），全拼默认转写；未重新调参。',
        '- 官方 wanxiang-lts-zh-hans.gram，419,911,724 bytes，2026-09-18 更新。下载 SHA256 与官方发布元数据一致。',
        '- 补编译独立 octagram 插件，commit 57d18b9f58e5284bd891d559f6bdd16cf60341e9；strace 确认完整模型以只读方式映射。',
        '- 隔离用户目录，translator/enable_user_dict=false，不执行上屏；每句清空组合，无个人词库／上文强化。',
        '- 最后追加只改注释的 Lua 边界探针，保留原顺序和正文，记录候选消费的原始编码范围。',
        '- 万象首选全部消费完整输入，但前 50 候选中完整句子数量 p50/p95/p99 都为 1，其余通常是局部词语。',
        '  因此不将它的候选菜单 Top-10 与虎爪十条完整句子 Top-10 当作等价搜索空间。',
        '- test.jsonl 是 set_input 后获取候选；另以 100 条实际逐键输入核对其完整候选列表，见 verification.json。', '',
        '## 解读边界', '',
        '纯 n-gram 原型与万象在此集整句首选接近；加入 Qwen 后原型高约 14.12 个百分点，但成本更高。',
        '本实验不能据此推断日常输入全面优于万象：未测试纠错、简拼、个人学习、前端交互，也未证明语料与任一模型训练集不重叠。',
        '拼音由冻结 pypinyin 0.55.0 标注，仅抽检 100 条，保留已知多音字错误；合理同音写法与参考不同也计错。', '',
        '来源：[万象 v18.0.8](https://github.com/amzxyz/rime-wanxiang/releases/tag/v18.0.8)、',
        '[LTS 模型](https://github.com/amzxyz/RIME-LMDG/releases/tag/LTS)、',
        '[Octagram](https://github.com/lotem/librime-octagram)。', '']
    (w / 'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps({k: v for k, v in report.items() if k not in
          ['tiger', 'wanxiangOnlyCorrect', 'tigerQwenOnlyCorrect', 'sha256']}, ensure_ascii=False, indent=2))
    print('wanxiang-only', len(wins), 'tiger-qwen-only', len(losses))


if __name__ == '__main__':
    main()
