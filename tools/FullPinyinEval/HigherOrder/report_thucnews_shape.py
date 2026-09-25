"""Pair completed mohu v5 and Rime mainline runs on the frozen THUCNews sample."""
import argparse
import csv
import json
from pathlib import Path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', type=Path, required=True)
    p.add_argument('--title', default='THUCNews')
    p.add_argument('--dataset-note', type=Path)
    args = p.parse_args()
    root = args.root
    names = ['mohu-v5', 'rime-mainline']
    data = {name: [json.loads(line) for line in (root / name / 'predictions.jsonl').read_text().splitlines()]
            for name in names}
    manifests = {name: json.loads((root / name / 'manifest.json').read_text()) for name in names}
    assert manifests[names[0]]['cases_sha256'] == manifests[names[1]]['cases_sha256']
    assert manifests[names[0]]['decoder_sha256'] == manifests[names[1]]['decoder_sha256']
    assert manifests[names[0]]['exporter_sha256'] == manifests[names[1]]['exporter_sha256']
    count = json.loads((root / 'dataset-manifest.json').read_text())['total']
    assert len(data[names[0]]) == len(data[names[1]]) == count
    rows = []
    for a, b in zip(data[names[0]], data[names[1]], strict=True):
        assert all(a[k] == b[k] for k in ('id', 'source', 'code', 'target'))
        correct_a, correct_b = a['prediction'] == a['target'], b['prediction'] == b['target']
        change = ('both_correct' if correct_b else 'both_wrong') if correct_a == correct_b else (
            'rime_improved' if correct_b else 'rime_regressed')
        rows.append(dict(id=a['id'], source=a['source'], code=a['code'], target=a['target'],
                         mohu=a['prediction'], rime=b['prediction'],
                         mohu_rank=a['rank'], rime_rank=b['rank'], change=change))
    summary = {}
    for source in ['combined'] + sorted({r['source'] for r in rows}):
        subset = [r for r in rows if source == 'combined' or r['source'] == source]
        stats = dict(n=len(subset))
        for name in ('mohu', 'rime'):
            correct = sum(r[name] == r['target'] for r in subset)
            stats[name] = dict(correct=correct, accuracy=correct / len(subset),
                               top5=sum(0 < r[name + '_rank'] <= 5 for r in subset),
                               top20=sum(0 < r[name + '_rank'] <= 20 for r in subset))
        stats['rime_improved'] = sum(r['change'] == 'rime_improved' for r in subset)
        stats['rime_regressed'] = sum(r['change'] == 'rime_regressed' for r in subset)
        stats['net'] = stats['rime_improved'] - stats['rime_regressed']
        stats['different_outputs'] = sum(r['mohu'] != r['rime'] for r in subset)
        assert stats['net'] == stats['rime']['correct'] - stats['mohu']['correct']
        summary[source] = stats
    for name, subset in [(f'COMPARISON_ALL_{count}.csv', rows),
                         ('DIFFERENT_OUTPUTS.csv', [r for r in rows if r['mohu'] != r['rime']]),
                         ('ERRORS_EITHER_MODEL.csv', [r for r in rows if r['change'] != 'both_correct'])]:
        with (root / name).open('w', encoding='utf-8-sig', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(subset)
    (root / 'comparison-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
    total = summary['combined']
    report = [f'# {args.title}：mohu v5 与 Rime 当前主线', '',
              f"固定{count}条：mohu v5 首选 {total['mohu']['correct']}（{total['mohu']['accuracy']:.3%}），"
              f"Rime 主线 {total['rime']['correct']}（{total['rime']['accuracy']:.3%}）。"
              f"Rime相对mohu进步 {total['rime_improved']} 条、退步 {total['rime_regressed']} 条，净 {total['net']:+d} 条。", '',
              '|类别|样本|mohu正确|Rime正确|mohu准确率|Rime准确率|Rime进步/退步|',
              '|---|---:|---:|---:|---:|---:|---:|']
    for source, stats in summary.items():
        report.append(f"|{source}|{stats['n']}|{stats['mohu']['correct']}|{stats['rime']['correct']}|"
                      f"{stats['mohu']['accuracy']:.3%}|{stats['rime']['accuracy']:.3%}|"
                      f"{stats['rime_improved']}/{stats['rime_regressed']}|")
    report += ['', f"Top5：mohu {total['mohu']['top5']}/{count}；Rime {total['rime']['top5']}/{count}。"
               f"Top20：mohu {total['mohu']['top20']}/{count}；Rime {total['rime']['top20']}/{count}。", '',
               '## 数据与协议', '',
               args.dataset_note.read_text() if args.dataset_note else r'来源：用户提供的 C:\Users\yc\Downloads\语料包.7z。六类 THUCNews 共337235篇。固定种子20260923，每类5000条，正文连续中文4–40字，每篇最多一个片段，全局文本去重，不跨标点、数字、英文拼接。平均10.585字，3条含显式选重。所有30000条已核对原文偏移。', '',
               '模型运行前即冻结测试集，未根据预测挑样或调参。过滤统计、原文件SHA、片段位置见 dataset-manifest.json 与 provenance.jsonl。分母保留无输出行。使用相同冻结形码解码器、码表、compact参数和词表先验，关闭LLM、学习、提前上屏。两路均通过666项增量/回删/锁前缀检查。', '',
               'mohu使用自身三阶模型完成搜索和observed-bigram隔离先验；Rime五阶用于搜索，沿用主线原三阶模型的observed-bigram隔离先验。因此是各模型现有用法的比较，不能把全部差异单独归因为阶数。', '',
               '## 模型身份', '']
    for name in names:
        m = manifests[name]
        report += [f"- {name}: {m['model_bytes']}字节；SHA256 `{m['model_sha256']}`。"]
    report += ['', r'mohu来源由用户指定：D:\Archive\tigerclaw_sentence_ml\experiments\original-size-sweep-20260920\controls\mohu_v5\sentence-ngram-mobile.bin。TCSKNM02三阶；与历史v5哈希一致。Windows与Linux复制均核验SHA。未使用下载目录另一份TCSKNM04文件。', '',
               'Rime为brightmart-char5-context128-tcs3-q16，TCSKNM03五阶，哈希与当前Rime仓库default-model.json一致。', '',
               '## 明细与边界', '',
               f'- COMPARISON_ALL_{count}.csv：全部配对预测与正确目标名次。',
               '- DIFFERENT_OUTPUTS.csv：全部输出差异，含进步、退步、两者均错。',
               '- ERRORS_EITHER_MODEL.csv：任一模型出错的全部样本。',
               '- 两个模型子目录保存候选池、验证日志、预测与manifest。', '',
               '这是分来源抽样片段评测，并非源目录的全量逐句评测，也不是按原始篇数加权的总体成绩。与各训练集重合未审计；不能称作无污染泛化成绩。测试使用冻结解码器，不是最新Rime宿主实机验收，未评测延迟与提前上屏。未修改安装、模型部署或运行配置。']
    (root / 'REPORT.md').write_text('\n'.join(report) + '\n')
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
