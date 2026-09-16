# 第三方版权与授权说明

TigerClaw 自身的原创源代码、脚本及配套项目文档采用 GPL-3.0-only，见 [README.md](README.md) 与 [LICENSE](LICENSE)。本声明不改变第三方原有的版权及许可证，也不将第三方作品的版权归于本项目作者。文件内及上游附带的版权、许可和免责声明应予保留。

## Microsoft SampleIME

`BimeTSF2/SampleIME/` 和 `reference/SampleIME/` 中包含 Microsoft SampleIME 来源的代码；相关源文件保留 Microsoft 的版权及免责声明。项目自身的修改与上游原始代码应区分理解，不应删除或替换原作者声明。

微软官方 SampleIME 来源：

- https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/IME
- https://github.com/microsoft/Windows-classic-samples/blob/main/LICENSE

微软官方仓库的许可证副本保存在 [licenses/Microsoft-SampleIME-LICENSE.txt](licenses/Microsoft-SampleIME-LICENSE.txt)。个别资源另有授权时，仍以该资源的独立说明为准。

## llama.cpp

可选的 Qwen 重排组件使用 `third_party/llama.cpp` 子模块。此次核对的子模块提交为 `9b05354ec6fb58b4e665e9a39ebc40285c015638`，其顶层许可证为 MIT，版权声明为 `Copyright (c) 2023-2026 The ggml authors`。

- 上游：https://github.com/ggml-org/llama.cpp
- 对应许可证：https://github.com/ggml-org/llama.cpp/blob/9b05354ec6fb58b4e665e9a39ebc40285c015638/LICENSE
- 随仓库副本：[licenses/llama.cpp-LICENSE.txt](licenses/llama.cpp-LICENSE.txt)

子模块内部的其他第三方组件仍应保留其各自的许可。模型权重与推理程序的许可证是不同事项；此处的 MIT/GPL 声明不替代具体模型版本的授权条款。

## 紧凑整句词先验

`next/TigerClaw.Core/Data/sentence_lexical.bin` 是不可逆的 Bloom filter，派生自
[`fcxxxz/rime-mohu`](https://github.com/fcxxxz/rime-mohu) 提交
`9f43098cefdb450fe8dec0f3069fe8d9999b9d10` 的 `mohu_flypy.base.dict.yaml`。
原文件由 rime-mohu contributors 以
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) 授权，并列明 Rime
八股文词库、THUOCL、雾凇拼音词库补充数据及人工补充词等来源。

本项目只保留虎整句码表可编码的 2～4 字条目，按上游权重、词长和 Unicode
顺序选取 50,000 条，再丢弃词文本和权重，转换为 1,200,000 bit、10 次散列的
TCSLEX01 Bloom filter。转换不表示上游作者认可本项目。原文件 SHA-256 为
`877c6dacb4d5bb6738e230ce2d9235f3ac0f48404959c2db26fb18c7ddd31cb6`，发布文件
SHA-256 为 `8dbc884b6cb719d07e4cef153c8048db19a11f8224f75a4ed87853e688a27393`。

## 参考代码、模型和其他资源

`reference/` 中的参考项目保留其原始授权，不因位于本仓库而被统一重新授权。该目录的用途和构建边界以 [reference/README.md](reference/README.md) 及 [AGENTS.md](AGENTS.md) 为准。

外部取得的模型权重、训练或评测语料、码表、字体、图片、音效等资源不因与程序一同存放或打包就自动采用 GPL。使用或再分发时，应核对各自来源和许可证；已经单独授权的数据也不因本次代码授权而改变许可。

## 再分发

发布程序及安装包时，应保留项目许可证、本文件以及实际包含的第三方组件所要求的声明，并按 GPL v3 提供相应的完整对应源码及构建、安装脚本。不能仅凭一份项目 LICENSE，宣称所有外部材料均已获得再分发许可。

本文件记录此次核对的主要来源，不是完整依赖清单、历史版权审计或对所有二进制发布包的合规验收。此次许可证提交未重新打包既有发布版本。
