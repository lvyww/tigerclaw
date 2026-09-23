"""Produce paired Q16/Q8 results, preserving every case and disagreement."""
import argparse,csv,hashlib,json
from pathlib import Path

def read(path):return {r['id']:r for r in map(json.loads,path.read_text().splitlines())}
def main():
    p=argparse.ArgumentParser();p.add_argument('work',type=Path);a=p.parse_args();w=a.work
    archive=Path('/mnt/c/Archive/char5-corpus4_0-20260922')
    sets=[('历史20k（冻结解码器）',archive/'rime-joint-compression/control-mainline-tcs3',w/'eval-20k'),('THUCNews',archive/'thucnews-comparison-20260923/rime-mainline',w/'eval-thucnews'),('articles',archive/'articles-comparison-20260923/rime-mainline',w/'eval-articles'),('历史20k（当前Rime解码器）',w/'current-q16-eval',w/'current-q8-eval')]
    summaries=[];allrows=[];case_checks=[]
    for label,old,new in sets:
        om=json.loads((old/'manifest.json').read_text());nm=json.loads((new/'manifest.json').read_text())
        assert om['decoder_sha256']==nm['decoder_sha256']
        if om['cases_sha256']!=nm['cases_sha256']:
            # The legacy scratch file is in shard-concatenation order; the
            # archived baseline uses numeric ID order. Validate complete rows.
            assert label=='历史20k（冻结解码器）'
            original=(archive/'evaluation-shape-20k-20260923/cases.tsv').read_bytes()
            actual=Path('/home/yc/tmp/tigirl-tcs3-cases.tsv').read_bytes()
            assert hashlib.sha256(original).hexdigest()==om['cases_sha256']
            assert hashlib.sha256(actual).hexdigest()==nm['cases_sha256']
            assert sorted(original.splitlines())==sorted(actual.splitlines())
            case_checks.append(dict(dataset=label,ordering_only=True,baseline_sha256=om['cases_sha256'],run_sha256=nm['cases_sha256'],sorted_rows_sha256=hashlib.sha256(b'\n'.join(sorted(actual.splitlines()))).hexdigest()))
        left=read(old/'predictions.jsonl');right=read(new/'predictions.jsonl');assert left.keys()==right.keys()
        rows=[]
        for ident,x in left.items():
            y=right[ident];assert all(x[k]==y[k]for k in ('source','code','target'))
            lp=x.get('prediction',x.get('predictions',{}).get('mainline_tcs3_frozen'))
            lr=x.get('rank',x.get('target_ranks',{}).get('mainline_tcs3_frozen'))
            assert lp is not None and lr is not None
            row=dict(dataset=label,id=ident,source=x['source'],code=x['code'],target=x['target'],q16=lp,q8=y['prediction'],q16_rank=lr,q8_rank=y['rank'])
            row['outcome']='improved' if lp!=x['target'] and y['prediction']==x['target'] else 'regressed' if lp==x['target'] and y['prediction']!=x['target'] else 'both_correct' if lp==x['target'] else 'both_wrong'
            rows.append(row)
        for source in ['combined']+sorted({r['source']for r in rows}):
            rs=[r for r in rows if source=='combined' or r['source']==source]
            summaries.append(dict(dataset=label,source=source,n=len(rs),q16=sum(r['q16']==r['target']for r in rs),q8=sum(r['q8']==r['target']for r in rs),improved=sum(r['outcome']=='improved'for r in rs),regressed=sum(r['outcome']=='regressed'for r in rs),changed=sum(r['q16']!=r['q8']for r in rs),q16_top5=sum(0<r['q16_rank']<=5 for r in rs),q8_top5=sum(0<r['q8_rank']<=5 for r in rs),q16_top20=sum(0<r['q16_rank']<=20 for r in rs),q8_top20=sum(0<r['q8_rank']<=20 for r in rs)))
        allrows+=rows
    for name,rs in [('ALL.csv',allrows),('DIFFERENCES.csv',[r for r in allrows if r['q16']!=r['q8']])]:
        with (w/name).open('w',encoding='utf-8-sig',newline='')as f:
            writer=csv.DictWriter(f,fieldnames=list(allrows[0]));writer.writeheader();writer.writerows(rs)
    (w/'comparison.json').write_text(json.dumps(summaries,ensure_ascii=False,indent=2))
    (w/'case-identity-checks.json').write_text(json.dumps(case_checks,ensure_ascii=False,indent=2))
    combined=[r for r in summaries if r['source']=='combined']
    sizes={p.name:p.stat().st_size for p in w.iterdir() if p.suffix in ('.zip','.7z')}
    text=['# Rime 五阶 Q8 量化实验（2026-09-24）','','## 体积','', '| 项目 | 字节 |','|---|---:|','| 原 Q16 模型 | 460693519 |','| Q8 模型 | 356492204 |','| 原完整 ZIP | 425566674 |','| 原 Q16 7z 试压（含额外工具） | 349085048 |']
    text += [f'| {k} | {v} |'for k,v in sizes.items()]
    text += ['','## 配对准确率','','| 测试集 | 条数 | Q16 首选 | Q8 首选 | 救回 | 退步 | 净增 | 首选变化 |','|---|---:|---:|---:|---:|---:|---:|---:|']
    text += [f"| {r['dataset']} | {r['n']} | {r['q16']} ({r['q16']/r['n']:.4%}) | {r['q8']} ({r['q8']/r['n']:.4%}) | {r['improved']} | {r['regressed']} | {r['q8']-r['q16']:+d} | {r['changed']} |"for r in combined]
    text += ['','## 方法与边界','','- 原模型 SHA256：4e6d79b957a55edf35cd9e2e66c62bd0bbe598581b7dc088b462122a713172a7。','- Q8 SHA256：5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361。','- 从已发布 Q16 参数再次线性量化；不是从原 ARPA 重新训练或重新量化，不等同于 KenLM Q8 的量化方法。','- 词表、各阶记录和上下文、后继字 ID 全保留。概率码 0..255；回退码 0 精确表示零，其余 1..255 表示非零权重范围。','- TCSKNM03 version 2：概率及回退字段 1 字节，token ID 和 successor count 仍为 2 字节，索引仍为原布局；header step 定点比例由 1e12 改成 1e9，min 比例仍为 1e7。旧读取器拒绝 version 2，新读取器兼容 version 1。','- 三组冻结测试沿用此前同一解码器、码表、Beam、三阶 observed-bigram 孤立字先验。当前 Rime 对照另行使用实际主线解码器、五阶自身的 observed-bigram 先验；两种环境结果分别列出，不混加。','- 历史20k在两种环境中重复评测；三个语料集间有少量文本重合，不宣称所有样本独立或与训练语料完全无重合。','- 量化规则事先固定，未根据测试命中率调整参数。关闭 LLM、学习与提前上屏。未安装、未发布、未改变主线默认模型。','- 独立 Python 参考评分：669 个采样上下文块、1341 个查询，Q16/Q8 × Lua/LuaJIT 四组评分一致；仅为采样校验。转换器核对全部区块、各阶计数和索引位置。','- Lua、LuaJIT 回归、原 Q16 fixture、Q8 真实模型增量/回删/锁定测试通过。压缩包 CRC 与清单哈希另见 packaging-verification.json。','- 未做实际 Rime 前端打字验收和严谨延迟对比。','','全部逐句数据见 ALL.csv，首选变化见 DIFFERENCES.csv，分类统计及 Top5/Top20 见 comparison.json。','实验包必须同时更新模型与 lua/tiger_sentence_fivegram.lua；旧版 Lua 不支持 Q8。']
    text += ['','历史20k本轮输入沿用临时文件的分片拼接顺序，旧归档按数字ID排序，因此原始文件 SHA256 不同；已逐行排序核对全部记录完全相同，并逐 ID 核对来源、编码、目标。每句重置解码缓存。证据见 case-identity-checks.json。']
    (w/'REPORT.md').write_text('\n'.join(text)+'\n');print(json.dumps(combined,ensure_ascii=False,indent=2))

if __name__=='__main__':main()
