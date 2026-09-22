"""Frozen Beam-200 joint candidates, with/without Qwen on completed WeType cases."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import subprocess
import sys
import time
sys.path.insert(0,str(Path(__file__).resolve().parent.parent))
import analyze as metrics
from fuse import ranked


def win(path):
    return subprocess.check_output(['wslpath','-w',str(Path(path).resolve())],text=True).strip()


def main():
    p=argparse.ArgumentParser()
    p.add_argument('root',type=Path)
    p.add_argument('--report-only',action='store_true')
    a=p.parse_args();root=a.root.resolve()
    manifest=json.loads((root/'manifest.json').read_text())
    assert manifest['beam']==200 and manifest['top']==10
    assert metrics.digest(root/'ngram.jsonl')==manifest['ngramSubsetSha256']
    assert metrics.digest(root/'cases.jsonl')==manifest['casesSha256']
    for k,path in manifest['paths'].items():assert metrics.digest(Path(path))==manifest['fingerprints'][k],k
    exe=root/'client/TigerClaw.Core.Tests.exe'
    paths=[root/f'qwen-{i}.jsonl' for i in range(manifest['workers'])]
    if not a.report_only:
        def worker(i):
            command=[str(exe),'qwen',win(root/'ngram.jsonl'),win(paths[i]),win(manifest['paths']['host']),win(manifest['paths']['gguf']),str(i),str(manifest['workers'])]
            print('START',i,flush=True)
            started=time.monotonic()
            with (root/f'qwen-{i}.log').open('a') as log:
                log.write('COMMAND '+json.dumps(command)+'\n');log.flush()
                subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True)
            print('DONE',i,round(time.monotonic()-started,2),flush=True)
        with ThreadPoolExecutor(max_workers=manifest['workers']) as pool:
            list(pool.map(worker,range(manifest['workers'])))
    cases=metrics.indexed(metrics.read(root/'cases.jsonl'))
    decoded=metrics.validate(cases,metrics.read(root/'ngram.jsonl'),'test')
    assert len(decoded)==manifest['count']
    for i,path in enumerate(paths):
        meta=json.loads(Path(str(path)+'.manifest.json').read_text())
        assert meta['input'].lower()==manifest['ngramSubsetSha256']
        for k in ['host','gguf','executable']:assert meta[k].lower()==manifest['fingerprints'][k]
        assert meta['worker']==i and meta['workers']==manifest['workers'] and meta['top']==10
    qwen=metrics.qwen_rows(paths,decoded)
    wetype=metrics.indexed(metrics.read(root/'wetype.jsonl'))
    assert wetype.keys()==decoded.keys()
    fused={};rescued=[];regressed=[]
    with (root/'fused.jsonl').open('w') as output:
        for i,row in decoded.items():
            scores=qwen[i]['scores'] if len(row['candidates'])>1 else None
            candidates=ranked(row,scores,manifest['alpha'])
            fused[i]=candidates[0]['text'] if candidates else ''
            target=cases[i]['text'];base=row['candidates'][0]['text'] if row['candidates'] else ''
            detail=dict(id=i,text=target,code=row['code'],ngram=base,qwen=fused[i],baseRank=row['rank'])
            if base!=target and fused[i]==target:rescued.append(detail)
            if base==target and fused[i]!=target:regressed.append(detail)
            output.write(json.dumps(dict(id=i,text=target,code=row['code'],alpha=manifest['alpha'],candidates=candidates),ensure_ascii=False)+'\n')
    outputs={
        'jointBeam200':{i:r['candidates'][0]['text'] if r['candidates'] else '' for i,r in decoded.items()},
        'jointBeam200Qwen':fused,
        'wetypeFast':{i:r['fastText'] for i,r in wetype.items()},
        'wetypeAdaptive':{i:r['adaptiveText'] for i,r in wetype.items()}}
    total_chars=sum(len(r['text']) for r in cases.values())
    result={}
    for label,texts in outputs.items():
        correct=sum(text==cases[i]['text'] for i,text in texts.items())
        result[label]=dict(count=len(cases),correct=correct,accuracy=correct/len(cases),
            characterErrorRate=sum(metrics.edit_distance(cases[i]['text'],text) for i,text in texts.items())/total_chars)
    comparison={}
    for label in ['wetypeFast','wetypeAdaptive']:
        rescued_other=[];regressed_other=[]
        for i,text in fused.items():
            target=cases[i]['text'];other=outputs[label][i]
            item=dict(id=i,text=target,code=cases[i]['code'],qwen=text,wetype=other)
            if text==target and other!=target:rescued_other.append(item)
            if other==target and text!=target:regressed_other.append(item)
        comparison[label]=dict(oursRescued=rescued_other,oursRegressed=regressed_other,net=len(rescued_other)-len(regressed_other))
    latency=metrics.quantiles([r['ms'] for r in qwen.values() if r['count']>1])
    report=dict(manifest=manifest,metrics=result,ngramRecall=metrics.accuracy(list(decoded.values())),
        qwenLatencyMs=latency,rescued=rescued,regressed=regressed,net=len(rescued)-len(regressed),
        versusWeType=comparison,scoreFailures=0,scoreCount=len(qwen),
        resultHashes={p.name:metrics.digest(p) for p in paths})
    (root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    lines=['# 字音联合模型 Beam 200：Qwen 重排配对测试','',
        f"固定使用已完成 WeType 快测及必要慢速复核的 {len(cases)} 条相同样本（冻结 test 分区，输入不超过 60 字母）。",
        '原始拼音反查词库 + joint_3gram.klm，Beam 固定 200。无重排使用已冻结并验证指纹的候选；Qwen 对同一份前十候选重新评分。',
        f"融合公式 `(1-alpha)*ngramScore + alpha*qwenScore`，alpha={manifest['alpha']}，沿用此前 m5 开发集值，未针对联合模型或本轮测试样本调参。",'',
        '| 实现 | 首选正确/样本数 | 准确率或通过率 | 字符错误率↓ |','|---|---:|---:|---:|']
    labels={'jointBeam200':'联合模型 Beam 200，无重排','jointBeam200Qwen':'联合模型 Beam 200 + Qwen Top-10',
        'wetypeFast':'WeType：快速输入一次结果','wetypeAdaptive':'WeType：错误句慢速复核后的综合结果'}
    for k,v in result.items():lines.append(f"| {labels[k]} | {v['correct']}/{v['count']} | {v['accuracy']:.3%} | {v['characterErrorRate']:.3%} |")
    lines+=['',f'Qwen 相对无重排：救回 {len(rescued)} 句，退化 {len(regressed)} 句，净增加 {len(rescued)-len(regressed)} 句。',
            f"无重排 Top-10 召回：{report['ngramRecall']['top10']:.3%}。所有 Qwen 记录均成功，逐条校验 ID、候选数量、分数顺序所对应的输入指纹，无故障回退混入。",'',
            '## 范围与时序','',
            '- WeType 已停止；本轮只运行独立离线评分进程，没有模拟按键，也不部署或替换日用 Core/模型。',
            '- WeType 快测为 30ms/键、结束等 1 秒；错误句慢测为 100ms/键、结束等 3 秒。综合结果保留快测正确句，错误才慢测，是两种节奏下的综合通过率，不是统一速度下的一次首选准确率。',
            '- WeType 当前用户学习、在线设置和重复输入影响未隔离。参考拼音与训练语料仍有既有标注/重叠边界；本结果不是纯模型公平排名或日常输入验收。',
            '- Qwen3 0.6B Q8，独立 FullPinyinProbe 随机管道，4 进程×每进程 4 线程；不连接生产 Sentence 管道。',
            '- 下列耗时为上述并发条件下的完整 Top-10 请求耗时，不是按键响应延迟，也不与 WeType 人工等待时间比较。','',
            '| Qwen 请求耗时 | p50 | p95 | p99 | 最大 |','|---|---:|---:|---:|---:|',
            '| 毫秒 | '+' | '.join(f'{latency[k]:.2f}' for k in ['p50','p95','p99','max'])+' |','']
    (root/'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps(result,indent=2))
    print('rescued',len(rescued),'regressed',len(regressed),'net',report['net'])


if __name__=='__main__':main()
