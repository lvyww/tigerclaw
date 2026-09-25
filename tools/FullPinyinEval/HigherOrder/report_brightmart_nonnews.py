"""Verify and archive either the news-only or non-news Brightmart experiment."""
import argparse
import json
from pathlib import Path
from train_articles import dump, sha
from build_articles_model import verified_copy
from report_merge_budget import rows, stats, compare, copy_tree
from train_brightmart_nonnews import CASES, HERE, KENLM, CONVERTER, QUANTIZER


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--work', type=Path, required=True)
    p.add_argument('--archive', type=Path, required=True)
    a = p.parse_args(); w = a.work
    m = json.loads((w/'manifest.json').read_text())
    assert 'finished' in m and len(m['evaluations']) == 3
    news_only = m['config'].get('only_directory') == 'new2016zh'
    current_key = 'news' if news_only else 'nonnews'
    source = json.loads((w/'source-manifest.json').read_text())
    allowed = ('new2016zh',) if news_only else ('baike2018qa', 'webtext2019zh', 'wiki_zh_2019')
    assert source and all(x['relative_path'].split('/')[0] in allowed for x in source)
    if news_only:
        assert len(source) == 1 and source[0]['relative_path'] == 'new2016zh/news2016zh_train.json'
    assert all('valid' not in x['relative_path'] and 'testa' not in x['relative_path'] for x in source)
    results = {}
    for dataset, cases in CASES.items():
        dest = w/'eval'/dataset
        current = rows(dest/'predictions.jsonl')
        manifest = json.loads((dest/'manifest.json').read_text())
        reference = json.loads((Path('/home/yc/tmp/corpus4-articles-merge-budget-20260924/alpha-0.25/eval/q8')/dataset/'manifest.json').read_text())
        for key in ('cases_sha256', 'decoder_sha256', 'exporter_sha256', 'fixture_hashes'):
            assert manifest[key] == reference[key], (dataset, key)
        assert manifest['cases_sha256'] == sha(cases)
        assert '666' in (dest/'validation.log').read_text()
        legacy = '20k' if dataset == 'old10k' else dataset
        full = Path('/home/yc/tmp/wsmerge-accuracy-20260924')
        baselines = {
            'mainline': rows(full/'baselines'/f'{legacy}-mainline_q8.jsonl', dataset == 'old10k'),
            'mainline_full': rows(Path('/home/yc/tmp/mainline-full5-eval-20260924')/legacy/'predictions.jsonl', dataset == 'old10k'),
            'corpus4_full': rows(full/legacy/'predictions.jsonl', dataset == 'old10k'),
            'articles': rows(Path('/home/yc/tmp/articles-corpus4-complement-20260924')/dataset/'predictions.jsonl'),
            'merge25': rows(Path('/home/yc/tmp/corpus4-articles-merge-budget-20260924/alpha-0.25/eval/q8')/dataset/'predictions.jsonl'),
        }
        if news_only:
            baselines['nonnews'] = rows(Path('/home/yc/tmp/brightmart-nonnews-20260925/eval')/dataset/'predictions.jsonl')
        results[dataset] = dict(baselines={k: stats(v) for k, v in baselines.items()},
                                comparisons={k: compare(current, v, w/f'{dataset}-vs-{k}.jsonl') for k, v in baselines.items()})
        results[dataset][current_key] = stats(current)
        if news_only:
            prior_bad = []
            for key, old in baselines['nonnews'].items():
                if old['prediction'] != old['target'] and baselines['mainline'][key]['prediction'] == old['target']:
                    row = current[key]
                    prior_bad.append(dict(id=key, target=old['target'], nonnews_prediction=old['prediction'],
                                          news_prediction=row['prediction'], recovered=row['prediction'] == old['target']))
            assert len(prior_bad) == {'old10k': 22, 'articles': 113, 'thucnews': 56}[dataset]
            (w/f'{dataset}-nonnews-regression-followup.jsonl').write_text(''.join(json.dumps(x, ensure_ascii=False)+'\n' for x in prior_bad))
            results[dataset]['prior_nonnews_regressions'] = dict(total=len(prior_bad), recovered=sum(x['recovered'] for x in prior_bad))
    identity = dict(bytes=(w/'sentence-fivegram-mobile.bin').stat().st_size, sha256=sha(w/'sentence-fivegram-mobile.bin'))
    summary = dict(results=results, model=identity, counts=m['counts'], heldout=m['heldout'],
                   net_vs_mainline=sum(r['comparisons']['mainline']['net'] for r in results.values()))
    dump(w/'summary.json', summary)
    def accuracy(key):
        scores = [results[d][current_key] if key == current_key else results[d]['baselines'][key] for d in CASES]
        return '|'.join(f"{100*s['correct']/s['n']:.3f}% ({s['correct']:,})" for s in scores)
    text = ['# Brightmart 新闻独立五阶模型' if news_only else '# Brightmart 排除 new2016zh：独立五阶模型', '',
            '只使用 new2016zh/news2016zh_train.json 的 title/content；其他领域及官方验证集不参与训练。原始语料未修改。' if news_only else
            '保留百科问答、社区问答和维基文本；仅排除新闻目录，不代表剩余文本中完全没有新闻内容。原始语料未修改。', '',
            '|模型|体积 MB|旧集 10,000|Articles 33,129|THUCNews 30,000|',
            '|---|---:|---:|---:|---:|']
    model_rows = [('当前主线 Q8', 'mainline', 356.492204), ('主线裁剪前五阶 Q8', 'mainline_full', 2032.38), ('Corpus4 完整 Q8', 'corpus4_full', 7770.486806),
                  ('Articles 独立模型', 'articles', 2503.378280), ('Corpus4 + Articles 25% 融合剪枝', 'merge25', 415.305954)]
    if news_only:
        model_rows.append(('Brightmart 排除新闻', 'nonnews', 1551.708536))
    model_rows.append(('Brightmart 仅新闻' if news_only else 'Brightmart 排除新闻', current_key, identity['bytes']/1e6))
    for label, key, size in model_rows:
        text.append(f'|{label}|{size:.2f}|'+accuracy(key)+'|')
    if news_only:
        text += ['', '新闻版与上一轮排除新闻版使用相同的清洗、评测目标片段排除、训练剪枝和Q8转换规则；训练来源与数据量不同。']
        text += ['', '## 上一轮非新闻模型退步句的复查', '', '|评测集|上一轮主线正确、非新闻模型错误|新闻模型救回|', '|---|---:|---:|']
        for dataset in CASES:
            follow = results[dataset]['prior_nonnews_regressions']
            text.append(f"|{dataset}|{follow['total']}|{follow['recovered']}|")
        text += ['', '此处只统计旧退步集合的救回，不等于新闻模型的净收益；完整新退步和救回见逐句差异及 summary.json。']
    text += ['', f"三套合计较主线净增正确句数：{summary['net_vs_mainline']:+d}。本次只训练独立模型，没有执行融合或部署。", '',
             '## 训练与验证', '',
             f"输入训练文件 {len(source):,} 个，共 {sum(x['bytes'] for x in source):,} 字节；文件列表和 SHA256 见 source-manifest.json。",
             '保留原 Brightmart 清洗：NFKC、HTML/URL 清理、汉字段长 2..256、字段去重、字段内片段去重，不跨标点或空白拼接。官方 valid/test 文件未参与训练。',
             '按片段 SHA256 固定留出 0.1%，所有同文片段都排除；另从训练和留出中排除三套冻结评测的完整目标片段。近似重复及长段内子串未全面排除，历史模型没有统一重训以应用这项额外排除，因此不是只改变新闻比例的严格消融。',
             f"预处理统计：`{json.dumps(m['stages']['preprocess'], ensure_ascii=False)}`。",
             '五阶 modified Kneser–Ney，训练时 --prune 0 0 1 1 1；保留原始 ARPA。BOS 上下文哨兵归零后，保留所有记录转换 TCSKNM03 Q16/Q8，没有额外预算剪枝。',
             '各阶记录数：'+', '.join(f'{x:,}' for x in m['counts'])+'。',
             f"固定留出 {m['heldout']['rows']:,} 句，Q8 perplexity {m['heldout']['perplexity']:.6f}，OOV 字符 {m['heldout']['oov']}。留出集不同，不能直接与其他模型各自留出的困惑度比较。",
             'Q16/Q8 格式和评分误差检查通过；三套各666条增量/回退/锁定前缀检查通过。评测使用同一冻结 Lua 和历史先验，关闭学习、Qwen、提前上屏；不是当前 C# 实际输入验收。',
             f"最终模型 SHA256：`{identity['sha256']}`。", '',
             '原始 ARPA 和最终 Q8 已归档；大体积训练令牌、Q16及本地输入快照通过工作目录和哈希引用保留。', '']
    report = '\n'.join(text); (w/'REPORT.md').write_text(report)
    a.archive.mkdir(parents=True, exist_ok=True)
    for name in ('char5.arpa', 'sentence-fivegram-mobile.bin'):
        verified_copy(w/name, a.archive/name)
    copy_tree(w/'eval', a.archive/'eval')
    copy_tree(w/'validation', a.archive/'validation')
    for path in w.iterdir():
        if path.is_file() and path.suffix in ('.json', '.jsonl', '.md', '.log', '.lua', '.tsv'):
            verified_copy(path, a.archive/path.name)
    corpus = a.archive/'corpus'; corpus.mkdir(exist_ok=True)
    for name in ('manifest.json', 'heldout.jsonl', 'heldout.tokens'):
        verified_copy(w/'corpus'/name, corpus/name)
    dump(a.archive/'intermediate-references.json', {str(path): dict(bytes=path.stat().st_size, sha256=sha(path))
         for path in (w/'corpus/train.tokens', w/'model-q16.bin', w/'char5-tcs.arpa')})
    scripts = a.archive/'scripts'; scripts.mkdir(exist_ok=True)
    for name in ('train_brightmart.py', 'train_brightmart_nonnews.py', 'test_brightmart_nonnews.py', 'report_brightmart_nonnews.py',
                 'train_articles.py', 'build_articles_model.py', 'evaluate_external_shape.py', 'report_merge_budget.py'):
        verified_copy(HERE/name, scripts/name)
    reader = scripts/'TcsQ8'; reader.mkdir(exist_ok=True)
    for name in ('validate.py', 'requantize.cpp', 'tiger_sentence_fivegram.lua'):
        verified_copy(HERE/'TcsQ8'/name, reader/name)
    binaries = scripts/'bin'; binaries.mkdir(exist_ok=True)
    for tool in (KENLM/'lmplz', CONVERTER, QUANTIZER):
        assert sha(tool) == m['tools'][str(tool)]
        verified_copy(tool, binaries/tool.name)
    for name in ('builder-manifest.json', 'build_tcs_knm03_preserving.cpp', 'LICENSE'):
        verified_copy(CONVERTER.parent/name, scripts/name)
    (HERE/('BRIGHTMART_NEWS.md' if news_only else 'BRIGHTMART_NONNEWS.md')).write_text(report)
    hashes = {str(path.relative_to(a.archive)): sha(path) for path in a.archive.rglob('*') if path.is_file() and path.name != 'SHA256.json'}
    dump(a.archive/'SHA256.json', hashes)
    for path, digest in hashes.items():
        assert sha(a.archive/path) == digest
    print(report)
    print('ARCHIVE VERIFIED', len(hashes))


if __name__ == '__main__':
    main()
