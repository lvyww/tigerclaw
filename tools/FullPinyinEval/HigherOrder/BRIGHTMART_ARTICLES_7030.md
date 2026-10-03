# Brightmart 完整版 70% + Articles 30% 概率融合

2026-10-03，用户指定固定权重的离线实验。旧 Brightmart 完整模型及中间文件曾按授权清理，本轮从原始语料重建。
训练输入 3,340,161,522 个汉字，SHA256 `f8b677f84b54a9e839330aeca25213aa2c1ea7a1f8716af5db9a844f1d9daf3d` 与历史记录完全一致。
含新闻、百科、社区问答、维基；新闻目录现位于 `C:\Archive\Copus\new2016zh`，通过新实验目录只读符号链接恢复原输入布局。
五阶 modified Kneser–Ney，训练期 `--prune 0 0 1 1 1`；没有后续 context128 剪枝。
Articles 从保留的原始 ARPA 恢复 Q16/Q8，最终 Q8 哈希与历史模型完全一致。

## 结果

| 模型 | 大小 MB | 旧集 10,000 | Articles 33,129 | THUC 30,000 | 合计正确 / 73,129 |
|---|---:|---:|---:|---:|---:|
|Brightmart 完整版|2032.38|9,963|32,878|29,951|72,792|
|当前三模型主线|405.66|9,945|32,913|29,964|72,822|
|Brightmart 70% + Articles 30%|4535.76（双模型合计）|9,959|32,937|29,948|72,844|

## 配对变化

| 集合 | 参照 | 救回 | 退步 | 净增 | 首选改变 |
|---|---|---:|---:|---:|---:|
|old10k|brightmart_full|0|4|-4|5|
|old10k|threeway_mainline|16|2|+14|22|
|articles|brightmart_full|92|33|+59|136|
|articles|threeway_mainline|68|44|+24|120|
|thucnews|brightmart_full|7|10|-3|17|
|thucnews|threeway_mainline|11|27|-16|39|

## 方法与验证

每个模型按自己的词表和状态独立完成回退，然后按 `P = 0.70 × P_Brightmart + 0.30 × P_Articles` 混合每个字符及 EOS 的概率，再取自然对数。
这不是拼接语料重训，也不是平均候选排名或对数概率。未物化为单一模型，未做融合后剪枝或部署。
使用冻结 Lua 解码器、码表和历史孤立字先验，关闭学习、Qwen 和提前上屏。三套输入、目标、fixture 和导出器身份均核对。
历史先验从保留的 shape-logfusion 实验备份恢复：469,886,928 字节，SHA256 `c0063898fdff27c1fb00c1c72fa28a6c1b375fade1ec2045d731b9db958bdecc`，与历史文档记录一致；见 prior-restored.json。
融合版为本轮新跑；参照成绩复用已保留的历史逐句结果。Brightmart 恢复后的哈希与此前完成全量预测一致性验证的稳定版本完全相同。
权重 0、1、0.3 的独立标量概率测试通过；端点完整候选池及分数逐字节一致。端点及三套融合评测各通过 666 项增量/回删/锁前缀回归。
重建采用历史稳定版本，未复现原始 KenLM 文件两处记录中的 8 个异常字节；详见 brightmart-restored.json、reconstruction 历史记录。
历史数据存在训练重合且反复用于实验，本次不作为独立泛化测试，不按这些成绩继续挑选权重，也不代表手机或桌面实机延迟验收。
另一个 Rime 邻键纠错 holdout 任务保持原状态，本实验没有恢复它。

## 模型身份

- brightmart: `/home/yc/tmp/brightmart-articles-7030-20261003/brightmart-full-q8.klm`，2,032,382,402 字节，SHA256 `00252320a5cfac09906fe972c5e855d9ac56a3cc86ff1949200aeb37b91d376a`。
- articles: `/home/yc/tmp/brightmart-articles-7030-20261003/articles/model-q8.bin`，2,503,378,280 字节，SHA256 `2e8687555dbd301dfe35384634d5ee7a0026d009fa3ac21a57a1ab7a72f1c169`。

工作目录：`/home/yc/tmp/brightmart-articles-7030-20261003`。逐句预测、候选池、配对变化及日志均保留。
