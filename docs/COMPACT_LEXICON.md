# Core 紧凑码表存储

2026-09-09：主码表和拼音反查表的常驻候选数据改为 `CompactLexicon`。
TXT/YAML 和用户调整文件仍是数据源，加载顺序、频率稳定排序、候选
去重及 display/commit 封装保持原有解析行为。没有修改 Rime/Fcitx5。

## 内存和二进制布局

格式名 TCLX，版本 1，全部整数为 little-endian Int32。文件头 20 字节：
magic、version、codeCount、candidateCount、textCount。随后连续存储：

| 区域 | 元素数量 | 含义 |
| --- | --- | --- |
| codeTextIds | codeCount | 原始插入顺序下的编码文本 ID |
| sortedCodeIndices | codeCount | 按 OrdinalIgnoreCase 排序的编码序号，用于二分查询 |
| candidateStarts | codeCount + 1 | 各编码的候选区间 |
| candidateTextIds | candidateCount | 原始候选顺序下的文本 ID |
| textOffsets | textCount + 1 | 文本池中的 UTF-16 code unit 偏移 |
| textPool | 可变 | 去重的连续 UTF-16 数据，不添加终止符 |

这是一份直接查询的二进制 byte[]，不会反序列化成常驻 Dictionary/List。
编码不限 ASCII 和长度。候选支持词组、补充平面字符、组合字符、内嵌
零字符，并保留原始 UTF-16 单元。候选的顺序和编码的插入顺序独立于
二分查询排序；整句最优码的同长度优先级不变。

字符串只在访问时构建；每张表最多缓存 256 个、每个不超过 256 UTF-16
单元的字符串。缓存槽使用原子发布，允许并发读取旧快照。不永久缓存
所有曾访问文本。候选视图只持有所属表及区间，普通/拼音分页只读取
实际一页；兼容调用 `GetCandidates` 仍返回独立 List。

目前二进制常驻托管内存，**不是文件映射，也未启用自动磁盘缓存**。
`WriteTo`/`FromBinary` 可原样导出、验证并载入该布局；日用目录不会因此
生成新文件。加载验证覆盖版本、区间、文本 ID、查询索引顺序/唯一性和
截断；从外部缓冲区载入时复制数据，防止调用者随后修改。

## 生命周期与用户调整

- 拼音表来自独立根目录，只由 CoreRuntimeState 持有，不再放入方案快照。
  Ctrl+M 切换和最近方案缓存共享同一张表；显式完整重载重新读取拼音数据。
  主表和拼音表都准备完成后再在锁内发布。已有候选视图仍能读旧版本。
- 主码表为不可变快照。加词、删除、置顶、前移创建新的二进制表，再按
  现有规则更新元数据和 LexiconVersion；用户调整持久化逻辑不变。
- 普通调序复用文本 ID，复制紧凑数组。追加文本产生的废弃空间超过上次
  整理尺寸的 25% 或 64 KiB（取较大者）时重新压实；删除大量文本也触发
  压实。无需空闲释放，不引入反查首次按键加载。
- 主码表解析期间的构建 Dictionary、注释/拆分/全码元数据，以及整句
  解码专用索引仍存在。不能把本次单表节省量视为整个 Core 的节省量。

## 验证和测量

```powershell
dotnet build next/TigerClaw.Core.Tests/TigerClaw.Core.Tests.csproj -c Release -p:PublishAot=false
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll --compact-lexicon-tests
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll
dotnet next/_run/Tests/Release/net10.0-windows/TigerClaw.Core.Tests.dll --compact-lexicon-bench <单个码表文件>
```

基准只调用生产解析器读取指定文件，不 Initialize、不改配置、不启动
Core。原 Dictionary 存活内存用 GC.GetTotalMemory(true) 差值近似测量，
包括一次性运行环境噪声；二进制值是缓冲区准确字节数，不包含对象头、
少量字段和有界字符串缓存。查询为预热后循环 200,000 次“编码查找＋读取
首选文本”，不含 UI、IPC、组句及模型，亦不含旧 API 的 List 复制。
测试程序是 .NET Release 托管运行，不是发布的 AOT 程序性能。

2026-09-09 本机两份实际数据，所有编码和候选顺序逐项匹配：

| 单表 | 编码 / 候选数 | Dictionary 近似字节 | 紧凑二进制字节 | 20 万次查询：Dictionary / 紧凑 |
| --- | --- | --- | --- | --- |
| 拼音.txt | 38,999 / 65,120 | 9,283,424 | 2,021,730 | 7.02 / 52.31 ms |
| 虎整句.txt | 14,374 / 15,369 | 2,597,712 | 451,038 | 4.50 / 70.97 ms |

上述首轮编码耗时分别约 36.89 / 16.72 ms；单次候选反序更新约 8.41 /
6.83 ms（含首次 JIT，不含 Core 后续元数据更新）。紧凑查询比直接
Dictionary 查询慢；本轮均摊约 0.26 / 0.35 微秒，不能据此宣称打字加速。
真实全进程内存和端到端延迟仍需固定方案/模型/输入轨迹的后续测量。

测试覆盖二进制往返和非法输入、任意 UTF-16、并发读取、400 次随机
修改后的全表对照、废弃文本空间限制、大条目删除、原始编码/候选顺序、
旧快照隔离、拼音跨方案共享、重载替换，以及末页空格和第二页数字选重。
