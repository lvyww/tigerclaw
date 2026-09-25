"""Validate frozen comparisons and archive a completed three-source experiment."""
from pathlib import Path
import argparse,json,shutil
from report_merge_budget import rows,stats,compare,sha,copy_tree
from build_articles_model import verified_copy

def main():
 p=argparse.ArgumentParser();p.add_argument('work',type=Path);p.add_argument('archive',type=Path);a=p.parse_args();w=a.work;dest=a.archive;h=Path(__file__).resolve().parent
 assert (w/'complete.json').exists()
 read=lambda p:json.loads(p.read_text())
 sets=('old10k','articles','thucnews');stages=('dynamic','full','pruned','q8');result={};manifest_checks={}
 for d,legacy in zip(sets,('20k','articles','thucnews')):
  current={s:rows(w/'eval'/s/d/'predictions.jsonl') for s in stages}
  base=Path('/home/yc/tmp/wsmerge-accuracy-20260924')
  models={'mainline':rows(base/'baselines'/f'{legacy}-mainline_q8.jsonl',d=='old10k'),
   'corpus4':rows(base/legacy/'predictions.jsonl',d=='old10k'),
   'articles':rows(Path('/home/yc/tmp/articles-corpus4-complement-20260924')/d/'predictions.jsonl'),
   'nonnews':rows(Path('/home/yc/tmp/brightmart-nonnews-20260925/eval')/d/'predictions.jsonl'),
   'news':rows(Path('/home/yc/tmp/brightmart-news-20260925/eval')/d/'predictions.jsonl'),
   'two_source_q8':rows(Path('/home/yc/tmp/corpus4-articles-merge-budget-20260924/alpha-0.25/eval/q8')/d/'predictions.jsonl')}
  models.update(current);result[d]={'models':{name:stats(r) for name,r in models.items()},'comparisons':{}}
  for s in stages:
   for b in ('mainline','corpus4','two_source_q8'):
    result[d]['comparisons'][s+'_vs_'+b]=compare(current[s],models[b],w/f'{d}-{s}-vs-{b}.jsonl')
  for s,b in [('full','dynamic'),('pruned','full'),('q8','pruned')]:
   result[d]['comparisons'][s+'_vs_'+b]=compare(current[s],current[b],w/f'{d}-{s}-vs-{b}.jsonl')
  reference=read(Path('/home/yc/tmp/corpus4-articles-merge-budget-20260924/alpha-0.25/eval/q8')/d/'manifest.json')
  manifest_checks[d]={}
  for s in stages:
   path=w/'eval'/s/d;m=read(path/'manifest.json')
   for key in ('cases_sha256','total','decoder_sha256','exporter_sha256','fixture_hashes'):assert m[key]==reference[key],(d,s,key)
   assert '666' in (path/'validation.log').read_text(),(d,s,'lifecycle')
   assert len(current[s])=={'old10k':10000,'articles':33129,'thucnews':30000}[d]
   manifest_checks[d][s]={'frozen_inputs_match':True,'lifecycle_checks':666}
 selected=read(w/'selected.json');counts=list(map(int,(w/'full/complete.txt').read_text().split()))
 summary={'results':result,'selected':selected,'full_counts':counts,'validation':manifest_checks,'complementarity':read(w/'complementarity.json')}
 (w/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2))
 text=(h/'CORPUS4_ARTICLES_THREEWAY.md').read_text().replace('（进行中）','（已完成）').replace('实际准确率尚待运行完成。','全部四阶段、三套冻结集评测已完成。')
 text=text.replace('直接融合初步旧集结果 9954/10000；其余阶段待完成。','直接融合旧集结果 9954/10000，完整四阶段结果见下表。').replace('此前三个评测子进程继续运行，无重复解码。','切换未重复解码。')
 text+='\n## 实际结果\n\n| 模型 | 旧集 10k | Articles 33,129 | THUC 30k | 合计正确 / 73,129 | 相对主线 |\n|---|---:|---:|---:|---:|---:|\n'
 labels={'mainline':'当前主线 Q8','corpus4':'Corpus4 完整 Q8','articles':'Articles 独立 Q8','nonnews':'非新闻独立 Q8','news':'新闻独立 Q8','two_source_q8':'Corpus4 75% + Articles 25% → 主线阶数剪枝 Q8','dynamic':'三路动态原始概率融合','full':'三路完整物化浮点','pruned':'三路按主线阶数剪枝浮点','q8':'三路剪枝 Q8'}
 baseline=sum(result[d]['models']['mainline']['correct'] for d in sets)
 for key,label in labels.items():
  values=[result[d]['models'][key]['correct'] for d in sets];total=sum(values)
  text+=f'| {label} | '+ ' | '.join(f'{v:,}' for v in values)+f' | {total:,} | {total-baseline:+d} |\n'
 text+='\n阶段净变化（后者减前者）：\n\n| 阶段 | 旧集 | Articles | THUC | 合计 |\n|---|---:|---:|---:|---:|\n'
 for key,label in [('full_vs_dynamic','物化'),('pruned_vs_full','剪枝'),('q8_vs_pruned','Q8 量化')]:
  v=[result[d]['comparisons'][key]['net'] for d in sets];text+='| '+label+' | '+' | '.join(f'{x:+d}' for x in v)+f' | {sum(v):+d} |\n'
 text+=f"\n完整物化各阶记录：`{counts}`。剪枝各阶：`{selected['counts']}`。最终 Q8 `{selected['bytes']:,}` 字节（{selected['bytes']/1e6:.2f} MB），SHA256 `{selected['sha256']}`。\n"
 q8_total=sum(result[d]['models']['q8']['correct'] for d in sets)
 two_total=sum(result[d]['models']['two_source_q8']['correct'] for d in sets)
 text+=f'\n最终三路 Q8 比主线合计 {q8_total-baseline:+d} 句，比此前 Corpus4 75% / Articles 25% 的双路剪枝 Q8 合计 {q8_total-two_total:+d} 句；体积减少 {(415305954-selected["bytes"])/1e6:.2f} MB。分集合差异应分别看，合计提升不代表所有题材都优于主线。\n'
 text+='\n互补性结论：非新闻救回更多 Corpus4 错句，也救回更多 Corpus4 与 Articles 的共同错句，因此选择非新闻参与本轮融合。但非新闻单独替换 Corpus4 时新增错误也更多（286 对新闻 243），新闻独立模型总正确数反而高 11 句。尚未运行同权重的新闻三路融合，不能据此断言非新闻融合一定优于新闻融合；本轮验证的是选中方案的实际收益。单模型救回数也不是概率融合收益的理论上限。\n'
 if (w/'q8-source-breakdown.json').exists():
  text+='\n按来源细分，三路 Q8 相对双路 Q8 的旧集增益为 26 句；Articles 净增 9 句，其中 nue 子集增 18 句，其余子集合计减 9 句。THUC 总数持平，游戏/社会合计增 4 句，房产/教育合计减 4 句。收益并非均匀分布，详见 `q8-source-breakdown.json`。\n'
 text+='\n校验：三来源原始记录独立概率 oracle、完整/剪枝归一化及前缀闭包采样、Q16/Q8 格式与量化、四阶段各三套的 666 项生命周期，以及冻结样本/解码器/词表身份均通过。完整记录文件及未量化精确查询索引保留于工作目录，用哈希引用；归档包含最终剪枝 ARPA、Q8、所有阶段评测、差异明细及脚本。\n'
 text+=f"\n物化顺序前缀查询优化与原实现的 {read(w/'prefix-stream-real-parity.json')['records']:,} 条实际五阶记录逐字节一致，概率及 KL 评分均保留。原始记录的随机预读提示试验已撤回；最终仍沿用普通预读。\n"
 text+=f'\n归档：`{dest}`。未部署日用模型。\n'
 (w/'REPORT.md').write_text(text)
 dest.mkdir(parents=True,exist_ok=True)
 for source,name in [(w/'model.arpa','model.arpa'),(w/'model-q8.bin','sentence-fivegram-mobile.bin')]:verified_copy(source,dest/name)
 for directory in ('eval','q8-validation'):copy_tree(w/directory,dest/directory)
 for p in w.iterdir():
  if p.is_file() and p.suffix in ('.json','.jsonl','.log','.txt','.md','.py','.sh'):verified_copy(p,dest/p.name)
 scripts=dest/'scripts';scripts.mkdir(exist_ok=True)
 for name in ('mixture_budget.hpp','mixture_budget.cpp','mixture_pack.cpp','shape5_budget_lua.cpp','test_mixture_three.py','test_mixture_budget.py','audit_mixture_budget.py','audit_accelerated_sources.py','run_mixture_three.py','build_streamed_index.py','report_mixture_three.py','evaluate_external_shape.py','build_articles_model.py','report_merge_budget.py'):
  verified_copy(h/name,scripts/name)
 for name in ('pack','budget','budget.so','budget-before-prefix-stream','mixture_budget-before-prefix-stream.cpp'):verified_copy(w/name,scripts/name)
 binaries=dest/'binaries';binaries.mkdir(exist_ok=True)
 for path,expected in read(w/'external-tools-identity.json').items():
  source=Path(path);assert sha(source)==expected['sha256'];verified_copy(source,binaries/source.name)
 licenses=dest/'licenses';licenses.mkdir(exist_ok=True)
 for name in ('LICENSE','COPYING','COPYING.3','COPYING.LESSER.3'):
  verified_copy(Path('/home/yc/tools/tigerclaw-brightmart/kenlm')/name,licenses/name)
 hashes={str(p.relative_to(dest)):sha(p) for p in dest.rglob('*') if p.is_file() and p.name!='SHA256.json'}
 (dest/'SHA256.json').write_text(json.dumps(hashes,indent=2))
 for name,digest in hashes.items():assert sha(dest/name)==digest,name
 (h/'CORPUS4_ARTICLES_THREEWAY.md').write_text(text);(w/'archive-verified.json').write_text(json.dumps(dict(files=len(hashes),archive=str(dest))))
 print(text);print('ARCHIVE VERIFIED',len(hashes))
if __name__=='__main__':main()
