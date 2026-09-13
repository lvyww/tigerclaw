using System;
using System.Collections.Generic;
using System.Reflection;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal static partial class Program
    {
        private static void TemporaryPinyinAlwaysShowsReverseLookupAnnotations()
        {
            var state = new CoreRuntimeState();
            state.TrySetConfigValue("显示注释", "否", out _, out _);
            state.TrySetConfigValue("显示拆分", "否", out _, out _);
            var splits = (Dictionary<string, string>)typeof(CoreRuntimeState)
                .GetField("_splitMap", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
            var codes = (Dictionary<string, string>)typeof(CoreRuntimeState)
                .GetField("_fullCodeMap", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
            var comments = (Dictionary<string, string>)typeof(CoreRuntimeState)
                .GetField("_commentMap", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
            splits["明"] = "日月";
            codes["明"] = "ab";
            comments["明"] = "明亮";
            codes["甲"] = "cd";
            splits["乙"] = "乙根";
            comments["丙"] = "注释";

            Equal("日月 | ab | 明亮", state.GetCandidateAnnotation("明", true), "pinyin_hint.all");
            Equal("cd", state.GetCandidateAnnotation("甲", true), "pinyin_hint.code_without_split");
            Equal("乙根", state.GetCandidateAnnotation("乙", true), "pinyin_hint.split_without_code");
            Equal("注释", state.GetCandidateAnnotation("丙", true), "pinyin_hint.comment_only");
            Equal("", state.GetCandidateAnnotation("丁", true), "pinyin_hint.missing");
            Equal("", state.GetCandidateAnnotation("明", false), "pinyin_hint.normal_respects_settings");
            True(!state.GetShowComment() && !state.GetShowSplit(), "pinyin_hint.settings_unchanged");
        }
    }
}
