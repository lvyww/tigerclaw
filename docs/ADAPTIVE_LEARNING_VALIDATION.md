# 自适应自学习迭代：验证记录

开发基于 GitHub 主线，在 mec-fedora 独立目录编译和测试。zenbook 离线；未改其工作区，未覆盖虎娘候选窗/焦点调整的未提交修改，未打包或安装日用版本。

| 验证 | 结果 |
| --- | --- |
| TigerClaw C# 生产代码及测试项目编译 | 通过，.NET 10.0.401，PublishAot=false |
| TigerClaw 可移植学习套件 | 29,762 项；另包含加权增量索引和可读日志缓存审查 |
| Tigirl C++ 学习套件 | 30,059 项，含真实 SentenceSession 补充子串识别、加权规划、持久化 |
| 两端独立进程日志竞争 | 每端16进程、320个事件、重复重放不加计 |
| Rime Lua 5.4 全套回归及功能负向对照 | 通过；学习23,063项，新策略/真实文本IO 788项 |
| TigerClaw 内附 Rime 全套回归及功能负向对照 | 通过；仅同步学习模块与补充语料接口，未替换独立纠错代码 |
| 新格式 Python 维护工具 | 每端15项，覆盖跳级撤销、重放、备份和损坏文件保护 |
| Git diff whitespace check | 通过 |

新增覆盖：首次/后续每次1..3级、重叠片段不累加、一次片段纠正一条记录、真实确认次数与排序等级分离、补充语料无历史仍可识别、低权重语料精确成员查询、非法编码边界拒绝、加权计划与独立重放一致、UTF-8中文文件/BOM/CRLF/无末行换行/重启/失败写入/旧格式拒绝。

Rime真实文本IO测试使用生产读写代码，互斥锁API由测试对象提供；不代表实际librime宿主验收。C#可移植套件不包含Windows Engine/TSF/Hook协议验收。C++测试在Linux构建；本轮不宣称Windows DLL或安装包已经重建，也不宣称真实输入法前端验收完成。

复现命令：

```console
# TigerClaw
 dotnet run --project next/TigerClaw.Core.Tests -p:PublishAot=false -- --learning-portable-tests
 python3 tests/test_learning_maintenance.py
 python3 tools/run_regressions.py --lua lua5.4 --negative-control
# Tigirl
 python3 tests/sentence_learning_test.py
 python3 tests/test_learning_maintenance.py
# Standalone Rime
 python3 tools/run_regressions.py --lua lua5.4 --negative-control
```

开发机使用系统Lua5.4共享库的独立进程启动器执行相同Lua文件，未为此安装系统软件。正式Windows及其它Lua版本继续由仓库现有CI验证，不在以上已完成结果中。
