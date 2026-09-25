"""Exercise directory exclusion, official split exclusion and evaluation leakage."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
from train_brightmart import inputs, preprocess, tokenize
from train_brightmart_nonnews import canonical_arpa


def main():
    assert [r[1] for r in tokenize(['你好，世界。游 戏。你好'])] == ['你好', '世界']
    with tempfile.TemporaryDirectory(prefix='nonnews-test-') as tmp:
        root = Path(tmp)
        arpa = root/'toy.arpa'; converted = root/'canonical.arpa'
        arpa.write_text('\\data\\\n'+''.join(f'ngram {i}=1\n' for i in range(1,6))+
                        '\\1-grams:\n-99\t<s>\t0\n\\2-grams:\n-1\t<s> 中\n\\end\\\n')
        assert canonical_arpa(arpa, converted) == [1]*5
        assert '-1\t<s> 中\n' in converted.read_text() and '0\t<s>\t0\n' in converted.read_text()
        source = root/'source'
        for d in ('baike2018qa', 'webtext2019zh', 'wiki_zh_2019/wiki_zh/AA', 'new2016zh'):
            (source/d).mkdir(parents=True)
        texts = ['训练样本'+chr(0x4e00+i//100)+chr(0x4e00+i%100)+'甲乙丙丁' for i in range(5000)]
        reserved = '这是必须排除的评测文本'
        baike = source/'baike2018qa/baike_qa_train.json'
        baike.write_text(''.join(json.dumps(dict(title=t, answer=reserved+'。正常训练内容'), ensure_ascii=False)+'\n' for t in texts))
        (source/'webtext2019zh/web_text_zh_train.json').write_text(json.dumps(dict(content=texts[0]+'。'+reserved))+'\n')
        (source/'wiki_zh_2019/wiki_zh/AA/wiki_00').write_text(json.dumps(dict(text='百科知识文本'))+'\n')
        for path in ('new2016zh/news2016zh_train.json', 'baike2018qa/baike_qa_valid.json', 'webtext2019zh/web_text_zh_testa.json'):
            (source/path).write_text('INVALID JSON MUST NOT BE READ')
        selected = inputs(source, True)
        assert len(selected) == 3 and all('new2016zh' not in p.parts for p, _ in selected)
        cases = root/'cases.tsv'; cases.write_text('case\told\tabcd\t'+reserved+'\n')
        output = root/'corpus'; output.mkdir()
        work = root/'work'; work.mkdir()
        manifest = {}
        preprocess(argparse.Namespace(source=source, output=output, work=work, workers=2,
                   reserve_gib=0, exclude_news=True, exclude_cases=[cases]), manifest)
        train = {line.replace(' ', '') for line in (output/'train.tokens').read_text().splitlines()}
        held = {json.loads(line)['text'] for line in (output/'heldout.jsonl').read_text().splitlines()}
        assert reserved not in train | held and not train & held
        assert '百科知识文本' in train | held
        assert manifest['preprocess']['evaluation_segments_excluded'] == 2
        assert manifest['preprocess']['duplicate_fields'] >= 4999
        assert held and all(int.from_bytes(hashlib.sha256(t.encode()).digest()[:8], 'big') % 1000 == 0 for t in held)
        try:
            inputs(source, True, True)
        except ValueError:
            pass
        else:
            raise AssertionError('Conflicting source selection accepted')
        news = source/'new2016zh/news2016zh_train.json'
        news.write_text(''.join(json.dumps(dict(title=t, content=reserved+'。新闻独有片段'))+'\n' for t in texts))
        (source/'new2016zh/news2016zh_valid.json').write_text('INVALID JSON MUST NOT BE READ')
        for path, _ in selected:
            path.write_text('INVALID NON-NEWS JSON MUST NOT BE READ')
        assert inputs(source, only_news=True) == [(news, ['title', 'content'])]
        news_output = root/'news-corpus'; news_output.mkdir()
        news_work = root/'news-work'; news_work.mkdir()
        manifest = {}
        preprocess(argparse.Namespace(source=source, output=news_output, work=news_work, workers=2,
                   reserve_gib=0, only_news=True, exclude_cases=[cases]), manifest)
        train = {line.replace(' ', '') for line in (news_output/'train.tokens').read_text().splitlines()}
        held = {json.loads(line)['text'] for line in (news_output/'heldout.jsonl').read_text().splitlines()}
        assert reserved not in train | held and not train & held
        assert '新闻独有片段' in train | held and '百科知识文本' not in train | held
        assert manifest['preprocess']['records'] == 5000
        assert manifest['preprocess']['evaluation_segments_excluded'] == 1
    print('News-only/nonnews selection, evaluation exclusion, holdout and BOS tests passed')


if __name__ == '__main__':
    main()
