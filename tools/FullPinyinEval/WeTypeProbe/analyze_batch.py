"""Paired black-box comparison on frozen, ID-selected full-pinyin cases."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent.parent))
import analyze as metrics


def read(path):
    return [json.loads(line) for line in path.open()]


def wilson(k,n):
    z=1.959963984540054
    p=k/n
    mid=(p+z*z/(2*n))/(1+z*z/n)
    half=z*math.sqrt(p*(1-p)/n+z*z/(4*n*n))/(1+z*z/n)
    return [mid-half,mid+half]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('mode',choices=['extract','report'])
    parser.add_argument('root',type=Path)
    a=parser.parse_args();root=a.root
    cases=metrics.indexed(read(root/'cases.jsonl'))
    if a.mode=='extract':
        experiments=root.parent
        paths={'m5':experiments/'joint-pinyin-20260921/m5.jsonl',
               'joint200':experiments/'joint-pinyin-20260921/joint.jsonl',
               'joint2000':experiments/'joint-pinyin-beam2000-20260921/joint.jsonl'}
        result={};sources={}
        for name,path in paths.items():
            rows=[]
            with path.open() as f:
                for line in f:
                    row=json.loads(line)
                    if row['id'] in cases:
                        assert row['code']==cases[row['id']]['code'] and row['text']==cases[row['id']]['text']
                        rows.append(dict(id=row['id'],text=row['candidates'][0]['text'],rank=row['rank']))
            assert len(rows)==len(cases)
            result[name]=rows;sources[name]=dict(path=str(path),sha256=metrics.digest(path))
        result['sources']=sources
        result['casesSha256']=metrics.digest(root/'cases.jsonl')
        (root/'baselines.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
        return
    baseline=json.loads((root/'baselines.json').read_text())
    assert baseline['casesSha256']==metrics.digest(root/'cases.jsonl')
    events=read(root/'wetype.jsonl')
    assert events[-1]['event']=='stop' and events[-1]['detail']=='completed'
    actual=metrics.indexed([r for r in events if r['event']=='result'])
    assert actual.keys()==cases.keys()
    for i,r in actual.items():
        assert r['code']==cases[i]['code']
        assert r['status']=='ok' and r['rawVerified'] and not r['interrupted'],r
        assert r['compositionAfter']==''
        assert ''.join(c for c in r['compositionBefore'] if 'a'<=c<='z')==r['code']
    groups={k:metrics.indexed(baseline[k]) for k in ['m5','joint200','joint2000']}
    groups['wetype']=actual
    stats={};comparisons={}
    for k,rows in groups.items():
        assert rows.keys()==cases.keys()
        correct=sum(r['text']==cases[i]['text'] for i,r in rows.items())
        errors=sum(metrics.edit_distance(cases[i]['text'],r['text']) for i,r in rows.items())
        stats[k]=dict(count=len(rows),correct=correct,top1=correct/len(rows),wilson95=wilson(correct,len(rows)),
                      characterErrorRate=errors/sum(len(c['text']) for c in cases.values()))
        if k=='wetype':continue
        rescued=[];regressed=[]
        for i,c in cases.items():
            old=rows[i]['text'];new=actual[i]['text'];target=c['text']
            item=dict(id=i,target=target,code=c['code'],model=old,wetype=new)
            if old!=target and new==target:rescued.append(item)
            if old==target and new!=target:regressed.append(item)
        comparisons[k]=dict(wetypeRescued=rescued,wetypeRegressed=regressed,net=len(rescued)-len(regressed))
    manifest=json.loads((root/'manifest.json').read_text())
    count=len(cases)
    assert count==manifest['count']
    for r in actual.values():
        assert r['keyDelayMs']==manifest['keyDelayMs']
        assert r['postInputWaitMs']>=manifest['finalWaitMs']+400
    timing_path=root/'timing-comparison.json'
    timing=json.loads(timing_path.read_text()) if timing_path.exists() else None
    report=dict(metrics=stats,comparisons=comparisons,
                totalScheduledCaseWallMs=sum(r['caseWallMs'] for r in actual.values()),
                manifest=manifest,timingAudit=timing,
                caseSha256=metrics.digest(root/'cases.jsonl'),resultSha256=metrics.digest(root/'wetype.jsonl'),
                version='2.1.4.6',validRawCount=sum(r['rawVerified'] for r in actual.values()))
    (root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    names={'m5':'原 m5 / Beam 200','joint200':'字音联合 / Beam 200','joint2000':'字音联合 / Beam 2000','wetype':'WeType 2.1.4.6'}
    lines=['# WeType 黑盒输入配对测试','',
           f'从既有冻结测试集筛选编码不超过 60 个字母的句子，按 SHA256 固定顺序抽取 {count} 句；各模型比较完全相同的句子。',
           'WeType 为实际虚拟键输入 + 空格确认，保留当前用户和在线设置。三个离线模型使用原始拼音词库，不启用 Qwen。','',
           '| 实现 | 首选正确 | 首选准确率 | Wilson 95% 区间 | 字符错误率↓ |',
           '|---|---:|---:|---:|---:|']
    for k,v in stats.items():
        lo,hi=v['wilson95']
        lines.append(f"| {names[k]} | {v['correct']}/{v['count']} | {v['top1']:.2%} | {lo:.2%}–{hi:.2%} | {v['characterErrorRate']:.2%} |")
    lines+=['']
    for k,v in comparisons.items():
        lines.append(f"WeType 对比 {names[k]}：救回 {len(v['wetypeRescued'])}，退化 {len(v['wetypeRegressed'])}，净变化 {v['net']:+d}。")
    lines+=['','## 输入完整性与范围','',
            f'- {count} 条的提交前编码逐条去分隔符，与发送编码完全一致；提交后 composition 为空，无焦点中断或超时。',
            f'- 每字母请求间隔 {manifest["keyDelayMs"]}ms（Windows 定时器实际间隔可更长）；完整输入后等待 {manifest["finalWaitMs"]}ms，提交后至少 400ms 文本稳定。',
            '- 另用 10 条开发集样本比较 30ms 与 100ms 按键间隔；结果详见 pilot-comparison.json。重复开发样本可能受学习影响，未用于正式准确率。',
            '- 长句校准发现 73 字母输入仅保留前 60 字母，慢速输入同样如此。因此本次主动限定输入长度，未将截断结果混入模型准确率。',
            '- 每句取消旧 composition 并清空测试控件；未删除或重建 WeType 个人词库，也未隔离云服务。测试提交可能进入自学习。',
            '- 注音沿用冻结测试集，保留其粗标注误差；完全匹配参考句才算正确。没有证明测试与训练语料互斥。',
            f'- 本结果只适用于该 {count} 句及当前设置；不能将抽样数字当成全部 8,019 句成绩，也没有测 Top-10。',
            '- caseWallMs 等耗时包含主动等待及模拟打字，不是输入法解码延迟，不用于与离线模型 p95 比较。','']
    if manifest.get('priorRun'):
        lines+=['## 时序复核','',
                '初轮 30ms/键、结束等待 1000ms 的 300 句测试后来发现速度依赖：抽查 5 句中有 3 句在 100ms/键时输出改变，包括末尾漏字恢复。初轮 61.33% 仅为该快输入条件下的结果，不能作为稳定准确率结论。',
                '为控制耗时，慢速复测固定取原随机顺序前 100 句，未按正确与否挑样本。样本已经经历快输入测试，未清除自学习；反复测试的影响未隔离。','']
        if timing is not None:
            lines.append(f'额外时序核验：100ms 与 200ms 两档均等待 3000ms 后提交，{len(timing)} 句中 {sum(r["same"] for r in timing)} 句输出一致。详细文本见 timing-comparison.json。')
    (root/'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps(stats,ensure_ascii=False,indent=2))
    print({k:{'rescued':len(v['wetypeRescued']),'regressed':len(v['wetypeRegressed']),'net':v['net']} for k,v in comparisons.items()})


if __name__=='__main__':main()
