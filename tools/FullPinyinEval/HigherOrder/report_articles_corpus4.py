"""Compare the full corpus4 fivegram with the frozen Articles baselines."""
import argparse
import csv
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    args = parser.parse_args()
    root = args.root
    names = ['mohu-v5', 'rime-mainline', 'corpus4-fivegram']
    data = {name: [json.loads(line) for line in (root / name / 'predictions.jsonl').read_text().splitlines()]
            for name in names}
    manifests = {name: json.loads((root / name / 'manifest.json').read_text()) for name in names}
    count = json.loads((root / 'dataset-manifest.json').read_text())['total']
    for name in names:
        assert len(data[name]) == count
        for field in ('cases_sha256', 'decoder_sha256', 'exporter_sha256', 'fixture_hashes'):
            assert manifests[name][field] == manifests[names[0]][field]
    rows = []
    for group in zip(*(data[name] for name in names), strict=True):
        row = {key: group[0][key] for key in ('id', 'source', 'code', 'target')}
        for name, item in zip(names, group):
            assert all(item[key] == value for key, value in row.items() if key in ('id', 'source', 'code', 'target'))
            row[name] = item['prediction']
            row[name + '_rank'] = item['rank']
        rows.append(row)
    summary = {}
    for source in ['combined'] + sorted({row['source'] for row in rows}):
        subset = [row for row in rows if source == 'combined' or row['source'] == source]
        stats = dict(n=len(subset), methods={}, comparisons={})
        for name in names:
            correct = sum(row[name] == row['target'] for row in subset)
            stats['methods'][name] = dict(correct=correct, accuracy=correct / len(subset),
                                         top5=sum(0 < row[name + '_rank'] <= 5 for row in subset),
                                         top20=sum(0 < row[name + '_rank'] <= 20 for row in subset))
        for name in names[:-1]:
            improved = sum(row[names[-1]] == row['target'] and row[name] != row['target'] for row in subset)
            regressed = sum(row[names[-1]] != row['target'] and row[name] == row['target'] for row in subset)
            stats['comparisons'][name] = dict(improved=improved, regressed=regressed, net=improved-regressed)
        summary[source] = stats
    (root / 'corpus4-comparison-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
    outputs = {'THREE_MODELS_ALL.csv': rows}
    for name in names[:-1]:
        outputs[f'CORPUS4_VS_{name}.csv'] = [row for row in rows if row[names[-1]] != row[name]]
    for filename, subset in outputs.items():
        with (root / filename).open('w', encoding='utf-8-sig', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(subset)
    total = summary['combined']
    report = ['# Articles：完整 corpus4 五阶与两条基线', '',
              f'原封不动复用此前冻结的 {count} 条样本、形码、解码器及其他资源；不重新抽样、不调参。', '',
              '|模型|文件MB（十进制）|首选正确|准确率|Top5|Top20|', '|---|---:|---:|---:|---:|---:|']
    for name in names:
        value = total['methods'][name]
        report.append(f"|{name}|{manifests[name]['model_bytes']/1e6:.2f}|{value['correct']}|{value['accuracy']:.3%}|{value['top5']}|{value['top20']}|")
    report += ['', '|corpus4相对基线|修正|退步|净变化|', '|---|---:|---:|---:|']
    for name, value in total['comparisons'].items():
        report.append(f"|{name}|{value['improved']}|{value['regressed']}|{value['net']:+d}|")
    report += ['', '|来源|样本|mohu正确|Rime正确|corpus4正确|corpus4相对Rime净变化|', '|---|---:|---:|---:|---:|---:|']
    for source, stats in summary.items():
        if source == 'combined':
            continue
        values = [str(stats['methods'][name]['correct']) for name in names]
        report.append(f"|{source}|{stats['n']}|" + '|'.join(values) + f"|{stats['comparisons']['rime-mainline']['net']:+d}|")
    report += ['', '## 模型与范围', '',
               'corpus4源文件为用户指定的 C:\\Archive\\char5-corpus4_0-20260922\\char5-q8.klm，与Linux缓存逐文件SHA256一致后复用缓存。模型身份见corpus4-source.json，各运行manifest保存模型、解码器、输入与资源哈希。', '',
               'corpus4与Rime主线均在搜索中使用五阶，保留相同原三阶observed-bigram隔离先验；mohu使用自身三阶及其隔离先验。两条五阶比较不是同体积比较：完整corpus4约7.62GB，Rime主线约460.69MB。', '',
               '关闭LLM、学习、提前上屏；未改安装或模型部署，未测延迟。数据是分来源抽样的4–40字片段，平均8.50字，含古文、诗歌、疑似错字及作品重复；训练重合未审计。正确指精确复现原文，不能解释为无污染泛化准确率。样本及来源验证见DATASET.md、dataset-validation.json；前次两模型差异分析见ERROR_ANALYSIS.md。', '',
               '全部三方预测：THREE_MODELS_ALL.csv；逐基线输出差异：CORPUS4_VS_mohu-v5.csv、CORPUS4_VS_rime-mainline.csv。']
    (root / 'CORPUS4_REPORT.md').write_text('\n'.join(report) + '\n')
    print(json.dumps(total, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
