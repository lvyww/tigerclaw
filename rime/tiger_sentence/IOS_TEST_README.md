# 虎整句 iOS Rime 测试包

这是移动端内存优化测试包。语言模型为 `TCSKNM02` 分页格式，保留桌面版
Kneser-Ney 模型的全部 n-gram 和 float32 概率。Lua 不再一次读入整个模型，
常驻索引约 2.1 MiB，上下文页缓存上限 8 MiB。

## 安装

1. 先在输入法应用中备份现有 Rime 用户目录。
2. 把压缩包 `Rime/` 目录内的文件导入输入法应用的 Rime 用户目录。
3. 在输入法应用中执行“重新部署”或“部署”。
4. 选择“虎整句”方案测试。

如果使用全新的测试配置，可以完整复制 `Rime/`。其中的
`default.custom.yaml` 只启用虎整句。

如果用户目录已有其他方案，不要覆盖已有的 `default.custom.yaml` 和
`rime.lua`：

- 在已有 `default.custom.yaml` 的 `schema_list` 中加入
  `- schema: tiger_sentence`。
- 在已有 `rime.lua` 末尾追加 `merge/rime.lua.fragment` 的三行内容。
- 其余 `tiger_sentence.*`、`lua/tiger_sentence*.lua` 和
  `models/sentence-ngram-mobile.bin` 可以直接复制。

## 测试重点

- 首次输入两码以上整句时，输入法是否被系统杀死、闪退或自动切回系统键盘。
- 第一次出候选的耗时，以及后续连续输入时的响应速度。
- 连续输入 5、10、20 分钟后的稳定性和内存变化。
- 候选顺序是否与 Windows 虎整句一致。
- 删除 `models/sentence-ngram-mobile.bin` 后应仍能输入，但会失去语言模型打分；
  这可用于区分模型加载问题与方案本身的问题。

反馈时请注明：iPhone/iPad 型号、iOS 版本、Rime 输入法应用及版本、是否开启
完全访问、发生问题前输入的编码，以及应用能显示的内存或崩溃日志。

## 按键

- 连续输入字母组成整句编码。
- 空格提交当前候选，回车提交原始编码，Esc 清空。
- `;`、`'`、数字分别选择第 2、第 3、第 N 个码表候选，`0` 表示第 10 个。
- 上下方向键或 Tab、Shift+Tab 遍历候选；移动端可使用输入法提供的候选操作。

