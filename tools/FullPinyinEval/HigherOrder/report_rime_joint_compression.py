"""Report the single-file Rime mixture against unchanged frozen baselines."""
import csv,json
from pathlib import Path
ROOT=Path('/mnt/c/Archive/char5-corpus4_0-20260922/rime-joint-compression')
NAME='mix70_tcs3_joint'
LABELS={'old_420mb_fivegram':'当前主线 KenLM Q8','mainline_tcs3_frozen':'同一主线 ARPA → Rime TCSKNM03','mix_old70':'完整双模型 70∶30',NAME:'统一历史剪枝＋静态融合 Rime'}
def main():
 selected=json.loads((ROOT/'selected.json').read_text())
 summary=json.loads((ROOT/'evaluation/summary.json').read_text())
 rows=[json.loads(s) for s in (ROOT/'evaluation/predictions.jsonl').read_text().splitlines()]
 assert len(rows)==len({r['id'] for r in rows})==20000
 lines=['| 模型 | 旧集 / 10000 | 新集 / 10000 | 总正确 | 准确率 |','|---|---:|---:|---:|---:|']
 for name,label in LABELS.items():
  values=[summary[g]['methods'][name]['correct'] for g in ['old','fresh','combined']]
  lines.append(f'| {label} | {values[0]} | {values[1]} | {values[2]} | {values[2]/200:.3f}% |')
 changes=['# 全部进退步','']
 for baseline in ['old_420mb_fivegram','mix_old70','mainline_tcs3_frozen']:
  changes+=['## 相对 '+LABELS[baseline],'','| ID | 原句 | 原输出 | 融合压缩输出 | 变化 |','|---|---|---|---|---|']
  for r in rows:
   a,b,t=r['predictions'][baseline],r['predictions'][NAME],r['target']
   if (a==t)==(b==t):continue
   values=[r['id'],t,a,b,'进步' if b==t else '退步']
   changes.append('| '+' | '.join(v.replace('|','\\|') for v in values)+' |')
  changes.append('')
 (ROOT/'ALL_IMPROVEMENTS_REGRESSIONS.md').write_text('\n'.join(changes)+'\n')
 with (ROOT/'COMPARISON_ALL_20000.csv').open('w',encoding='utf-8-sig',newline='') as f:
  out=csv.writer(f);out.writerow(['id','source','code','target']+list(LABELS.values()))
  for r in rows:out.writerow([r[k] for k in ['id','source','code','target']]+[r['predictions'][k] for k in LABELS])
 report=f'''# 70∶30 单文件 Rime 融合压缩实验

模型：{selected['bytes']:,} 字节（{selected['bytes']/1e6:.2f} MB），TCSKNM03，SHA256 `{selected['sha256']}`。

'''+'\n'.join(lines)+'''\n
## 方法与边界

使用统一词表的 70∶30 unigram 频率排序，对两模型应用相同的后缀封闭历史保留规则。保留全部一、二阶；三阶历史字符排名上限见 selected.json，四、五阶上限均为 64。候选阈值仅按模型字节数选择，没有按两万句准确率选择。

这种共同整历史删除与线性概率混合可交换，已做 484 次穷举检查。它仍属于粗粒度 context pruning，并非基于逐条相对熵的最优剪枝，不应宣称保留了全部有价值的低频高阶记录。

随后用固定线性权重 0.7/0.3 构造联合显式 n-gram 的概率，重新计算回退权重，输出单一静态 ARPA，再用 TCSKNM03 的 16-bit 量化和分页布局保存。转换使用 --preserve-all，没有再次隐式剪枝。两模型共享预算，不是旧模型保留 420 MB、新模型限定 80 MB。

普通单回退静态模型无法一般性地精确表示完整双模型概率混合，故结果包含静态近似损失。统一词表中某字不属于一个组件词表时，该组件赋零质量；这与原在线双 KenLM 各自映射 UNK 的策略不同。输入为原 ARPA 概率，也不同于之前两个 Q8 模型的量化概率。这些差异均不得归因于容器或剪枝单一因素。本轮没有单独跑未剪枝的单一静态融合模型，因此不能精确分拆静态近似、统一词表、剪枝和量化各自造成的进退步条数。

主线格式控制转换与既有 Rime 主线文件 SHA256 完全一致；另在本轮冻结解码器下重新评测，保留原三阶 observed-bigram 隔离先验。因此这里的对照更适合与此前混合实验比较，不能直接当成本机 Rime/虎娘实际输入验收。

每档分母固定 20,000，旧集原 m5 held-out、新集 mohu v5 held-out。无 LLM、学习、提前上屏或延迟对比。旧、新集已用于多次排查，并非独立的新近语料泛化测试。没有修改安装。

模型字节预算仅指这一个五阶文件；冻结评测仍加载原三阶隔离先验，不把其他运行资源算入此数字。

## 相对变化

'''
 for baseline in ['old_420mb_fivegram','mix_old70','mainline_tcs3_frozen']:
  stats=summary['combined']['comparisons'][baseline]
  report+=f'- 相对{LABELS[baseline]}：{stats["improved"]} 句进步，{stats["regressed"]} 句退步，净 {stats["net"]:+d}。\n'
 delivery=json.loads((ROOT/'model-delivery.json').read_text())
 report+='\n## 记录数与验证\n\n| 阶数 | 当前主线 | 融合压缩 |\n|---|---:|---:|\n'
 for n,(old,new) in enumerate(zip([21230,7959327,69562625,10273459,8415769],delivery['ngram_counts']),1):
  report+=f'| {n} | {old:,} | {new:,} |\n'
 report+='\n5 万高频词相邻二元组覆盖 50,000/50,000，独立缺失二元组为 0；此前七个缺口均存在。该指标只表示存在，不代表概率足够高或整句一定正确。\n'
 report+='\n固定权重融合小模型 29 项检查、共同历史剪枝 484 项检查、Lua/LuaJIT 低频历史转换检查、真实主线转换 SHA256 复现，以及每档 666 项增量/回删/锁定检查均通过。\n'
 report+='\n最终模型在 15 个上下文上遍历全部 22,239 个可预测词元，概率和最大偏差 0.0000242483，小于量化误差界限 0.000515094。输出模型已流式复制到本目录，并重新计算 SHA256 核对。\n'
 report+='\n模型：`sentence-fivegram-mobile.bin`；完整身份及记录数：`model-delivery.json`。\n'
 report+='\n## 结论\n\n体积和结构验证达标，但命中率回退，不建议替换主线。相对主线的 58 条退化，正确目标仍全部在前 3 位（54 条第 2、4 条第 3）；这批退化表现为排序变化，不能用词汇覆盖率 100% 来证明质量已经达标。此前两句“愧疚”仍正确。\n'
 report+='\n下一步需要区分静态回退近似与共同历史剪枝各自的损失，并评估逐条重要性／相对熵剪枝；不能把本轮的失败解释为 Rime 容器本身不适用，也不能宣称已经保住完整 70∶30 的收益。\n'
 report+='\n全量对照：`COMPARISON_ALL_20000.csv`；全部进退步：`ALL_IMPROVEMENTS_REGRESSIONS.md`；模型信息：`selected.json`。\n'
 (ROOT/'REPORT.md').write_text(report)
 Path(__file__).with_name('RIME_JOINT_COMPRESSION_20K.md').write_text(report+'\n归档：`'+str(ROOT)+'`\n')
 print('\n'.join(lines))
if __name__=='__main__':main()
