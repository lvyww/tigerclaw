"""Paired joint-model Beam comparison, with frozen-input and shard checks."""
import argparse
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import analyze as m


def load(root):
    manifest = json.loads((root/'run-manifest.json').read_text())
    assert m.digest(root/'cases.jsonl') == manifest['cases']
    assert m.digest(root/'tokens.json') == manifest['tokens']
    assert m.digest(root/'bin/Joint.dll') == manifest['executable']
    assert m.digest(root/'bin/libjointkenlm.so') == manifest['native']
    cases = m.indexed(m.read(root/'cases.jsonl'))
    path = root/'joint.jsonl'
    shards = json.loads(Path(str(path)+'.manifest.json').read_text())
    assert shards.pop('sha256') == m.digest(path)
    assert len(shards) == manifest['jobs']
    for meta in shards.values():
        for key in ['table','cases','executable']:
            assert meta[key].lower() == manifest[key]
        assert meta['model'].lower() == manifest['models']['joint']
        assert meta['tokenMap'].lower() == manifest['tokens']
        assert meta['beam'] == manifest['beam']
        assert meta['reward'] == manifest['reward'] == 2
        assert meta['naturalLog'] and not manifest['qwen']
    rows = m.validate(cases, m.decode_rows(path), 'test')
    latency_path = root/'latency-joint.jsonl'
    meta = json.loads(Path(str(latency_path)+'.manifest.json').read_text())
    assert meta == next(iter(shards.values()))
    latency = m.read(latency_path)
    expected_keys = []
    for c in sorted(cases.values(),key=lambda c:c['id'])[:100]:
        expected_keys.extend((c['id'],'append',i) for i in range(1,len(c['code'])+1))
        expected_keys.extend((c['id'],'backspace',i) for i in range(len(c['code'])-1,-1,-1))
    assert [(r['id'],r['direction'],r['keys']) for r in latency] == expected_keys
    errors = sum(m.edit_distance(r['text'],r['candidates'][0]['text'] if r['candidates'] else '') for r in rows.values())
    metrics = dict(beam=manifest['beam'],count=len(rows),correct=sum(r['rank']==1 for r in rows.values()),
                   **m.accuracy(list(rows.values())),characterErrorRate=errors/sum(len(r['text']) for r in rows.values()),
                   latencyOperations=len(latency),latency={d:m.quantiles([r['ms'] for r in latency if r['direction']==d]) for d in ['append','backspace']})
    return manifest, rows, metrics


def main():
    p = argparse.ArgumentParser()
    p.add_argument('baseline',type=Path)
    p.add_argument('root',type=Path)
    a=p.parse_args()
    old_manifest, old, old_metrics=load(a.baseline)
    manifest, new, metrics=load(a.root)
    for key in ['table','cases','tokens','native','reward','qwen']:
        assert manifest[key] == old_manifest[key],key
    assert manifest['models']['joint'] == old_manifest['models']['joint']
    rescued=[];regressed=[];changed=0
    for i,r in new.items():
        o=old[i]
        first=lambda x:x['candidates'][0]['text'] if x['candidates'] else ''
        changed+=first(o)!=first(r)
        detail=dict(id=i,text=r['text'],code=r['code'],old=first(o),new=first(r),oldRank=o['rank'],newRank=r['rank'])
        if o['rank']!=1 and r['rank']==1:rescued.append(detail)
        if o['rank']==1 and r['rank']!=1:regressed.append(detail)
    report=dict(baseline=old_metrics,current=metrics,rescued=rescued,regressed=regressed,
                net=len(rescued)-len(regressed),changedTop1=changed,manifest=manifest,
                baselineManifest=old_manifest)
    (a.root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
    lines=['# 联合字音模型：Beam 对照','',
           '相同的 8,019 条冻结测试句、原始拼音反查词库、joint_3gram.klm 和字音映射。仅改变 Beam；每字奖励 +2，不使用 Qwen。','',
           '| Beam | 首选正确 | 首选准确率 | Top-5 | Top-10 | Top-50 | 字符错误率↓ |',
           '|---|---:|---:|---:|---:|---:|---:|']
    for v in [old_metrics,metrics]:
        lines.append(f"| {v['beam']} | {v['correct']}/{v['count']} | "+' | '.join(f'{v[k]:.3%}' for k in ['top1','top5','top10','top50','characterErrorRate'])+' |')
    lines+=['',f'救回 {len(rescued)} 条，退化 {len(regressed)} 条，净变化 {report["net"]:+d} 条；首选改变 {changed} 条。',
            '', '## 单进程逐键延迟（毫秒）','',
            '相同 100 句，共 10,412 次追加/回删；在批量解码进程退出后测量。基线采用上一轮同平台结果。','',
            '| Beam | 操作 | p50 | p95 | p99 | 最慢 |','|---|---|---:|---:|---:|---:|']
    for v in [old_metrics,metrics]:
        for d in ['append','backspace']:
            lines.append(f"| {v['beam']} | {d} | "+' | '.join(f'{v["latency"][d][k]:.3f}' for k in ['p50','p95','p99','max'])+' |')
    lines+=['','耗时含首次触页和运行时抖动；为离线解码器测试，不代表实际 UI 延迟。',
            '测试输入保留既有粗注音误差，未验证训练/测试语料互斥。没有部署或替换日用模型。',
            '每个编码位置保留最多 Beam 个状态；暂存超过 4×Beam 时裁到 2×Beam；最终输出 50 个不同汉字文本候选。','']
    (a.root/'REPORT.md').write_text('\n'.join(lines))
    print(json.dumps({k:v for k,v in report.items() if k not in ['rescued','regressed','manifest','baselineManifest']},indent=2))
    print(f'rescued={len(rescued)} regressed={len(regressed)}')


if __name__=='__main__':main()
