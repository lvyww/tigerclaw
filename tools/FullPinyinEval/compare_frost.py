"""Real librime Frost vs frozen joint-model and WeType paired results."""
import argparse
import json
from pathlib import Path
from analyze import read,indexed,digest,edit_distance


def main():
    p=argparse.ArgumentParser()
    p.add_argument('root',type=Path)
    p.add_argument('paired',type=Path)
    p.add_argument('--wanxiang',type=Path,help='Existing full-test Wanxiang experiment; select the same case IDs')
    a=p.parse_args();root=a.root
    manifest=json.loads((root/'manifest.json').read_text())
    assert digest(root/'cases.jsonl')==digest(a.paired/'cases.jsonl')==manifest['casesSha256']
    assert digest(root/'user/zh-moqi.gram')==manifest['grammarSha256']
    assert digest(root/'source.zip')==manifest['sourceArchiveHash']
    cases=indexed(read(root/'cases.jsonl'));rows=indexed(read(root/'test.jsonl'))
    assert rows.keys()==cases.keys() and len(rows)==manifest['count']
    checks=indexed(read(root/'check-set-input.jsonl'))
    assert len(checks)==100
    for i,r in checks.items():
        assert r['code']==rows[i]['code'] and r['candidates']==rows[i]['candidates'],i
    control=json.loads((root/'control-verification.json').read_text())
    assert control['count']==100 and not control['mismatches']
    correct=0;errors=0;partial=0;full_counts=[]
    for i,row in rows.items():
        c=cases[i];assert row['code']==c['code'] and row['candidates']
        for cand in row['candidates']:
            start,end=map(int,cand['boundary'].split(':'))
            assert 0<=start<=end<=len(c['code'])
        first=row['candidates'][0];full=first['boundary']==f"0:{len(c['code'])}"
        partial+=not full;correct+=full and first['text']==c['text']
        errors+=edit_distance(c['text'],first['text'])
        full_counts.append(sum(x['boundary']==f"0:{len(c['code'])}" for x in row['candidates']))
    old=json.loads((a.paired/'report.json').read_text())
    fused=indexed(read(a.paired/'fused.jsonl'));assert fused.keys()==cases.keys()
    frost_only=[];qwen_only=[]
    for i,row in rows.items():
        c=cases[i];first=row['candidates'][0];other=fused[i]['candidates'][0]['text']
        ok=first['text']==c['text'] and first['boundary']==f"0:{len(c['code'])}"
        detail=dict(id=i,text=c['text'],code=c['code'],frost=first['text'],oursQwen=other)
        if ok and other!=c['text']:frost_only.append(detail)
        if not ok and other==c['text']:qwen_only.append(detail)
    stats=dict(count=len(rows),correct=correct,accuracy=correct/len(rows),
               characterErrorRate=errors/sum(len(c['text']) for c in cases.values()),partialFirst=partial)
    grammar_trace=(root/'grammar.trace').read_text()
    assert 'zh-moqi.gram", O_RDONLY)' in grammar_trace
    assert f'mmap(NULL, {manifest["grammarBytes"]}, PROT_READ, MAP_SHARED' in grammar_trace
    report=dict(frost=stats,other=old['metrics'],manifest=manifest,
        verification=dict(count=len(checks),setInputMatchesKeystrokes=True,grammarReadOnlyMapped=True,emptyUserDictionaryAndObserverControl=control),
        frostOnlyCorrect=frost_only,oursQwenOnlyCorrect=qwen_only,
        hashes={name:digest(root/name) for name in ['test.jsonl','check-set-input.jsonl','rime-probe','librime-octagram.so']})
    if a.wanxiang:
        source=indexed(read(a.wanxiang/'test.jsonl'))
        verification=json.loads((a.wanxiang/'verification.json').read_text())
        assert verification['count']==100 and verification['candidateListsEqual']
        selected=[];wc=we=wp=0
        for i,c in cases.items():
            row=source[i]
            assert row['code']==c['code'] and row['candidates'],i
            first=row['candidates'][0]
            full=first['boundary']==f"0:{len(c['code'])}"
            wc+=full and first['text']==c['text'];wp+=not full
            we+=edit_distance(c['text'],first['text'])
            selected.append(row)
        (root/'wanxiang-paired.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in selected))
        report['wanxiang']=dict(count=len(selected),correct=wc,accuracy=wc/len(selected),
            characterErrorRate=we/sum(len(c['text']) for c in cases.values()),partialFirst=wp)
        report['wanxiangSource']=dict(root=str(a.wanxiang),testSha256=digest(a.wanxiang/'test.jsonl'),
            selectedSha256=digest(root/'wanxiang-paired.jsonl'),verification=verification)
    (root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    lines=['# 拼音方案：同批 1,050 句对比','',
        f"官方主线提交 `{manifest['commit']}`，保留官方词库、默认拼写与过滤链，以及自带 `zh-moqi.gram`（{manifest['grammarBytes']:,} 字节）和组词惩罚参数。",'',
        '| 方案 | 正确句数 | 准确率／通过率 | 字符错误率↓ |','|---|---:|---:|---:|']
    items=[('我们的联合模型，Beam 200，无重排',old['metrics']['jointBeam200']),
           ('我们的联合模型，Beam 200＋Qwen Top-10',old['metrics']['jointBeam200Qwen']),
           ('白霜拼音，官方默认排序',stats),
           ('WeType，快测错误再慢速复核',old['metrics']['wetypeAdaptive'])]
    if a.wanxiang:
        items.insert(2,('万象 Base v18.0.8＋LTS 简体模型',report['wanxiang']))
    for label,v in items:lines.append(f"| {label} | {v['correct']}/{v['count']} | {v['accuracy']:.3%} | {v['characterErrorRate']:.3%} |")
    if a.wanxiang:
        lines+=['', '- 万象复用既有 8,019 句真实 librime 结果，按相同 ID 抽取本批 1,050 句，逐条核对编码；没有混用全量准确率。官方 Base v18.0.8 + wanxiang-lts-zh-hans.gram（419,911,724 字节），隔离用户词库；原实验另有 100 句逐键输入与 set_input 候选列表一致性核验。',
            f"- 万象首选未消费完整输入：{wp} 句；抽取结果见 wanxiang-paired.jsonl，完整来源见 `{a.wanxiang}/REPORT.md`。"]
    lines+=['',f'相对我们的 Qwen 方案：白霜单独正确 {len(frost_only)} 句，我们单独正确 {len(qwen_only)} 句。',
        '', '## 验证与范围','',
        '- 同一份冻结注音的 1,050 条 test 样本，编码不超过 60 字母；不给额外分词边界，不调整官方参数迎合测试结果。',
        '- 真实 librime 1.16.1 + Lua 5.4 + 独立 octagram 插件；独立用户目录，关闭主词库用户学习，不提交文本。',
        '- 全部 1,050 句通过 process_key 逐键输入；另选 100 句与 set_input 的完整候选列表逐条比较，全部一致。',
        '- 另以空用户词库开启、去掉诊断滤镜的配置复测同样 100 句，候选正文和顺序全部一致。首次部署存在英语重复词条和依赖解析告警，但最终 4 个 schema 均成功，运行无错误日志。',
        '- 在过滤链最后添加仅修改注释的候选边界探针，保留正文和顺序；整句正确同时要求首选消费全部编码。',
        f'- 首选未消费完整输入的记录：{partial}；不会把局部词语候选当成完整句子计算正确。',
        '- strace 确认官方 zh-moqi.gram 被只读映射；未另外添加万象等外部大模型，也没有给白霜套用我们的 Beam 或 Qwen。',
        '- 我们的 Qwen 权重固定 alpha=0.45；WeType 一行是错误句慢速复核后的综合通过率，不是统一速度下的一次首选准确率。WeType 个人学习/在线设置未隔离。',
        '- 注音有既有粗标注局限，训练与测试语料互斥未验证；不由此推断日常输入全面优劣。没有覆盖本机小狼毫。','',
        f"官方源码：[gaboolic/rime-frost 固定快照](https://github.com/gaboolic/rime-frost/tree/{manifest['commit']})。",'']
    (root/'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps(stats,ensure_ascii=False,indent=2))
    print('frost-only',len(frost_only),'ours-qwen-only',len(qwen_only))


if __name__=='__main__':main()
