# TCSKNM03 Q8 五阶模型

默认模型从 2026-09-24 起为 `brightmart-char5-context128-tcs3-q8`。
虎爪和 Rime 使用同一份文件；Rime 用纯 Lua 分页读取，不需要原生 DLL。

- 文件：`sentence-fivegram-mobile.bin`
- 大小：356,492,204 字节（356.49 MB）
- SHA256：`5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361`
- 各阶记录数：21,230 / 7,959,327 / 69,562,625 / 10,273,459 / 8,415,769。

## 数据与量化

训练来源和剪枝沿用 Brightmart 字符五阶：1–3 阶全保留；4–5 阶保留历史字属于
unigram 前128字（含特殊符号）的上下文，删除分布的回退权重为1。
Q8 由已发布 Q16 模型再次量化，不重新训练、不进一步剪枝。

概率码 `round(q16/257)`，共256档；回退码0精确表示log10权重0，其余使用
`1+round((q16-1)*254/65534)`。不能将量化码0当成缺失记录。
它与 KenLM 的 Q8 算法不同，不宣称逐分等价。

## 文件布局

magic 为 TCSKNM03，header 256字节，四个256项 bucket directory，词表和2–5阶
context block/index区。每64个上下文一个稀疏索引点。上下文字ID、后继字ID和
后继计数为uint16。Q8为version 2，概率/回退各uint8，header step使用1e-9单位；
min使用1e-7单位。读取器也支持同格式version 1/Q16（step单位1e-12）。

查询从最长历史逐级回退。BOS进入历史，EOS参与评分；同一个文件提供观察二元组
先验。上下文不存在时回退权重为0；上下文存在但无后继时仍保留其回退权重。

## 构建

先用 `tools/build_tcs_knm03.cpp` 生成 Q16，再用 `tools/requantize_tcs_q8.cpp` 转换：

```sh
g++ -std=c++20 -O3 tools/requantize_tcs_q8.cpp -o requantize_tcs_q8
./requantize_tcs_q8 q16.bin sentence-fivegram-mobile.bin
```

转换器检查所有区块、记录计数和索引位置。正式打包需核对默认模型 SHA256。

## 验证与迁移

Q8与Q16在当前Rime历史20k上的首选逐句一致：旧集9959、新集9971，合计19930。
另两组冻结解码测试：新闻30000句救回2句、无退步；articles33129句救回3句、
退步1句。语料间和训练语料存在重合，结果不等于独立泛化提升。
模型/回归/分页缓存、增量/回删/锁前缀均需通过；实际前端输入验收单独进行。

主线已删除 TCSKNM01/02 三阶读取入口。只搜索用户目录models/、用户目录根部、
共享目录models/下的五阶文件。缺失/损坏时使用已有无模型行为。升级必须同时更新
Lua模块和Q8模型；保留自己的码表、配置及学习数据。大模型不提交Git。
