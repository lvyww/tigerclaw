# 虎整句 iOS Rime 测试

这是当前 Rime 独立实验版的 iOS 验收清单。移动模型使用 TCSKNM02 分页格式，
稀疏索引也按需分页。不要将模型文件大小、模型页缓存预算或 Lua 堆用量视为
整个键盘扩展的内存上限。实测条件及结果见 `docs/IOS_MEMORY.md`。

## 默认紧凑内存档

方案默认使用 `compact`。旧安装如需显式指定，可在 `tiger_sentence.custom.yaml` 的已有 `patch` 下合并（不要覆盖其它配置）：

```yaml
patch:
  "tiger_sentence/memory_profile": compact
```

整体更新 `lua/` 中本方案的四个模块，然后重新部署；无需转换或重新下载模型，
无需清空学习数据。可选的 `balanced` 保留较大查询缓存，默认 `compact` 将模型数据页
缓存从 8 MiB 改为 2 MiB，同时限制查询元数据；两档不改 Beam、评分、候选池、
学习规则或提前上屏阈值。缩小缓存可能增加缺页和重新计算，需要实机比较延迟。

`require("tiger_sentence").memory_status()` 返回 Lua 堆与缓存诊断，不加载模型。
宿主可在拥有该 Lua 状态的线程、按键处理之间调用 `trim_memory()` 清理可重建缓存
并执行一次 GC；它保留模型、活动解码路径、锁定、证据和待提交学习。
此钩子**没有自动接入 iOS 内存警告**，普通用户只需设置紧凑档，不必调用它。
不要逐键强制 GC，也不要从另一个线程直接调用 Lua。

## 部署

1. 备份输入法应用的 Rime 用户目录。
2. 按同目录 `README.md` 部署方案、Lua 和三个明文数据 txt。
3. 把仓库外的 `sentence-ngram-mobile.bin` 放到用户目录 `models/`。
4. 合并而不是覆盖已有 `default.custom.yaml` 和 `rime.lua`。
5. 重新部署并选择“虎整句”。

删除模型后方案应仍可输入，但失去语言模型排序；这可区分模型读取与方案本身问题。
删除 `tiger_sentence.char_ranks.txt` 后仍可输入，但常用字过滤与生僻字孤立惩罚
一并禁用。

## 验收

- 首次两码以上输入是否闪退、被系统杀死或切回系统键盘。
- 冷启动首次候选和后续逐键候选的延时（明文码表索引在首次解码前构建，
  应只多出一次几十毫秒级的加载）。
- 连续输入 5、10、20 分钟后的内存和稳定性。
- Space/Enter/Esc、退格、候选切换和选重后缀是否正确。
- “单字重码组句”开关开/关时，分段路径的非首选单字竞争与整段单边首选顺序
  是否与 Windows 基准一致（如 `gyygch` 开启时可见“羊羔”，单独 `gch` 仍
  “赤”在前）。
- “提前上屏”开关关闭后，概率型提前上屏与空码自动上屏是否一并停止。
- 自动提前上屏是否出现重复提交、漏字或明显闪烁（提交通过一次原子输入
  赋值重建 composition，不经过清空）；`awmenamcunta`、`nuusvbbhoi`、
  `iejryfenahbmsp`、`uriczwxmjou` 四组回归的提交与剩余候选
  是否与 Windows 一致。
- `tiger_sentence/min_retained_raw_length` 配置为正数时，空码与概率型提交
  是否遵守最少保留编码数。
- 直接编辑用户目录的 `tiger_sentence.codes.txt`（如给某字增加一条编码）
  并重新部署后，新编码是否生效；`tiger_sentence/high_freq_limit` 设为 `0`
  后非最优码是否全部保留。
- 候选顺序是否与同一模型和码表下的 Windows/Rime 基准一致。

反馈请附：设备、iOS 版本、Rime 应用及版本、是否开启完全访问、复现编码、模型
SHA-256，以及可取得的内存或崩溃日志。
