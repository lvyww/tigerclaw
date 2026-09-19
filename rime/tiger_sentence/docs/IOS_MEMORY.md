# iOS-oriented memory reduction — 2026-09-14

基准：合并 #8/#9 后的 `c4ab19aa0d3ca4a18aeec4c83308547055f84b04`。
生产模型：230,400,096 字节，TCSKNM02，SHA-256
`79591aaf111ddcdbf9301755f351788ff7107abe629cfb1290abb9572fa9c413`。
模型及学习数据库不加入仓库。

## 不变量与实现

不改码表、模型数据、浮点运算顺序、Beam 宽度、学习权重、候选数、完整置信池或
自动上屏阈值；没有通过少搜索、删除学习记录或关闭模型来节省内存。

1. **稀疏索引也分页**：每 256 条原有索引记录（4 KiB）驻留一个首键；查询通过
   两级精确二分定位。原文件无需转换。此模型旧版整读的索引/单字记录共 7.69 MiB；
   新的 packed 索引目录仅 15,440 字节，另有有界索引页缓存和原有单字概率 Lua 表。
   这里不是把整个模型内存宣称为 15 KiB。
2. **紧凑列式缓存**：上下文、bigram 记录用 FIFO 槽位和数值列，避免每条记录再
   分配一张 table；零概率、observed=false、缺失上下文和淘汰重查语义不变。
3. **减少重复保留**：单候选码复用原候选数组；已选定的最终 Beam 桶保留冻结结果，
   释放其辅助去重表示。这不新增任何搜索剪枝。
4. **学习派生缓存有界**：每个评分快照最多缓存 256 个编码的派生评分表，淘汰后
   从原统计重建。原始记录、竞争衰减统计、时间戳和旧评分快照的语义不变。
   这是编码分区数量上限，不是严格字节上限：一个编码下很多竞争词仍可能较大。
5. **可释放缓存**：清理时换新 table，释放旧哈希容量。`trim_memory()` 仅清理
   可重建的查询/模型页/学习派生缓存并进行一次 GC，不关闭模型、不清除活动路径、
   锁定、置信证据或待提交的学习事务。

## 两个档位

| 缓存预算 | balanced（可选） | compact（方案默认） |
| --- | ---: | ---: |
| 模型数据页 | 8 MiB | 2 MiB |
| 稀疏索引页（两类合计上限） | 512 KiB | 128 KiB |
| 上下文记录（每类） | 16,384 | 4,096 |
| bigram 记录 | 8,192 | 2,048 |
| logp 记录 | 32,768 | 8,192 |
| observed 记录 | 32,768 | 4,096 |
| 孤立惩罚记录 | 8,192 | 2,048 |

`compact` 明确配置，不凭操作系统字符串猜宿主。带 schema 的 processor/translator
入口读取配置；没有 schema 的内部 decode 不重置当前档位。切换只影响缓存容量，
不触发学习命名空间/评分 epoch/模型 generation 变化。

```yaml
# 合并到 tiger_sentence.custom.yaml 的已有 patch：
patch:
  "tiger_sentence/memory_profile": compact
```

整体替换四个 `lua/tiger_sentence*.lua` 文件；不必重建模型或清空学习数据库。

## 实测：内存

Linux x86_64，系统 liblua5.4 5.4.7，独立进程计数分配器。以下为 **5 次新进程交错
配对运行的中位数**，单位 MiB。三种版本使用同一个探针、码表和上述真实模型。
包含命名输入、100 个固定种子的 40 键随机输入、一次 128 键长输入和复位。
阶段边界显式 GC 以区分存活与待回收对象；随机输入第 20～100 组之间无强制 GC。
因此这是隔离探针，不冒充不间断实机内存曲线，也不是 iOS 原生宿主的测量。

| 指标 | 旧版 | 新版 balanced | 新版 compact |
| --- | ---: | ---: | ---: |
| 模型加载后，GC 后存活 Lua 堆 | 14.30 | 6.79 | 6.79 |
| 100 组随机输入后，GC 后存活 Lua 堆 | 30.01 | 21.62 | 12.07 |
| 128 键长输入，GC 后存活 Lua 堆 | 34.07 | 25.67 | 16.13 |
| 全探针 Lua 分配峰值 | 59.74 | 41.27 | 24.24 |
| 进程高水位 RSS（Linux，不是 iOS footprint） | 66.27 | 47.52 | 28.96 |

Lua 峰值来自分配器的每次分配/释放计数，不是采样最大值；不计 malloc 内部开销、
Rime/LevelDb/C++、键盘 UI、其它方案与系统文件缓存。Linux RSS 列包含该测试进程的
本地分配开销，但仍不能替代 iOS 的 physical footprint。即使 Lua 存活量已下降，
宿主分配器也未必立即归还所有 RSS。

另做 10,000 条、每条 16 字且编码不同的合成学习历史压力探针（不是代表性用户
数据，也不含 LevelDb）：GC 后存活量 **56.23 → 10.16 MiB**，Lua 分配峰值
**70.36 → 20.46 MiB**。两版事件与聚合统计均保留；逐条及淘汰后再查仍返回相同分数。
该压力测量各跑一次，不混作上述五轮中位数。

## 实测：时间取舍

同一 100 组随机逐键输入，计入置信证据和分段显示，单位 ms/键；表格是每轮统计值的
五轮中位数。测量 `os.clock()` CPU 时间，不是物理按键到 UI 的墙钟延迟或冷存储延迟。
OS 页缓存没有清空。

| 指标 | 旧版 | balanced | compact |
| --- | ---: | ---: | ---: |
| 平均 | 0.547 | 0.534 | 1.457 |
| P95 | 1.493 | 1.446 | 4.267 |
| P99 | 2.710 | 3.002 | 8.343 |

**紧凑档用更多重读/重算换内存，不是无代价提速。** 方案默认使用 compact；用户可按
实际可用预算和延迟表现选择 compact 或 balanced。每轮最大延迟存在波动，不据此承诺最坏情况已解决。

## 验证与复现

真实模型旧/新独立进程逐键差分：balanced 15,128 个快照、compact 15,128 个快照，
均零差异。compact 每 37 个快照调用一次清理钩子；活动候选、路径边界、学习标志、
完整置信池和证据仍一致。浮点值使用十六进制精确序列化；这不是标注语料准确率统计。
新增内存回归 6,319 项（无生产模型），真实模型专项 6,356 项；保留既有回归与故障注入。
另以更大的合成模型覆盖第二级索引跨页、最后一页、缺失与淘汰回查。

```sh
python3 tools/run_regressions.py --lua lua5.4 --negative-control
lua5.4 tools/test_memory.lua . --require-model
python3 tools/compare_revisions.py --baseline /path/to/c4ab19a --lua lua5.4 \
  --model /path/to/sentence-ngram-mobile.bin --require-model \
  --memory-profile compact --trim-every 37 --report production-compact.json

# 计数分配器需要 Lua 5.4 开发头文件：
cc -O2 tools/lua_memory_runner.c $(pkg-config --cflags --libs lua5.4) -o /tmp/lua-memory
# 各源码目录均放置/链接同一个 models/sentence-ngram-mobile.bin：
/tmp/lua-memory tools/bench_memory.lua /path/to/c4ab19a baseline 100
/tmp/lua-memory tools/bench_memory.lua . balanced 100
/tmp/lua-memory tools/bench_memory.lua . compact 100
/tmp/lua-memory tools/bench_memory_learning.lua . 10000
```

普通 Lua 也能运行探针，但没有计数分配器时峰值字段明确为 `null`，不能用阶段采样
冒充真实分配峰值。独立差分报告包括全部 Lua 源码哈希，不只记录主模块。

## iOS 验收与边界

Apple 明确说明扩展的内存预算显著小于前台应用，且进程内存限制可能变化：
- https://developer.apple.com/library/archive/documentation/General/Conceptual/ExtensibilityPG/ExtensionCreation.html
- https://developer.apple.com/documentation/os/os_proc_available_memory

不能给所有 iPhone/Rime 宿主硬套一个固定阈值。尚未在用户的 iOS 宿主或真机上运行；
没有证明整个扩展一定低于系统限制。实机应记录扩展 physical footprint、
`os_proc_available_memory()`、峰值/长期使用、键盘被系统终止日志和按键墙钟延迟。

`memory_status()` 不加载模型、不强制 GC。`trim_memory()` 是宿主可选钩子，**没有
自动接入 iOS 内存警告**；只能在所属 Lua 线程、按键处理之间调用，不要逐键调用。
本次没有改变全局 GC 模式。TCSKNM01 旧格式仍按原路径整读，iOS 请用 TCSKNM02。
8/2 MiB 只是模型数据页的保留预算；特殊模型若单页超过预算仍须临时容纳该页。
活动解码历史、很大词库或同一编码的密集学习分区仍可能扩大内存，不能把本档位
当成严格总内存上限。没有以截断上下文来隐藏这一限制。
