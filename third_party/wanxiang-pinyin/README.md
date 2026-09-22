# 万象词库来源与转换说明
来源：amzxyz/rime-wanxiang Base v18.0.8，固定于 2026-09-21 的本地快照。
https://github.com/amzxyz/rime-wanxiang/releases/tag/v18.0.8
上游 README 标注 CC BY 4.0：https://creativecommons.org/licenses/by/4.0/
原作者及各词表说明见 wanxiang-dictionary-sources.zip 内 README 与源词表。

转换：保留主词典 import_tables 的纯汉字词条、原读音与原词频；去声调、ü 写作 v；
编码 nve/lve 规范为 nue/lue，模型 token 恢复 nve/lve；重复词码取最大频次，负数置零；
非汉字/无效编码排除。对应源文件 hash、排除明细见 wanxiang-pinyin.manifest.json。
字音映射来自源文件逐字拼音，不调用自动注音。共 2,202,473 条。
未包含万象 Lua 引擎或语法模型；虎爪仍使用自身三阶搜索及可选五阶重排模型。
