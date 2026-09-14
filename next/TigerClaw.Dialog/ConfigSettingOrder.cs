using System;
using System.Collections.Generic;

namespace TigerClaw.Dialog
{
    // Presentation only: never derive configuration persistence order from this list.
    internal static class ConfigSettingOrder
    {
        private static readonly string[] Keys =
        {
            // System
            "开机自动启动", "自动切换系统语言", "默认中文", "中文状态下使用英文标点",
            "主题", "隐藏状态栏", "开启打字音效(娱乐)", "按键音量0~100",
            // Code tables and ordinary input
            "当前码表", "码表存储位置", "最大码长", "最大码长无重自动上屏",
            "中英文不限长混合输入", "空码自动清屏",
            // Keys
            "shift切换中英文", "Ctrl+空格切换中英文", "切换最近码表快捷键", "手动加词快捷键",
            "`键拼音反查", "翻页键", "自定义选重键", "分号次选", "引号三选",
            "回车清屏", "TAB清屏", "/输出顿号",
            // Candidate window
            "字体", "字体大小", "竖排候选", "每页候选个数", "显示候选序号",
            "候选窗显示编码", "编码伪装", "显示注释", "显示拆分",
            "延时展开注释和拆分(毫秒)", "隐藏候选", "延时显示候选(毫秒)", "候选窗动效",
            "上屏后候选窗驻留时间(毫秒)",
            // Sentence
            "自动启用整句模式", "整句神经重排", "允许单字重码组句", "高频字仅使用最优码组句",
            "整句允许全码组句白名单", "整句自动提前上屏", "整句Tab自学习", "保留最少编码数量",
            // Native hook
            "Alt+\\启用或禁用外挂版", "使用剪贴板上屏", "使用剪贴板上屏白名单"
        };

        private static readonly Dictionary<string, int> Ranks = BuildRanks();

        private static Dictionary<string, int> BuildRanks()
        {
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Keys.Length; i++)
            {
                ranks.Add(Keys[i], i);
            }
            return ranks;
        }

        internal static int GetRank(string key)
        {
            return Ranks.TryGetValue(key, out int rank) ? rank : int.MaxValue;
        }
    }
}
