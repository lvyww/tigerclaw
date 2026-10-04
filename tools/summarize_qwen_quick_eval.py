#!/usr/bin/env python3
"""Summarize complete frozen A/B/C runs, without treating failures as fallback wins."""
import argparse
import csv
import json
import math
from pathlib import Path
import numpy as np

def read(path):
    return [json.loads(line) for line in path.read_text().splitlines()]

def main():
    p = argparse.ArgumentParser()
    p.add_argument('work', type=Path)
    a = p.parse_args()
    w = a.work
    cases = json.loads((w/'cases.json').read_text())
    ids = [r['id'] for r in cases]
    frozen = read(w/'freeze-A.jsonl')
    assert [r['item']['id'] for r in frozen] == ids
    base = {r['item']['id']:r for r in frozen}
    runs = {}
    for label in 'BC':
        rows = read(w/f'score-{label}.jsonl')
        assert [r['item']['id'] for r in rows] == ids
        runs[label] = {r['item']['id']:r for r in rows}
        assert json.loads((w/f'compatibility-{label}.json').read_text())['pass']
        lifecycle = json.loads((w/f'score-{label}-lifecycle.json').read_text())
        if not lifecycle['normal_exit']:
            # Completed score rows remain valid when only the final harness
            # shutdown failed. Require separately documented lifecycle repair
            # plus exact diagnostic-score/ranking reproduction; never fake exit.
            assert label == 'B' and json.loads((w/'score-Bcheck-lifecycle.json').read_text())['normal_exit']
            assert json.loads((w/'compatibility-Bcheck.json').read_text())['pass']
            for check in read(w/'score-Bcheck.jsonl'):
                prior = runs['B'][check['item']['id']]
                assert check['scores'] == prior['scores'] and check['final'] == prior['final']
    records = []
    diagnostics = []
    for item in cases:
        cid = item['id']; pool = base[cid]['pool']
        r = dict(item=item, pool=pool, A=pool[0]['text'] if pool else None)
        for label in 'BC':
            row = runs[label][cid]
            assert row['item'] == item
            assert len(row['scores']) in (0, min(5,len(pool)))
            r[label] = row['final'][0]['text'] if row['final'] else None
            r[label+'_error'] = row['error']
            r[label+'_scores'] = row['scores']
            r[label+'_final'] = row['final']
        (diagnostics if item['diagnostic'] else records).append(r)
    assert len(records) == 3000 and len({r['item']['text'] for r in records}) == 3000
    summary = {}
    for source in ('old10k','articles','thucnews','all'):
        rows = [r for r in records if source == 'all' or r['item']['source'] == source]
        summary[source] = dict(n=len(rows), correct={k:sum(r[k]==r['item']['text'] for r in rows) for k in 'ABC'},
            top5=sum(any(c['text']==r['item']['text'] for c in r['pool'][:5]) for r in rows),
            errors={k:sum(r[k+'_error'] is not None for r in rows) for k in 'BC'},
            timeouts={k:sum('TimeoutException' in (r[k+'_error'] or '') for r in rows) for k in 'BC'})
    comparisons = {}
    for reference in 'BA':
        paired = [r for r in records if not r['C_error'] and (reference=='A' or not r['B_error'])]
        rescue = sum(r['C']==r['item']['text'] and r[reference]!=r['item']['text'] for r in paired)
        regress = sum(r['C']!=r['item']['text'] and r[reference]==r['item']['text'] for r in paired)
        # Paired source-stratified multinomial bootstrap: same sampled rows for
        # both systems; no assumption of independent system accuracies.
        rng = np.random.default_rng(20261004)
        diffs = np.zeros(50000)
        for source in ('old10k','articles','thucnews'):
            rr=[r for r in paired if r['item']['source']==source]
            pos=sum(r['C']==r['item']['text'] and r[reference]!=r['item']['text'] for r in rr)
            neg=sum(r['C']!=r['item']['text'] and r[reference]==r['item']['text'] for r in rr)
            sample=rng.multinomial(len(rr),[pos/len(rr),neg/len(rr),1-(pos+neg)/len(rr)],size=50000)
            diffs += sample[:,0]-sample[:,1]
        ci=(np.quantile(diffs,[.025,.975])*100/len(paired)).tolist()
        comparisons['C_vs_'+reference]=dict(paired_n=len(paired),excluded_failures=3000-len(paired),rescue=rescue,regress=regress,
            net=rescue-regress,delta_pp=(rescue-regress)*100/len(paired),paired_bootstrap_95_pp=ci,
            caveat='Historical cases, possible record/training dependence; no equivalence claim; source-stratified case bootstrap, not independent generalization.')
        changes=[r for r in records if r['C']!=r[reference] or r['C_error'] or (reference=='B' and r['B_error'])]
        (w/f'changes-C-vs-{reference}.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in changes))
        with (w/f'changes-C-vs-{reference}.csv').open('w',newline='',encoding='utf-8-sig') as f:
            writer=csv.writer(f);writer.writerow(['id','source','code','target','A','B','C','C_error','B_error'])
            for r in changes:writer.writerow([r['item'][k] for k in ('id','source','code','text')]+[r[k] for k in ('A','B','C','C_error','B_error')])
    performance={}
    for label in 'BC':
        passes=[read(w/f'perf-{label}{i}.jsonl') for i in (1,2)]
        assert all(len(v)==100 for v in passes)
        rows=sum(passes,[])
        good=[r for r in rows if not r['error']]
        times=[r['elapsed_ms'] for r in good]
        score_lifecycle=json.loads((w/f'score-{label}-lifecycle.json').read_text())
        first = label if score_lifecycle['normal_exit'] else label+'check'
        lifecycle=[json.loads((w/f'{mode}-{name}-lifecycle.json').read_text()) for mode,name in [('score',first),('perf',label+'1'),('perf',label+'2')]]
        assert all(r['normal_exit'] and r['exit_code']==0 for r in lifecycle)
        performance[label]=dict(n=len(rows),errors=len(rows)-len(good),p50_ms=float(np.quantile(times,.5)),
            p95_ms=float(np.quantile(times,.95)),max_ms=max(times),private_peak_bytes=max(r['private_bytes'] for r in rows),
            working_set_peak_bytes=max(r['working_set'] for r in rows),startup_ms=[r['startup_ms'] for r in lifecycle],
            accuracy_run_lifecycle=score_lifecycle,
            timeout_rate=sum('TimeoutException' in (r['error'] or '') for r in rows)/len(rows),
            by_length={group:dict(n=sum(r['item']['perf_group']==group for r in rows),
                p50_ms=float(np.quantile([r['elapsed_ms'] for r in good if r['item']['perf_group']==group],.5)),
                p95_ms=float(np.quantile([r['elapsed_ms'] for r in good if r['item']['perf_group']==group],.95)))
                for group in ('short','medium','long')},
            normal_exits=3,per_pass=[dict(p50_ms=float(np.quantile([r['elapsed_ms'] for r in v if not r['error']],.5)),
                p95_ms=float(np.quantile([r['elapsed_ms'] for r in v if not r['error']],.95))) for v in passes])
    score_deltas=[c-b for r in records for b,c in zip(r['B_scores'],r['C_scores'])]
    scales=dict(candidate_count=len(score_deltas),C_minus_B_mean=float(np.mean(score_deltas)),
                C_minus_B_median=float(np.median(score_deltas)))
    result=dict(accuracy=summary,comparisons=comparisons,performance=performance,diagnostics=diagnostics,score_scales=scales,
                independent_generalization=False,production_changed=False)
    (w/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
    models=json.loads((w/'models.json').read_text())
    lines=['# Qwen Q4 固定权重快速替换验证','',
           '结论：兼容性通过，体积与工作集节省明显，值得继续作为低内存方案验证；本轮尚不足以判断准确率差异，不能宣称等效或据此直接替换日用 Q8。延迟仅小幅改善，未证明稳定加速。',
           '',f"模型大小：B {models['B']['bytes']/1e6:.2f} MB，C {models['C']['bytes']/1e6:.2f} MB，减少 {(1-models['C']['bytes']/models['B']['bytes'])*100:.1f}%。B 为 Qwen3-0.6B-Base Q8，C 元数据指向 Qwen2.5 0.5B 系列 Q4_0；并非同模型的单独量化对照。",'',
           '生产五阶解码器、日用配置快照，学习和提前上屏关闭，每条清空上下文。B/C 分别从同一冻结前五候选开始，调用生产融合函数，无调权。','',
           '| 来源 | 条数 | A 关闭神经重排 | B Qwen3 Q8 | C 新 Qwen Q4 | 基础 Top-5 入池 |',
           '|---|---:|---:|---:|---:|---:|']
    for source,r in summary.items():
        lines.append(f"| {source} | {r['n']} | "+' | '.join(f"{r['correct'][k]} ({r['correct'][k]/r['n']:.2%})" for k in 'ABC')+f" | {r['top5']} ({r['top5']/r['n']:.2%}) |")
    lines+=['','失败不计作成功重排；固定 3,000 分母的正确数如上，配对差异排除失败并单列。',json.dumps({s:r['errors'] for s,r in summary.items()},ensure_ascii=False),'']
    for key,r in comparisons.items():
        lines.append(f"- {key}：救回 {r['rescue']}，改错 {r['regress']}，净 {r['net']:+d}；差值 {r['delta_pp']:+.3f} 个百分点，配对 bootstrap 95% 区间 [{r['paired_bootstrap_95_pp'][0]:+.3f}, {r['paired_bootstrap_95_pp'][1]:+.3f}]。")
    lines+=['','区间按来源分层、逐样本配对重采样 50,000 次，种子 20261004。历史样本可能有关联和训练重叠，区间未校正这些因素，不能证明独立泛化或等效。',
        '', '| 性能（200 请求/模型） | B Q8 | C Q4 |','|---|---:|---:|']
    for key,title,scale in [('p50_ms','P50（ms）',1),('p95_ms','P95（ms）',1),('max_ms','最大（ms）',1),
                            ('errors','错误请求',1),('timeout_rate','超时率（%）',.01),
                            ('private_peak_bytes','私有内存采样最大值（MB）',1e6),('working_set_peak_bytes','工作集采样最大值（MB）',1e6)]:
        lines.append(f"| {title} | {performance['B'][key]/scale:.2f} | {performance['C'][key]/scale:.2f} |")
    lines.append('| 三次启动至可服务（ms） | '+' | '.join(' / '.join(f'{x:.1f}' for x in performance[k]['startup_ms']) for k in 'BC')+' |')
    lines+=['','性能按 B→C→C→B，每轮预热 10 次。100 个输入重复两轮；短/中/长 20/40/40，分组规则见 performance-selection.json。',
        '表中每模型三次正常退出的启动计时，启动测到 hello 成功；操作系统文件缓存可能影响结果。峰值内存是请求结束采样的最大值，不是精确瞬时峰值。',
        '120 秒超时；隔离服务同时最多一个。两模型各 2950 条实际重排，50 条单候选跳过调用但计入准确率分母；诊断另算。重复、单条/批量、倒序评分、断开取消恢复和旧代结果拒收证据见 compatibility 与逐行结果；不替代真实打字体验。','',
        '设备：Windows 11 ARM64，Snapdragon X2 Elite Extreme X2E94100，18 逻辑处理器；服务采用日用 ARM64 程序原有线程设置，无覆盖。详细系统信息见 machine.json。','',
        '## 分数尺度与固定权重','',
        f"同一候选的 C−B 神经总分平均 {scales['C_minus_B_mean']:.3f}，中位数 {scales['C_minus_B_median']:.3f}。分数尺度或原融合权重不匹配仍有可能，但统一绝对偏移在同权候选间会抵消，不能仅由总分更负就断定失配；模型偏好、tokenizer、EOS 和量化影响没有被分离。本轮没有调参。",'',
        '## 两组诊断（不计准确率）','']
    for r in diagnostics:
        lines += ['',f"### {r['item']['code']}",'','| 候选 | 基础分 | B 神经分 | B 融合分 | B 排名 | C 神经分 | C 融合分 | C 排名 |','|---|---:|---:|---:|---:|---:|---:|---:|']
        for i,c in enumerate(r['pool'][:5]):
            vals=[]
            for k in 'BC':
                final=r[k+'_final'];j=next(j for j,x in enumerate(final) if x['text']==c['text'])
                vals += [f"{r[k+'_scores'][i]:.6f}", f"{final[j]['fused']:.6f}",str(j+1)]
            lines.append(f"| {c['text']} | {c['score']:.6f} | "+' | '.join(vals)+' |')
    lines+=['','## 复现与边界','',
        '完整样本及冻结候选在 cases.json / freeze-A.jsonl；B/C 原始结果在 score-B/C.jsonl；所有首选变化在 changes-C-vs-{A,B}.csv/jsonl。',
        '模型 SHA256、GGUF 元数据见 models.json；码表/补充词库/五阶/配置身份与抽样规则见 sampling.json，各运行参数与 Core/工具身份见 *-manifest.json。',
        '评分为 BOS＋候选原文＋EOS，各模型采用自身 tokenizer 与特殊符号，未加聊天提示。服务固定 q8 标签未用于判定模型身份。',
        '工具修复记录：初期取消时重复释放已关闭管道、Escape 使引擎退出整句状态，均在完整评测前修复；失败日志保留于 development-attempt-*。B 的 3002 条完整评分后，工具未读取 shutdown 回执导致退出超时，原始 lifecycle 仍标记 normal_exit=false。修复仅涉及退出回执读取，未改解码或评分；Bcheck 重新通过兼容性、两组诊断分数/排序精确复现并正常退出。表中 B 的首次正常启动用 Bcheck，另两次为 B1/B2；C 用完整评分和 C1/C2。各版本源码/二进制均归档，没有将不同构建的行拼接成准确率结果。',
        'Q4 的微调来源尚不明确；差异同时包含模型、tokenizer、特殊符号及量化变化。无生产部署，无学习记录修改，未恢复其他暂停任务。']
    (w/'REPORT.md').write_text('\n'.join(lines)+'\n')
    print(json.dumps(dict(accuracy=summary,comparisons=comparisons,performance=performance),ensure_ascii=False,indent=2))

if __name__=='__main__':main()
