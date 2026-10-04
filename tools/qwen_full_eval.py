#!/usr/bin/env python3
"""Full historical A/B/C evaluation. Isolated resources; sequential native hosts."""
import argparse
import collections
import csv
import fcntl
import hashlib
import json
import math
import os
import shutil
import subprocess
import time
from pathlib import Path

from prepare_qwen_quick_eval import identity
from run_qwen_quick_eval import win

SOURCES = {'old10k': 10000, 'articles': 33129, 'thucnews': 30000}


def save(path, value):
    tmp = path.with_suffix(path.suffix + '.partial')
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2))
    tmp.replace(path)


def rows(path):
    with path.open(encoding='utf-8-sig') as stream:
        for line in stream:
            yield json.loads(line)


def replay_fusion(pool, ranking, scores):
    """Independent cache-only reproduction; no model call or weight fitting."""
    count = ranking['rerank_count']
    assert len(scores) == count and all(math.isfinite(x) for x in scores)
    fused = []
    for i, candidate in enumerate(pool[:count]):
        base = candidate['score']
        if ranking['policy'] == 'candidate-convex':
            alpha = ranking['alphas'][i]
            score = (1-alpha)*base + alpha*scores[i]
        else:
            assert ranking['policy'] == 'shared-additive'
            score = base + ranking['additive_weight']*scores[i]
        fused.append(dict(text=candidate['text'],score=base,fused=score,rank=candidate['rank']))
    def key(c):
        # .NET String.CompareOrdinal compares UTF-16 code units, not scalar order.
        text = c['text'].encode('utf-16-be')
        return (-c['fused'],c['rank'],text) if ranking['prefer_score'] else (c['rank'],-c['fused'],text)
    fused.sort(key=key)
    return fused


def prepare(a):
    if a.work.exists():
        raise ValueError('Use a fresh experiment directory')
    a.work.mkdir(parents=True)
    previous = json.loads((a.quick / 'sampling.json').read_text())
    cases, sources = [], {}
    (a.work / 'case-sources').mkdir()
    for source, count in SOURCES.items():
        src = Path(previous['sources'][source]['path'])
        assert identity(src)['sha256'] == previous['sources'][source]['sha256']
        dst = a.work / 'case-sources' / (source + '.tsv')
        shutil.copyfile(src, dst)
        source_rows = dst.read_text().splitlines()
        assert len(source_rows) == count
        for line in source_rows:
            cid, group, code, text = line.split('\t')
            if source == 'old10k':
                assert group == 'old' and 1 <= int(cid.removeprefix('old_')) <= 10000
            key = hashlib.sha256(f'20261004\t{source}\t{cid}\t{code}\t{text}'.encode()).hexdigest()
            cases.append(dict(id=f'{source}:{cid}', source=source, group=group, code=code, text=text,
                              hash=key, diagnostic=False))
        sources[source] = dict(original=identity(src), frozen=identity(dst), count=count)
    assert len(cases) == 73129 and len({r['id'] for r in cases}) == 73129
    unique_targets = len({r['text'] for r in cases})
    cases += [r for r in json.loads((a.quick / 'cases.json').read_text()) if r['diagnostic']]
    save(a.work / 'cases.json', cases)
    all_by_id = {r['id']:r for r in cases}
    for prior in json.loads((a.quick / 'cases.json').read_text()):
        assert all_by_id[prior['id']] == prior
    assert not list((a.quick / 'runtime').rglob('自学习-*.txt')), 'Frozen runtime must not contain learning journals'
    shutil.copytree(a.quick / 'runtime', a.work / 'runtime')
    assert not list((a.work / 'runtime').rglob('*.log'))
    shutil.copyfile(a.quick / 'TigerClaw.QwenQuickProbe.exe', a.work / 'TigerClaw.QwenQuickProbe.exe')
    (a.work / 'models').mkdir()
    models = json.loads((a.quick / 'models.json').read_text())
    for label, model in models.items():
        src = Path(model['path'])
        if not src.is_absolute():
            src = a.repo / src
        assert identity(src)['sha256'] == model['sha256']
        dst = a.work / 'models' / (label + '.gguf')
        shutil.copyfile(src, dst)
        models[label] = dict(identity(dst), metadata=model['metadata'])
        assert models[label]['sha256'] == model['sha256']
    save(a.work / 'models.json', models)
    for name in ('performance-cases.json', 'performance-selection.json', 'machine.json'):
        shutil.copyfile(a.quick / name, a.work / name)
    # Keep the exact already-frozen performance sample, independent of new scores.
    save(a.work / 'plan.json', dict(created_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        rows=73129, diagnostics=2, counts=SOURCES, unique_target_texts=unique_targets,
        repeated_target_rows=73129-unique_targets, preserve_all_historical_rows=True,
        seed=20261004, order='source order then original TSV order; no resampling or deduplication',
        quick_reference=str(a.quick), sources=sources, cases=identity(a.work / 'cases.json'),
        runtime=[identity(p) for p in sorted((a.work / 'runtime').rglob('*')) if p.is_file()],
        host=identity(a.work / 'TigerClaw.QwenQuickProbe.exe'), models=models,
        no_tuning=True, learning=False, early_commit=False, production_changes=False,
        service_concurrency=1, performance_sample='same frozen 100 inputs as quick experiment'))
    print('PREPARED', len(cases), 'rows including diagnostics;', unique_targets, 'unique targets', flush=True)


def command(a, mode, label, cases=None):
    model_label = label[0] if label[0] in 'BC' else 'C'
    return [a.dotnet, win(a.work / 'build' / 'TigerClaw.Core.Tests.dll'), '--quick', win(a.repo),
            win(a.work), mode, label, win(a.work / 'models' / (model_label + '.gguf')),
            win(cases or a.work / ('performance-cases.json' if mode == 'perf' else 'cases.json')), '--resume']


def run(a):
    lock = (a.work / 'runner.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    save(a.work / 'runner.json', dict(pid=os.getpid(), started=time.time(), work=str(a.work)))
    plan = json.loads((a.work / 'plan.json').read_text())
    assert identity(a.work / 'cases.json')['sha256'] == plan['cases']['sha256']
    for entry in plan['runtime'] + list(plan['models'].values()) + [plan['host']]:
        assert identity(entry['path'])['sha256'] == entry['sha256'], entry['path']
    # Use the exact production assemblies accepted by the quick experiment.
    for name in ('TigerClaw.Core.dll', 'TigerClaw.Pinyin.dll'):
        assert identity(a.work / 'build' / name)['sha256'] == identity(a.quick / 'build' / name)['sha256'], name
    phases = [('freeze','A'), ('score','B'), ('score','C'), ('perf','B1'), ('perf','C1'), ('perf','C2'), ('perf','B2')]
    commands = [command(a, mode, label) for mode, label in phases]
    save(a.work / 'commands.json', commands)
    for (mode, label), cmd in zip(phases, commands):
        if (a.work / 'STOP').exists():
            save(a.work / 'status.json', dict(state='stopped', mode=mode, label=label, time=time.time()))
            return
        complete = a.work / f'{mode}-{label}-complete.json'
        if complete.exists():
            previous = json.loads((a.work / f'{mode}-{label}-manifest.json').read_text())
            for key, path in [('tool',a.work/'build/TigerClaw.Core.Tests.dll'),('core',a.work/'build/TigerClaw.Core.dll')]:
                assert previous[key]['sha256'].lower() == identity(path)['sha256'], (mode,label,key)
            print('ALREADY COMPLETE', mode, label, flush=True)
            continue
        status = dict(state='running', mode=mode, label=label, started=time.time(), command=cmd)
        save(a.work / 'status.json', status)
        print('START', mode, label, flush=True)
        with (a.work / f'{mode}-{label}.log').open('a') as log:
            result = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT)
        status.update(finished=time.time(), exit_code=result.returncode)
        if result.returncode:
            save(a.work / 'status.json', dict(status, state='failed'))
            raise RuntimeError(f'{mode} {label} failed; see log')
        if not complete.exists():
            save(a.work / 'status.json', dict(status, state='stopped'))
            return
        save(a.work / 'status.json', dict(status, state='phase-complete'))
        print('DONE', mode, label, flush=True)
    summarize(a)
    save(a.work / 'status.json', dict(state='complete', time=time.time()))
    save(a.work / 'artifacts-sha256.json', [identity(p) for p in sorted(a.work.rglob('*'))
        if p.is_file() and p.name not in ('artifacts-sha256.json','driver.log','runner.lock')])


def summarize(a):
    # Stream the three large files together; keep only paired counters and changes.
    import itertools
    import numpy as np
    expected = json.loads((a.work / 'cases.json').read_text())
    assert collections.Counter(r['source'] for r in expected if not r['diagnostic']) == SOURCES
    summary = {s:dict(n=0, correct={k:0 for k in 'ABC'}, top5=0, errors={k:0 for k in 'BC'}, timeouts={k:0 for k in 'BC'})
               for s in [*SOURCES, 'all']}
    paired = {ref:{s:collections.Counter() for s in SOURCES} for ref in 'AB'}
    diagnostics, failures = [], []
    quick_a = {r['item']['id']:r for r in rows(a.quick / 'freeze-A.jsonl')}
    quick_scores = {k:{r['item']['id']:r for r in rows(a.quick / f'score-{k}.jsonl')} for k in 'BC'}
    parity = collections.Counter()
    streams = {ref:(a.work / f'changes-C-vs-{ref}.jsonl').open('w') for ref in 'AB'}
    csv_streams = {ref:(a.work / f'changes-C-vs-{ref}.csv').open('w', newline='', encoding='utf-8-sig') for ref in 'AB'}
    tuning = (a.work / 'tuning-cache.jsonl').open('w')
    writers = {ref:csv.writer(f) for ref,f in csv_streams.items()}
    for writer in writers.values():
        writer.writerow(['id','source','code','target','A','B','C','B_error','C_error'])
    total = 0
    try:
        for item,base,b,c in itertools.zip_longest(expected, rows(a.work/'freeze-A.jsonl'), rows(a.work/'score-B.jsonl'), rows(a.work/'score-C.jsonl')):
            assert all(x is not None for x in (item,base,b,c)), 'Missing or extra result rows'
            assert item == base['item'] == b['item'] == c['item']
            assert base['ranking'] == b['ranking'] == c['ranking']
            assert all(x['learning'] == 0 for x in base['pool'])
            cid = item['id']
            if cid in quick_a:
                assert base['pool'] == quick_a[cid]['pool'], ('quick pool differs',cid)
                for label,row in [('B',b),('C',c)]:
                    prior = quick_scores[label][cid]
                    assert row['final'] == prior['final'], ('quick final differs',label,cid)
                    assert row['scores'] == prior['scores'], ('quick scores differ',label,cid)
                parity['exact_quick_cases'] += 1
            record = dict(item=item,pool=base['pool'],A=base['pool'][0]['text'] if base['pool'] else None)
            for label,row in [('B',b),('C',c)]:
                assert len(row['scores']) in (0,min(5,len(base['pool'])))
                record[label] = row['final'][0]['text'] if row['final'] else None
                record[label+'_error'],record[label+'_scores'],record[label+'_final'] = row['error'],row['scores'],row['final']
                if not row['error'] and len(base['pool']) > 1:
                    replay = replay_fusion(base['pool'],base['ranking'],row['scores'])
                    for actual,computed in zip(row['final'],replay):
                        assert actual['text'] == computed['text'] and abs(actual['fused']-computed['fused']) < 1e-9, ('cache replay differs',label,cid)
                    parity[label+'_cache_replay'] += 1
                if row['error']:
                    failures.append(dict(id=cid,model=label,error=row['error']))
                if row['staleRejected'] is True:
                    parity[label+'_stale_rejected'] += 1
            tuning.write(json.dumps(dict(item=item,pool=base['pool'],ranking=base['ranking'],
                neural_scores={'B':b['scores'],'C':c['scores']},observed_top={k:record[k] for k in 'ABC'},
                errors={k:record[k+'_error'] for k in 'BC'}),ensure_ascii=False)+'\n')
            if item['diagnostic']:
                diagnostics.append(record)
                continue
            total += 1
            target, source = item['text'],item['source']
            correct = {k:record[k] == target for k in 'ABC'}
            for group in (source,'all'):
                stats = summary[group]; stats['n'] += 1
                stats['top5'] += any(x['text'] == target for x in base['pool'][:5])
                for k in 'ABC': stats['correct'][k] += correct[k]
                for k in 'BC':
                    stats['errors'][k] += bool(record[k+'_error'])
                    stats['timeouts'][k] += 'TimeoutException' in (record[k+'_error'] or '')
            for ref in 'AB':
                if not record['C_error'] and (ref == 'A' or not record['B_error']):
                    paired[ref][source][(correct[ref],correct['C'])] += 1
                if record['C'] != record[ref] or record['C_error'] or (ref == 'B' and record['B_error']):
                    streams[ref].write(json.dumps(record,ensure_ascii=False)+'\n')
                    writers[ref].writerow([item[k] for k in ('id','source','code','text')]+[record[k] for k in ('A','B','C','B_error','C_error')])
    finally:
        for f in [*streams.values(),*csv_streams.values(),tuning]: f.close()
    assert total == 73129 and len(diagnostics) == 2 and parity['exact_quick_cases'] == 3002
    comparisons = {}
    for ref in 'AB':
        rng = np.random.default_rng(20261004)
        samples = np.zeros(50000)
        rescue = regress = n = 0
        by_source = {}
        for source,counts in paired[ref].items():
            size = sum(counts.values()); pos,neg = counts[False,True],counts[True,False]
            assert size > 0
            draws = rng.multinomial(size,[pos/size,neg/size,1-(pos+neg)/size],size=50000)
            samples += draws[:,0]-draws[:,1]
            rescue += pos; regress += neg; n += size
            by_source[source] = dict(n=size,rescue=pos,regress=neg,net=pos-neg)
        comparisons['C_vs_'+ref] = dict(n=n,excluded_failures=total-n,rescue=rescue,regress=regress,net=rescue-regress,
            delta_pp=(rescue-regress)*100/n,paired_bootstrap_95_pp=(np.quantile(samples,[.025,.975])*100/n).tolist(),by_source=by_source)
    performance = {}
    for label in 'BC':
        compat = json.loads((a.work/f'compatibility-{label}.json').read_text()); assert compat['pass']
        lifecycle = [json.loads((a.work/f'{mode}-{name}-lifecycle.json').read_text()) for mode,name in [('score',label),('perf',label+'1'),('perf',label+'2')]]
        assert all(r['normal_exit'] and r['exit_code'] == 0 for r in lifecycle)
        data = list(rows(a.work/f'perf-{label}1.jsonl')) + list(rows(a.work/f'perf-{label}2.jsonl'))
        assert len(data) == 200
        good = [r['elapsed_ms'] for r in data if not r['error']]
        performance[label] = dict(n=200,errors=sum(bool(r['error']) for r in data),
            p50_ms=float(np.quantile(good,.5)),p95_ms=float(np.quantile(good,.95)),max_ms=max(good),
            private_peak_bytes=max(r['private_bytes'] for r in data),working_set_peak_bytes=max(r['working_set'] for r in data),
            startup_ms=[r['startup_ms'] for r in lifecycle])
    result = dict(accuracy=summary,comparisons=comparisons,performance=performance,diagnostics=diagnostics,parity=dict(parity),failures=failures,
        independent_generalization=False,production_changed=False)
    save(a.work/'summary.json',result)
    lines = ['# Qwen Q4 全量固定权重对照','',
        '当前生产 C# 五阶解码器、与快速测试相同的日用配置快照；学习和提前上屏关闭，无上下文，无调参。完整保留历史 73,129 行，不去重、不抽样；两组诊断不计入准确率。','',
        '| 来源 | N | A 关闭神经重排 | B 当前 Q8 | C 新 Q4 | 基础 Top-5 入池 |','|---|---:|---:|---:|---:|---:|']
    for source,r in summary.items():
        lines.append(f"| {source} | {r['n']} | "+' | '.join(f"{r['correct'][k]} ({r['correct'][k]/r['n']:.4%})" for k in 'ABC')+f" | {r['top5']} ({r['top5']/r['n']:.4%}) |")
    lines += ['','模型错误/超时：'+json.dumps({s:dict(errors=r['errors'],timeouts=r['timeouts']) for s,r in summary.items()},ensure_ascii=False),'']
    for name,r in comparisons.items():
        lines.append(f"- {name}：救回 {r['rescue']}，改错 {r['regress']}，净 {r['net']:+d}；差值 {r['delta_pp']:+.4f} 个百分点，配对 bootstrap 95% 区间 [{r['paired_bootstrap_95_pp'][0]:+.4f}, {r['paired_bootstrap_95_pp'][1]:+.4f}]。")
    ci = comparisons['C_vs_B']['paired_bootstrap_95_pp']
    interpretation = '在本历史集合上，固定权重的新 Q4 准确率低于当前 Q8。' if ci[1] < 0 else ('在本历史集合上，固定权重的新 Q4 准确率高于当前 Q8。' if ci[0] > 0 else '尚不足以判断 Q4 与 Q8 的准确率差异，不能宣称等效。')
    lines += ['',interpretation,'',
        '区间按来源分层、逐行配对重采样 50,000 次，种子 20261004。历史数据有训练重合、重复目标和反复实验限制，行级区间未校正这些相关性；不是独立泛化证据。失败不当作成功回退，仍计入总分母；配对区间排除失败并记录数量。','',
        '| 性能 | Q8 | Q4 |','|---|---:|---:|']
    for key in ('p50_ms','p95_ms','max_ms','errors','private_peak_bytes','working_set_peak_bytes','startup_ms'):
        lines.append(f"| {key} | {performance['B'][key]} | {performance['C'][key]} |")
    lines += ['','性能沿用快速实验的固定 100 个输入，短/中/长 20/40/40，每轮预热 10 次，顺序 B→C→C→B。每模型启动三次，文件缓存可能影响启动时间；内存是请求结束采样最大值。隔离测试不替代真实打字。','',
        '固定融合权重由生产函数应用。若收益不足，分数尺度或融合权重失配仍可能存在；本轮不能分离模型、tokenizer、特殊符号与量化的影响，没有调参。','',
        '## 诊断（不计入准确率）','']
    for r in diagnostics:
        lines += ['',f"### {r['item']['code']}",'','| 候选 | 基础分 | Q8 神经分 | Q8 融合分/排名 | Q4 神经分 | Q4 融合分/排名 |','|---|---:|---:|---:|---:|---:|']
        for i,candidate in enumerate(r['pool'][:5]):
            values = []
            for label in 'BC':
                final = r[label+'_final']; rank = next(j for j,x in enumerate(final) if x['text'] == candidate['text'])
                values += [f"{r[label+'_scores'][i]:.6f}",f"{final[rank]['fused']:.6f} / {rank+1}"]
            lines.append(f"| {candidate['text']} | {candidate['score']:.6f} | "+' | '.join(values)+' |')
    lines += ['','## 复现与核验','',
        'plan.json 保存全量源文件和模型指纹；cases.json 保留全部输入；freeze-A.jsonl 是共同候选池。score-B/C.jsonl 保存全部分数、融合结果、错误和内存。changes-C-vs-A/B.csv/jsonl 保存全部首选变化。',
        'tuning-cache.jsonl 合并全部候选、基础分、前五神经分、原始顺序、字数、权重和比较器选择，可离线重算融合；独立 Python 回放逐条核对生产排序与融合分。缓存不含第五项之后的神经分，扩大重排范围需补算。原始文件、模型、配置及构建一并保留。',
        'commands.json 保存实际命令；每阶段 manifest、complete、compatibility、lifecycle 是身份和完整性证据。原快速实验 3000 条及两组诊断的候选池、神经分、融合结果均精确复现。',
        '服务程序与生产程序集保持快速实验的字节身份；只改离线工具的进度、断点续跑与校验，不修改生产设置或 IPC。没有部署，没有写入日用学习日志，没有恢复其他暂停任务。']
    (a.work/'REPORT.md').write_text('\n'.join(lines)+'\n')
    save(a.work/'verification.json',dict(complete=True,rows=total,parity=dict(parity),scoring_errors=len(failures),
        all_lifecycles_normal=True,production_changed=False))
    print(json.dumps(dict(accuracy=summary,comparisons=comparisons,performance=performance),ensure_ascii=False),flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('action',choices=['prepare','run','summarize'])
    p.add_argument('--repo',type=Path,required=True)
    p.add_argument('--work',type=Path,required=True)
    p.add_argument('--quick',type=Path,default=Path('/mnt/c/Archive/qwen-q4-quick-20261004'))
    p.add_argument('--dotnet',default='/mnt/c/Program Files/dotnet/dotnet.exe')
    a = p.parse_args()
    a.repo, a.work, a.quick = a.repo.resolve(), a.work.resolve(), a.quick.resolve()
    {'prepare':prepare,'run':run,'summarize':summarize}[a.action](a)


if __name__ == '__main__':
    main()
