"""Strict paired metrics for original-table m5/char/joint model experiment."""
import argparse
from collections import Counter
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import analyze as m


def main():
    p = argparse.ArgumentParser()
    p.add_argument('root', type=Path)
    p.add_argument('baseline', type=Path)
    args = p.parse_args()
    root = args.root
    cases = m.indexed(m.read(root/'cases.jsonl'))
    manifest = json.loads((root/'run-manifest.json').read_text())
    assert m.digest(root/'cases.jsonl') == m.digest(args.baseline/'data/cases.jsonl') == manifest['cases']
    results, rows = {}, {}
    token_info = json.loads((root/'tokens.manifest.json').read_text())
    oov_tokens = set(token_info['oovTokenTypes'])
    oov_first = []
    for kind in ['m5','char','joint']:
        path = root/f'{kind}.jsonl'
        manifests = json.loads(Path(str(path)+'.manifest.json').read_text())
        assert manifests.pop('sha256') == m.digest(path)
        for meta in manifests.values():
            assert meta['model'].lower() == manifest['models'][kind]
            assert meta['table'].lower() == manifest['table']
            assert meta['executable'].lower() == manifest['executable']
        rows[kind] = m.validate(cases, m.decode_rows(path), 'test')
        data = rows[kind]
        chars = sum(len(r['text']) for r in data.values())
        errors = sum(m.edit_distance(r['text'], r['candidates'][0]['text'] if r['candidates'] else '') for r in data.values())
        latency = m.read(root/f'latency-{kind}.jsonl')
        keys = [(r['id'],r['direction'],r['keys']) for r in latency]
        if kind == 'm5': reference_keys = keys
        else: assert keys == reference_keys
        results[kind] = dict(**m.accuracy(list(data.values())), count=len(data),
            correct=sum(r['rank']==1 for r in data.values()), characterErrorRate=errors/chars,
            latency={d:m.quantiles([r['ms'] for r in latency if r['direction']==d]) for d in ['append','backspace']})
        if kind == 'joint':
            with path.open() as stream:
                for line in stream:
                    r=json.loads(line)
                    if not r['candidates']:continue
                    top=r['candidates'][0]
                    tokens=[t for seg in top['segments'] for t in seg['tokens']]
                    assert len(tokens)==len(top['text'])
                    unknown=[t for t in tokens if t in oov_tokens]
                    if unknown:oov_first.append(dict(id=r['id'],text=top['text'],unknown=unknown))
    old=m.validate(cases,m.decode_rows(args.baseline/'test-words.jsonl'),'test')
    mismatches=[]; max_score_error=0
    for i,r in rows['m5'].items():
        if [c['text'] for c in r['candidates']] != [c['text'] for c in old[i]['candidates']]:mismatches.append(i)
        else:
            max_score_error=max(max_score_error,max(abs(a['score']-b['score']) for a,b in zip(r['candidates'],old[i]['candidates'])))
    assert not mismatches, f'm5 candidate drift: {mismatches[:10]}'
    assert max_score_error<1e-8, max_score_error
    comparisons={}
    for baseline in ['m5','char']:
        rescued=[];regressed=[]
        for i,new in rows['joint'].items():
            oldrow=rows[baseline][i]
            detail=dict(id=i,text=new['text'],code=new['code'],old=oldrow['candidates'][0]['text'],
                        new=new['candidates'][0]['text'],oldRank=oldrow['rank'],newRank=new['rank'])
            if new['rank']==1 and oldrow['rank']!=1:rescued.append(detail)
            if oldrow['rank']==1 and new['rank']!=1:regressed.append(detail)
        comparisons[baseline]=dict(rescued=rescued,regressed=regressed,net=len(rescued)-len(regressed))
    report=dict(metrics=results,comparisons=comparisons,manifest=manifest,
                tokenAlignment=token_info,top1WithOov=oov_first,
                originalM5Verification=dict(cases=len(old),top50OrderIdentical=True,maxScoreDifference=max_score_error),
                scoringVerification=json.loads((root/'score-verification.json').read_text()))
    (root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    lines=['# 原始拼音词库：m5 与字音联合模型对比','',
        '同样的 8,019 条冻结测试句、65,120 条原始拼音反查词条、Beam 200、每字 +2 自然对数奖励。未使用万象词库，未运行 Qwen。','',
        '| 模型 | 首选准确率 | Top-5 | Top-10 | Top-50 | 字符错误率↓ |',
        '|---|---:|---:|---:|---:|---:|']
    names={'m5':'原 full-kn-m5-v2','char':'新批次 char_3gram','joint':'新批次 joint_3gram（汉字/读音）'}
    for kind,v in results.items():lines.append('| '+names[kind]+' | '+' | '.join(f'{v[k]:.3%}' for k in ['top1','top5','top10','top50','characterErrorRate'])+' |')
    lines += ['', '相同进程架构 Linux ARM64 / .NET 10.0.11 下重跑全部模型。原 m5 的 8,019 条 Top-50 顺序与原 Windows 结果逐条一致。',
              f'共有候选最大分差 {max_score_error:.3g}；原生 KenLM 对照 100 句的自然对数最大分差小于 1e-5。', '']
    for kind,c in comparisons.items():lines.append(f"联合模型相对 {names[kind]}：救回 {len(c['rescued'])} 条，退化 {len(c['regressed'])} 条，净变化 {c['net']:+d} 条。")
    lines += ['', '## 单进程逐键响应：同样 100 句、10,412 次按键操作', '',
              '| 模型 | 追加 p50 / p95 / p99 / 最大 ms | 回删 p50 / p95 / p99 / 最大 ms |',
              '|---|---:|---:|']
    for kind,v in results.items():
        cells=[' / '.join(f"{v['latency'][d][k]:.2f}" for k in ['p50','p95','p99','max']) for d in ['append','backspace']]
        lines.append('| '+names[kind]+' | '+' | '.join(cells)+' |')
    lines += ['', '## 接入与边界','',
        '- 正式根目录 .klm，KenLM trie，不使用 pilot。只读加载一次；每个进程独立有界查询缓存。',
        '- KenLM log10 分数乘 ln(10)，正确计入一次 BOS/EOS；词组逐字扩展字音 token。保留每个路径最后两个联合词元，最终展示候选才按汉字去重。',
        '- 原词库单字读音能唯一对齐 65,117 条记录，其余“道行/dao heng、南无/na mo、咱们/za men”从模型已见读音补齐；没有新加或删除候选词条，没有临时用 pypinyin 猜候选读音。',
        '- 原输入 nue/lue 映射模型 nve/lve；nv/lv 保持 ü 的 v 拼写。',
        f"- 原表对齐后有 {len(oov_tokens)} 种词元不在联合模型中，保留并显式使用 KenLM <unk> 回退；本轮 {len(oov_first)} 条首选包含 OOV，详见 report.json。未将 OOV 静默过滤。",
        '- 两套新模型为同批训练的汉字基线和字音联合模型，用于区分训练批次与读音状态的影响；没有基于测试集调整参数。',
        '- 训练标注与冻结测试注音均有 pypinyin 粗标注局限；没有验证语料训练集互斥。结果是参考文本完全匹配指标，不是日用输入法验收。',
        '- 本次只修改独立实验工具，不部署、不覆盖日用 Core、Rime 或模型。', '']
    (root/'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps(results,indent=2))
    print({k:dict(rescued=len(v['rescued']),regressed=len(v['regressed']),net=v['net']) for k,v in comparisons.items()})


if __name__=='__main__':main()
