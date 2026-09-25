"""Validate and archive the fixed original-model mixture -> budget-pruning sweep."""
import argparse
import hashlib
import json
from pathlib import Path
from build_articles_model import verified_copy


def read(path):return json.loads(path.read_text())
def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def copy_tree(source,destination):
    destination.mkdir(parents=True,exist_ok=True)
    for p in source.rglob('*'):
        if p.is_file():
            target=destination/p.relative_to(source)
            target.parent.mkdir(parents=True,exist_ok=True)
            verified_copy(p,target)
def rows(path,old=False):
    result=[json.loads(x) for x in path.read_text().splitlines()]
    if old:result=[x for x in result if x['source']=='old']
    assert len({x['id'] for x in result})==len(result)
    return {x['id']:x for x in result}
def stats(r):return dict(n=len(r),correct=sum(x['prediction']==x['target'] for x in r.values()),top5=sum(0<x['rank']<=5 for x in r.values()))
def compare(current,baseline,path):
    assert current.keys()==baseline.keys()
    changed=[]
    for key,x in current.items():
        b=baseline[key]
        assert all(x[k]==b[k] for k in ('id','source','code','target'))
        if x['prediction']!=b['prediction']:
            changed.append(dict(**x,baseline_prediction=b['prediction'],rescued=x['prediction']==x['target'],regressed=b['prediction']==b['target']))
    path.write_text(''.join(json.dumps(x,ensure_ascii=False)+'\n' for x in changed))
    result=dict(changed=len(changed),rescued=sum(x['rescued'] for x in changed),regressed=sum(x['regressed'] for x in changed))
    result['net']=result['rescued']-result['regressed']
    assert result['net']==stats(current)['correct']-stats(baseline)['correct']
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('work',type=Path);parser.add_argument('archive',type=Path);args=parser.parse_args()
    w,a=args.work,args.archive;h=Path(__file__).resolve().parent
    assert (w/'complete.json').exists()
    plan=read(w/'plan.json');summary={};validation={};selected={};used_native_hashes=set()
    stable_files = {}
    for prior in w.glob('*-identity-before-fast4.json'):
        current = w/prior.name.replace('-before-fast4', '')
        if not current.exists(): continue
        old_files, new_files = read(prior)['files'], read(current)['files']
        common = old_files.keys() & new_files.keys()
        for name in common: assert old_files[name] == new_files[name], ('accelerator changed existing model', name)
        stable_files[current.name] = len(common)
    (w/'accelerator-input-stability.json').write_text(json.dumps(stable_files, indent=2))
    oldmix=Path('/home/yc/tmp/corpus4-pruned-articles-mixture-20260924')
    fullmix=Path('/home/yc/tmp/corpus4-articles-mixture-20260924')
    corpus=Path('/home/yc/tmp/wsmerge-count-matched-20260924')
    full=Path('/home/yc/tmp/wsmerge-accuracy-20260924')
    articles=Path('/home/yc/tmp/articles-corpus4-complement-20260924')
    for alpha in plan['weights']:
        name=f'alpha-{alpha:.2f}';out=w/name;summary[name]={};validation[name]={}
        selected[name]=read(out/'selected.json');assert selected[name]['counts']==[19070,*plan['target'][1:]]
        assert sha(out/'model-q8.bin')==selected[name]['sha256']
        for d in ('old10k','articles','thucnews'):
            legacy='20k' if d=='old10k' else d
            models={s:rows(out/'eval'/s/d/'predictions.jsonl') for s in ('dynamic','full','pruned','q8')}
            models.update(mainline=rows(full/'baselines'/f'{legacy}-mainline_q8.jsonl',d=='old10k'),
                corpus4_full=rows(full/legacy/'predictions.jsonl',d=='old10k'),
                corpus4_pruned=rows(corpus/legacy/'predictions.jsonl',d=='old10k'),
                articles=rows(articles/d/'predictions.jsonl'),
                prune_then_mix=rows(oldmix/name/d/'predictions.jsonl'),
                previous_full_q8_mix=rows(fullmix/name/d/'predictions.jsonl'))
            reference=read(oldmix/name/d/'manifest.json')
            for stage in ('dynamic','full','pruned','q8'):
                m=read(out/'eval'/stage/d/'manifest.json')
                if 'native_sha256' in m: used_native_hashes.add(m['native_sha256'])
                for key in ('cases_sha256','decoder_sha256','exporter_sha256','fixture_hashes'):assert m[key]==reference[key],(name,d,stage,key)
                assert '666' in (out/'eval'/stage/d/'validation.log').read_text()
            result={s:stats(r) for s,r in models.items()};result['comparisons']={}
            for before,after in [('dynamic','full'),('full','pruned'),('pruned','q8'),('dynamic','q8'),('full','q8'),*[(s,'q8') for s in ('mainline','corpus4_full','corpus4_pruned','articles','prune_then_mix','previous_full_q8_mix')]]:
                key=f'{after}_vs_{before}'
                result['comparisons'][key]=compare(models[after],models[before],out/f'{d}-{key}.jsonl')
            result['by_source']={}
            for source in sorted({x['source'] for x in models['q8'].values()}):
                result['by_source'][source]={s:stats({k:x for k,x in rr.items() if x['source']==source}) for s,rr in models.items()}
            summary[name][d]=result
            validation[name][d]=dict(inputs_and_fixture_identical=True,stages=4,lifecycle_cases_each=666,n=len(models['q8']))
    totals={}
    for name,sets in summary.items():
        totals[name]={key:{field:sum(s['comparisons'][key][field] for s in sets.values()) for field in ('rescued','regressed','net')} for key in next(iter(sets.values()))['comparisons']}
        assert sum(s['q8']['n'] for s in sets.values())==73129
    result=dict(weights=summary,totals=totals,selected=selected)
    (w/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
    (w/'comparison-validation.json').write_text(json.dumps(validation,indent=2))
    text=['# Corpus4 原始模型与 Articles 融合后按主线记录预算剪枝','',
        '固定 Articles 权重 10%、25%，使用原始 ARPA 浮点概率，先融合再按记录预算剪枝。冻结 Lua，旧集10k口径；本轮为历史集探索，不选定正式权重。','',
        '|模型|体积 MB|旧集 10,000|Articles 33,129|THUCNews 30,000|','|---|---:|---:|---:|---:|']
    first=next(iter(summary.values()))
    def accuracy(sets,stage):return '|'.join(f"{100*sets[d][stage]['correct']/sets[d][stage]['n']:.3f}% ({sets[d][stage]['correct']:,})" for d in ('old10k','articles','thucnews'))
    for label,stage,size in [('当前主线','mainline',356.492204),('Corpus4 完整 Q8','corpus4_full',7770.486806),('Corpus4 记录预算剪枝版','corpus4_pruned',413.515511),('Articles 单模型','articles',2503.378280)]:
        text.append(f'|{label}|{size:.2f}|'+accuracy(first,stage)+'|')
    for name,sets in summary.items():
        text.append(f'|{name} 先剪枝后双模型融合|2916.89|'+accuracy(sets,'prune_then_mix')+'|')
        text.append(f"|{name} 先融合后剪枝 Q8|{selected[name]['bytes']/1e6:.2f}|"+accuracy(sets,'q8')+'|')
    text+=['','## 每阶段准确率','','|权重／阶段|旧集 10,000|Articles 33,129|THUCNews 30,000|','|---|---:|---:|---:|']
    for name,sets in summary.items():
        for stage in ('dynamic','full','pruned','q8'):text.append(f'|{name} / {stage}|'+accuracy(sets,stage)+'|')
    text+=['','dynamic：联合词表归一化后的动态概率融合；full：联合支持集物化后的完整回退模型；pruned：预算剪枝并重算回退的浮点模型；q8：最终单文件 TCSKNM03。','',
        '## 救回与退步（73,129 条）','','|权重|变化|救回|退步|正确净增|','|---|---|---:|---:|---:|']
    for name,comparisons in totals.items():
        for key,s in comparisons.items():text.append(f"|{name}|{key}|{s['rescued']}|{s['regressed']}|{s['net']:+d}|")
    text+=['','阶段净变化不能代替逐条分析：同一步骤可能同时救回和损失不同句子，上表保留两者。','']
    for name,comparisons in totals.items():
        text.append(f"{name}：动态融合 → 完整物化 {comparisons['full_vs_dynamic']['net']:+d} 条；完整物化 → 剪枝 {comparisons['pruned_vs_full']['net']:+d} 条；剪枝 → Q8 {comparisons['q8_vs_pruned']['net']:+d} 条。")
    text+=['','## 记录量','','|模型|一阶|二阶|三阶|四阶|五阶|','|---|---:|---:|---:|---:|---:|',
        '|当前主线|'+'|'.join(f'{x:,}' for x in plan['target'])+'|']
    for name,s in selected.items():text.append('|'+name+'|'+'|'.join(f'{x:,}' for x in s['counts'])+'|')
    text+=['','二至五阶准确匹配主线预算。一阶保留联合词表全部19,070项，不人为凑足21,230项。体积不设上限，记录数相同不意味着上下文块数、索引或文件大小相同。','',
        '## 方法与限制','',
        '原始 Corpus4 是 wsmerge 完整 ARPA，但训练时已有 --prune 0 0 1 1 1；不是未剪枝训练计数。Articles 使用原始未量化 ARPA。',
        '每个源模型的 UNK 质量均分给其未见的联合词表项和 UNK，未见历史仍映射到该源 UNK。此前双 Q8 插值使用不同的未知字符约定与量化来源，因此路线之间的全部差异不能只归因于剪枝顺序。',
        '联合支持集显式概率为线性插值；集合外以重新计算的归一化回退近似。动态到物化的差异单列，并有独立评分和质量检查。[SRILM 说明](https://mailman.speech.sri.com/pipermail/srilm-user/2009q3/000770.html)',
        '以删除单条记录的条件 KL 乘模型历史概率排序，自高阶向低阶按预算保留并保护必要前缀；同分按键顺序确定。重新计算回退权重后再量化。单条成本不等于多条同时删除的实际损失，不声称全局最优。[相对熵剪枝论文](https://www.sri.com/wp-content/uploads/2021/12/entropy-based_pruning_of_backoff_language_models.pdf)',
        '小模型独立全词表 KL、概率／归一化、预算、零预算／全保留和多线程／外排序一致性通过；大模型独立 mmap oracle、量化误差和各阶段解码生命周期检查通过。',
        '离线物化使用未量化四／五阶 Trie 加速各自最高阶的精确命中；未命中保留原始回退语义和双精度加法顺序。四阶索引确认真实上下文存在后，可从五阶索引读取单个原始回退权重；不采用库内累计评分或合成记录，评分模式无需再映射原始四、五阶记录文件。每个源模型9,330个穷举评分零差异，两档权重物化及断点续跑逐字节一致。构建阶段使用已验证等价的四阶加速路径，评分阶段使用四／五阶加速路径，以避免全量构建的随机读取工作集膨胀。该加速索引不进入最终模型或生产运行时。',
        '本机内核实际启用了查询库请求的文件大页，放大了随机读取。离线索引映射局部改用普通页和随机读取提示；Corpus4、Articles、完整10%模型各1,100个实际评分输出逐字节一致，内核映射也确认无文件大页。未修改全局内存设置或共享查询库。',
        '所有阶段使用相同冻结 Lua、词表、Beam、孤立字先验，关闭学习、Qwen、提前上屏。现有历史集反复使用且训练重合未全面审计，本轮不作为独立权重确认或真实输入性能验收。未部署。','',
        f'归档：`{a}`。最终 Q8 单文件复制至各权重子目录；大体积中间索引与 ARPA 通过工作目录路径、大小、哈希引用，不重复复制。','']
    report='\n'.join(text);(w/'REPORT.md').write_text(report)
    a.mkdir(exist_ok=True)
    for name in summary:
        src=w/name;dest=a/name;dest.mkdir(exist_ok=True)
        verified_copy(src/'model-q8.bin',dest/'sentence-fivegram-mobile.bin')
        assert sha(dest/'sentence-fivegram-mobile.bin')==selected[name]['sha256']
        copy_tree(src/'eval',dest/'eval')
        copy_tree(src/'q8-validation',dest/'q8-validation')
        refs={str(p):dict(bytes=p.stat().st_size,sha256=sha(p)) for p in (src/'model.arpa',src/'model-q16.bin')}
        (dest/'intermediate-references.json').write_text(json.dumps(refs,indent=2))
        for p in src.iterdir():
            if p.is_file() and p.suffix in ('.json','.jsonl','.log','.txt'):verified_copy(p,dest/p.name)
    for p in w.iterdir():
        if p.is_file() and p.suffix in ('.json','.md','.log','.py','.txt'):verified_copy(p,a/p.name)
    tools=a/'tools';tools.mkdir(exist_ok=True)
    for name in ('mixture_budget.hpp','mixture_budget.cpp','mixture_pack.cpp','shape5_budget_lua.cpp','audit_mixture_budget.py','test_mixture_budget.py','test_fast4_budget.py','audit_accelerated_sources.py','test_index_mapping_advice.py','report_merge_budget.py','evaluate_external_shape.py','build_articles_model.py','train_articles.py','prepare_thucnews_shape.py'):
        verified_copy(h/name,tools/name)
    build=read(w/'build.json');snapshots={}
    binaries=tools/'bin';binaries.mkdir(exist_ok=True)
    for name,digest in {**build['binary_hashes'],**build.get('reference_binary_hashes',{})}.items():
        source=w/name;assert sha(source)==digest,('binary identity changed',name)
        verified_copy(source,binaries/name)
        snapshots[name]=dict(sha256=digest,bytes=source.stat().st_size)
    assert used_native_hashes <= {item['sha256'] for item in snapshots.values()},'missing scorer binary used by an evaluation'
    (tools/'binary-snapshots.json').write_text(json.dumps(snapshots,indent=2))
    (h/'CORPUS4_ARTICLES_MERGE_BUDGET.md').write_text(report)
    hashes={str(p.relative_to(a)):sha(p) for p in a.rglob('*') if p.is_file() and p.name!='SHA256.json'}
    (a/'SHA256.json').write_text(json.dumps(hashes,indent=2))
    for name,digest in hashes.items():assert sha(a/name)==digest
    print(report)
    print('ARCHIVE VERIFIED',len(hashes))


if __name__=='__main__':main()
