# Rime 融合五阶格式核对与转换控制

阶段记录：此文记录格式核对时的状态。后续统一剪枝、静态融合和两万句评测已完成：单文件 499,891,125 字节，19889/20000，较主线净退 37，不推荐部署。最终结果见相邻 `rime-joint-compression/REPORT.md`。安装未修改；此前 420 MB＋80 MB 方案未继续运行。

## 现有格式

Rime 仓库 `/mnt/c/Users/yc/Desktop/tiger-sentense-rime_personalized_sync_20260918` 已有 `TCSKNM03`，不需要为单一 ARPA 五阶另设计格式。它支持 uint16 词 ID、16-bit 概率和回退权重量化、按历史分块及稀疏索引、纯 Lua 分页读取，以及 observed-bigram 查询。

现有格式不是新的剪枝算法：换成这个容器本身不会将任意大模型压到 500 MB。真实主线控制转换为 460,693,519 字节，SHA256 `4e6d79b957a55edf35cd9e2e66c62bd0bbe598581b7dc088b462122a713172a7`，与既有 Rime 文件逐字节相同。

## 修复转换阶段的隐式剪枝

上游 `tools/build_tcs_knm03.cpp` 硬编码 `kLimit=128`，会再次按 unigram 排名删掉四、五阶历史，并清零对应回退权重。这适合既有主线构建，但不应默认施加于已完成统一剪枝的融合 ARPA。

新增本仓库 `prepare_rime_preserving_builder.py`：从指定上游源码生成隔离构建，保留原默认行为，并增加 `--preserve-all`。严格匹配待修改源码片段，源码变更时拒绝盲改。上游仓库、运行时均不修改；隔离生成源码随附上游 GPL-3.0 许可证。

验证：

- 130 个普通字的小模型中，最低频历史的 4/5-gram 在默认模式被删；保留模式全部保留。
- 各阶记录数：默认 133/2/2/1/1；保留模式 133/2/2/2/2。
- Lua 5.4、LuaJIT 都通过该低频历史的 2/3/4/5-gram 概率查询检查。
- 真实主线 ARPA 的 `--preserve-all` 转换重现既有 Rime 模型的完整 SHA256，无需重复运行相同模型的两万句。

## 融合语义与未完成工作

70∶30 双模型逐 token 概率混合使用 `0.7 P_old(w|h)+0.3 P_new(w|h)`。两个模型缺失 n-gram 时各自回退，通常不能用单一固定 backoff 系数无损表示其混合。输出普通 ARPA/TCSKNM03 时必须明确采用近似，不能平均 log 概率或直接平均 backoff 系数。

正确的静态融合需要统一词表／UNK 处理；在联合显式 n-gram 集上计算混合概率；按保留记录重新计算 `alpha(h)=(1-sum P(w|h))/(1-sum P(w|suffix(h)))`，其中分母必须来自实际输出的低阶模型。统一剪枝后还需再计算 backoff，并检查概率质量。量化之前和之后的差异必须分别测量。

本轮检查过的本地工具：

- KenLM `lm/interpolate/interpolate_main.cc` 是 log-linear interpolation，输入要求训练中间格式。不能把它当成已验证的线性概率混合替代品。
- MITLM 最初在小模型加载／合并阶段失败，随后定位并修复了 NgramVector::Sort 在词表 ID 改变但记录位置未变时未重建 hash 和有界视图的问题。隔离修复版本已通过 29 项混合概率与归一化检查；没有绕过断言。后续真实实验记录在相邻 rime-joint-compression 目录。

当时提出的后续工作（实际完成范围及限制以最终报告为准）：建立并验证能够处理实际两模型词表、BOS/EOS 和后缀闭包的静态融合工具；先量化融合本身的误差；统一剪枝到 500,000,000 字节以内；用保留模式输出 TCSKNM03；在固定旧三阶隔离先验下复用冻结 Beam，比较主线、完整 70∶30、合并未剪枝与压缩模型的两万句结果。不得把格式控制转换当作融合压缩完成。

## 文件

- 隔离工作目录：`/home/yc/tmp/shape-mix-rime`
- 可重建转换器：`tools/FullPinyinEval/HigherOrder/prepare_rime_preserving_builder.py`
- 回归测试：`tools/FullPinyinEval/HigherOrder/test_rime_preserving_builder.py`
- 本目录 `verification.json` 和转换日志保存验证证据。
