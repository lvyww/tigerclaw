# WeType 本机黑盒输入探针

2026-09-21，在 Windows ARM64 / WeType 2.1.4.6 上验证成功。
实际运行证据：`C:\Archive\tigerclaw_sentence_ml\experiments\wetype-probe-20260921\probe.log`。

| 模拟字母按键 | 提交前 composition | 空格上屏后读取文本 |
|---|---|---|
| nihao | ni'hao | 你好 |
| yinhang | yin'hang | 银行 |
| xingzou | xing'zou | 行走 |

程序新建独立 RichEdit 窗口，通过 TSF ActivateProfile 的 FORPROCESS 标志选择
本机 WeType 配置，不修改默认输入法或安装文件。SendInput 发送真实虚拟键的
按下/抬起，不粘贴中文或用 WM_SETTEXT 伪造输入结果。每次发送前检查测试窗口
焦点、活动 WeType profile 和修饰键；条件不符立即停止。结束时关闭测试窗口。

这是三例连通性探针，不是准确率或延迟基准。字母间隔 100ms，完整输入后等待
1000ms，再按空格，等待 600ms 后读取控件。等待常数不能视为输入法实际响应时间。
沿用当前 WeType 用户设置与在线状态，未隔离个人词库/云服务，也未清除学习数据；
提交操作可能进入 WeType 自学习。批量评测应明确记录这些条件。
仅验证首选上屏结果与 composition 可读，未验证完整候选列表或 Top-10 读取。

使用 Visual Studio 的 ARM64 环境编译：

```bat
cl /nologo /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE probe.cpp /link user32.lib ole32.lib imm32.lib uuid.lib
probe.exe C:\path\probe.log
```

TSF 接口依据：[Microsoft ActivateProfile 文档](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile)。

## 批量测试

`batch.cpp` 接受 `输入.tsv 输出.jsonl 每键间隔毫秒 [输入后等待毫秒]`，TSV 仅包含 ID 和拼音，
不把参考汉字传给测试程序。通过同一个独立 RichEdit 控件模拟按键；每句清空控件，
完整输入后等待 1000ms，空格确认后等待 composition 清空且文本稳定至少 400ms。
失焦或切换输入法时暂停；恢复后将当前句标为 interrupted，不静默重试。
输出已存在时拒绝覆盖，最长等待焦点 120 秒。运行期间日志允许只读监控。

初轮数据：`C:\Archive\tigerclaw_sentence_ml\experiments\wetype-blackbox-300-20260921`。
从原冻结 test 分区按 `SHA256("wetype-blackbox-20260921:" + id)` 排序选取 300 句。
另取 dev 分区 10 句，用 30ms 与 100ms 两档按键间隔校准；两档结果全部相同，
其中 73 字母句均只保留前 60 字母。因此正式样本在抽样前限制为编码不超过 60
字母；保留最初未过滤的候选样本和两档试跑日志，不改写原冻结测试集。

初轮运行使用 30ms 请求间隔（Windows 定时器实际节奏可能更慢）。每句记录输入、
提交前 composition、去分隔符后的完整性校验、提交文本、剩余 composition、
中断/超时状态和人工等待在内的耗时。完整性不通过的结果不能混入准确率。

`analyze_batch.py extract 实验目录` 从已冻结的 m5 / joint Beam 200 / joint Beam
2000 结果中提取相同 ID 的首选；`report` 校验全部 ID、输入与提交完整性后生成
首选准确率、字符错误率、Wilson 区间和逐句救回/退化结果。不重复运行离线模型，
不测 Top-10，不把模拟打字总耗时当作输入法响应时间。

### 后续时序核验与慢速正式测试

初轮结果检查发现，完整编码进入 composition 并不保证最终候选不受输入节奏影响。
5 句抽查中有 3 句在 100ms/键下变化，包括末尾漏字恢复，因此初轮 300 句的
61.33% 仅为快输入条件结果，不作为稳定准确率结论。

慢速正式目录：`C:\Archive\tigerclaw_sentence_ml\experiments\wetype-blackbox-100-slow-20260921`。
为控制时长，固定取原随机顺序的前 100 句，不按正确与否选择。每键 100ms，
输入结束后等待 3000ms，再按空格；仍校验全编码、composition 清空及文本稳定。
额外 10 句（5 条异常/对照句 + 5 条开发集句）在 100ms/200ms 两档下均等待
3000ms 后提交，输出 10/10 完全一致。详见 timing-comparison.json。

100 句已经历初轮快输入测试，部分还参与时序复核，没有清除 WeType 学习记录；
重复测试、云服务及个人设置的影响未隔离。比较属于当前安装的黑盒表现，不是
出厂词库、纯离线模型或首次输入准确率。

## 持续采集（用户手动停止）

`batch.exe queue.tsv results.jsonl --continuous` 使用四列 UTF-8 TSV：
`id / code / expected_text / initial_stage`，最后一列为 `fast` 或 `slow`。
参考文本仅用于判断是否复核，从不注入输入控件或输入法。

- fast：30ms/键，结束等待 1000ms；完整且正确则跳过慢测。
- fast 错误或不完整：同句 slow 复核，100ms/键，结束等待 3000ms。
- slow：允许直接补齐上一轮未复核的错误句，不重复快测。
- 每次结果和复核决策立即追加保存；快测通过率、慢测救回及两阶段综合通过率
  必须分开统计，综合通过率不是统一速度下的一次首选准确率。
- 失焦、修饰键按住或活动输入法变化时无限期暂停。焦点返回后记录中断尝试并
  从该阶段重试，排除受干扰的记录；不会主动抢回焦点。
- 关闭测试窗口或创建 `results.jsonl.stop` 即停止，停止时取消本控件残留组合。
  不自动重启。队列耗尽后保持窗口等待，不重复刷同一批数据影响自学习。

当前活动目录由 `C:\Archive\tigerclaw_sentence_ml\experiments\wetype-active-run.txt`
记录；该目录的 `STOP.bat` 可一键写入停止标记。后台 Windows 进程号见 `process.json`。
`collection_status.py 活动目录` 只读检查累计进度。
