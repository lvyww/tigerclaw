# 搜狗拼音黑盒采集

2026-09-21，搜狗 16.8.0.4914；Windows ARM64 上运行 x64 独立 RichEdit 宿主。
复用 `../WeTypeProbe/batch.cpp`，通过编译宏选择搜狗 TSF profile。
只对本进程激活，不改变系统默认输入法；仅前台、焦点、profile 和修饰键状态
符合时发送真实字母/空格按键。参考汉字仅用于评分与决定慢速复核。

实验目录：`C:\Archive\tigerclaw_sentence_ml\experiments\sogou-1050-20260921`。
与既有五方案对照使用完全相同的 1,050 句。前三个简单连通性例子不计入；
五句长句校准直接计入，其余 1,045 句由 remaining.tsv 继续。
快测 30ms/键、尾部等 1s；错误再以 100ms/键、尾部等 3s 复核。
失焦暂停，中断尝试不计；关窗或创建 results.jsonl.stop 停止；队列完成自动退出。

搜狗在这个宿主中未通过 IMM 或 TSF composition range 返回组合文本。
因此 `rawVerified` 如实保留 false；`decision.valid` 只表示真实按键序列
完成、无中断并取得非空文本，不能证明输入法内部消费了全部编码。
每句开始 Escape 清理本控件残留组合，发送完整拼音后按一次空格提交。
这是两阶段上屏文本通过率，不是统一速度的首选准确率。
当前用户学习与在线设置未隔离；不能把模拟输入耗时当响应延迟。

实验目录中的 build-x64.bat 构建，launch.ps1 启动；输出已存在时拒绝覆盖。
不要重新运行已完成的 calibration 队列。报告可在采集中只读生成：

```bash
python3 tools/FullPinyinEval/SogouProbe/report.py \
  /mnt/c/Archive/tigerclaw_sentence_ml/experiments/sogou-1050-20260921
```

生成 report.json、REPORT.md、selected.jsonl；未完成时所有比较均缩到相同已完成 ID。
原始 JSONL、校准和失败的连通性探针记录保留，不能将空的连通性结果计入评分。

完成 891 句后，用户切回对话窗口触发暂停；重新聚焦仍未满足 profile/焦点检查，
故结束原宿主并在用户要求继续后通过 resume.ps1 重新激活本进程搜狗。
resume.tsv 仅含剩余 159 句，首句保留 slow 阶段；不重复已完成样本。
报告合并 calibration.jsonl、results.jsonl 和 resume.jsonl。
当前 STOP.bat 对应 resume.jsonl.stop，旧 results.jsonl.stop 只记录第一次停止。
