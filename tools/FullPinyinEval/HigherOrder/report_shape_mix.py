"""Export every regression/improvement and the fixed mixture results and requested follow-up."""
import csv,json
from pathlib import Path
ROOT=Path('/mnt/c/Archive/char5-corpus4_0-20260922/probability-mixture-20260923')
BASE='old_420mb_fivegram'
LABELS={BASE:'当前主线五阶', 'corpus4_fivegram':'corpus4 完整五阶', 'mix_old90':'旧90%＋新10%', 'mix_old80':'旧80%＋新20%', 'mix_old70':'旧70%＋新30%'}
def main():
 variants=['mix_old90','mix_old80','mix_old70']
 if (ROOT/'mix_old60/manifest.json').exists():
  variants.append('mix_old60');LABELS['mix_old60']='旧60%＋新40%'
 rows=[json.loads(s) for s in (ROOT/variants[-1]/'predictions.jsonl').read_text().splitlines()]
 table=['| 模型 | 旧集正确 / 10000 | 新集正确 / 10000 | 合计正确 / 20000 | 合计准确率 |','|---|---:|---:|---:|---:|']
 for name,label in LABELS.items():
  old=sum(r['source']=='old' and r['predictions'][name]==r['target'] for r in rows)
  fresh=sum(r['source']=='fresh' and r['predictions'][name]==r['target'] for r in rows)
  table.append(f'| {label} | {old} | {fresh} | {old+fresh} | {(old+fresh)/200:.3f}% |')
 pair=['| 配比 | 旧集进步 / 退步 | 新集进步 / 退步 | 总净变化 |','|---|---:|---:|---:|']
 for name in variants:
  summary=json.loads((ROOT/name/'summary.json').read_text())
  a=summary['old']['comparisons'][BASE];b=summary['fresh']['comparisons'][BASE]
  pair.append(f'| {LABELS[name]} | {a["improved"]} / {a["regressed"]} | {b["improved"]} / {b["regressed"]} | {a["net"]+b["net"]:+d} |')
 with (ROOT/'COMPARISON_ALL_20000.csv').open('w',encoding='utf-8-sig',newline='') as f:
  w=csv.writer(f);w.writerow(['id','source','code','target']+list(LABELS.values()))
  for r in rows:w.writerow([r[k] for k in ['id','source','code','target']]+[r['predictions'][k] for k in LABELS])
 changes=['# 相对当前主线的全部进退步句子','']
 for name in variants:
  changes+=['## '+LABELS[name],'','| ID | 原句 | 主线输出 | 混合输出 | 变化 |','|---|---|---|---|---|']
  for r in rows:
   a=r['predictions'][BASE];b=r['predictions'][name];t=r['target']
   if (a==t)==(b==t):continue
   cells=[r['id'],t,a,b,'进步' if b==t else '退步']
   changes.append('| '+' | '.join(c.replace('|','\\|') for c in cells)+' |')
  changes.append('')
 (ROOT/'ALL_IMPROVEMENTS_REGRESSIONS.md').write_text('\n'.join(changes)+'\n')
 report='''# 旧主线与 corpus4 五阶概率混合：冻结两万句

先固定 90∶10、80∶20、70∶30 三档，再按用户要求追加 60∶40。混合概率直接参与每步 Beam 搜索（含 EOS），不是候选生成后重排，也不是 log 分数线性相加。

`log P = log(w * P_old + (1-w) * P_new)`

旧模型为当前主线 419,929,926 字节五阶；新模型为完整 corpus4 7,616,716,169 字节五阶。两模型合计 8,036.65 MB，属于离线验证，不是 500 MB 交付方案。

'''+ '\n'.join(table)+'\n\n相对当前主线，按目标文本精确一致计分：\n\n'+'\n'.join(pair)+'''

## 验证与限制

- 0、1、0.9、0.8、0.7 五档各通过 344 次独立逐 token 概率核对；两端点各 50 句全部候选和分数逐字节一致。
- 每档全量实验先通过 666 项增量、回删和锁定前缀检查。20,000 句保持原分母，包括无可解码输出的条目。
- 词表 ID 与状态按模型分别维护，BOS/EOS 单独处理。词表外字符由各自模型映射为 UNK；没有把两个模型的 token ID 混用，也没有重新建立统一 UNK 概率分配。
- 冻结解码器、Beam、原有排名先验保持一致；三阶只保留原隔离先验用途。关闭 LLM、学习、提前上屏，没有做延迟对比。
- 旧集为原 m5 held-out，新集为 mohu v5 held-out；“新集”不是 2026 年近期语料测试集。它们已多次用于排查，属于回归集，不能作为完全未见的泛化验证。
- 既往审计发现旧 Brightmart 五阶训练语料与这些测试句存在精确重叠；此处沿用固定基线，不把绝对准确率解释为无污染泛化准确率。
- 精确匹配不等于语义唯一正确；不同但合理的输出仍计不命中。
- 前三档预先固定；60∶40 是查看前三档结果后追加的探索实验，不能把它视作独立测试。没有重新训练、修改原模型或替换本机安装。

完整句子 CSV：`COMPARISON_ALL_20000.csv`。全部进退步：`ALL_IMPROVEMENTS_REGRESSIONS.md`。每档目录包含候选池、预测、摘要、模型哈希和验证日志。
'''
 diagnostics=['\n## 互补与关注句\n','| 配比 | 两个单模型都错、混合正确 | 两个单模型都对、混合错误 | 愧疚两句正确数 |','|---|---:|---:|---:|']
 for name in variants:
  gained=sum(r['predictions'][name]==r['target'] and all(r['predictions'][k]!=r['target'] for k in [BASE,'corpus4_fivegram']) for r in rows)
  lost=sum(r['predictions'][name]!=r['target'] and all(r['predictions'][k]==r['target'] for k in [BASE,'corpus4_fivegram']) for r in rows)
  guijiu=sum(r['id'] in ['old_4139','old_4151'] and r['predictions'][name]==r['target'] for r in rows)
  diagnostics.append(f'| {LABELS[name]} | {gained} | {lost} | {guijiu}/2 |')
 report+='\n'.join(diagnostics)+'\n'
 best=max(variants,key=lambda name:sum(r['predictions'][name]==r['target'] for r in rows))
 stats=json.loads((ROOT/best/'summary.json').read_text())['combined']['comparisons'][BASE]
 report+=f'\n## 推进建议\n\n本轮已测配比中，{LABELS[best]} 合计命中最多，相对主线 {stats["improved"]} 句进步、{stats["regressed"]} 句退步、净增 {stats["net"]}。它可作为后续压缩实验的候选；90∶10 的旧集回归较少，可保留为对照。概率权重不等于原始语料 token 混合比例。应先验证合并／剪枝到约 500 MB 后是否仍有收益，并用独立近期测试集验证，再决定主线部署。\n'
 if 'mix_old60' in variants:
  report+='\n60∶40 额外通过 344 次独立逐 token 概率核对；验证日志：`adapter-validation-60.log`。与 70∶30 的完整逐句差异：`mix_old60/changes-vs-mix_old70.tsv`。\n'
 (ROOT/'REPORT.md').write_text(report)
 Path(__file__).with_name('CORPUS4_PROBABILITY_MIX_20K.md').write_text(report+'\n归档位置：`'+str(ROOT)+'`\n')
 print('\n'.join(table));print('\n'.join(pair))
if __name__=='__main__':main()
