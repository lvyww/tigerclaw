using System;
using System.Linq;
using TigerClaw.Dialog;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void SettingsOrderIsIndependentOfConfigurationOrder()
        {
            string[][] sections =
            {
                new[] { "开机自动启动", "默认中文", "主题", "隐藏状态栏", "开启打字音效(娱乐)", "按键音量0~100" },
                new[] { "当前码表", "码表存储位置", "最大码长", "最大码长无重自动上屏", "空码自动清屏" },
                new[] { "shift切换中英文", "切换最近码表快捷键", "手动加词快捷键", "`键拼音反查", "翻页键", "自定义选重键", "分号次选", "引号三选", "回车清屏", "TAB清屏" },
                new[] { "字体", "字体大小", "每页候选个数", "显示注释", "显示拆分", "延时展开注释和拆分(毫秒)", "隐藏候选", "延时显示候选(毫秒)" },
                new[] { "自动启用整句模式", "整句神经重排", "允许单字重码组句", "高频字仅使用最优码组句", "整句允许全码组句白名单", "整句自动提前上屏", "保留最少编码数量" },
                new[] { "Alt+\\启用或禁用外挂版", "使用剪贴板上屏", "使用剪贴板上屏白名单" }
            };
            foreach (string[] section in sections)
            {
                string[] input = section.Reverse().Concat(new[] { "Z-extension", "a-extension" }).ToArray();
                string[] expected = section.Concat(new[] { "a-extension", "Z-extension" }).ToArray();
                string[] sorted = input.OrderBy(ConfigSettingOrder.GetRank)
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
                True(expected.SequenceEqual(sorted), "settings_order.shuffled:" + section[0]);
                True(sorted.SequenceEqual(sorted.OrderBy(ConfigSettingOrder.GetRank)
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)), "settings_order.idempotent");
            }
            True(ConfigSettingOrder.GetRank("SHIFT切换中英文") == ConfigSettingOrder.GetRank("shift切换中英文"),
                "settings_order.case_insensitive");
        }
    }
}
