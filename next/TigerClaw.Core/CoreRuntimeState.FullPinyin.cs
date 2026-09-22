using System;
using System.IO;
using System.Text.Json;

namespace TigerClaw.Core
{
    internal sealed partial class CoreRuntimeState
    {
        private int _pinyinDescriptorVersion = -1;
        private string _pinyinDirectory;
        private bool _pinyinDouble;
        internal string GetFullPinyinDirectory()
        {
            lock (_lock)
            {
                if (_pinyinDescriptorVersion == ConfigVersion) return _pinyinDirectory;
                _pinyinDescriptorVersion = ConfigVersion;
                _pinyinDirectory = null; _pinyinDouble = false;
                string directory = GetCurrentCodeTablePath();
                string path = Path.Combine(directory ?? "", "schema.json");
                if (!File.Exists(path)) return null;
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(path));
                    if (json.RootElement.TryGetProperty("engine", out var engine) && engine.GetString() == "full_pinyin")
                    {
                        _pinyinDirectory = directory;
                        _pinyinDouble = json.RootElement.TryGetProperty("layout", out var layout) && layout.GetString() == "xiaohe";
                    }
                }
                catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    // A present but unreadable descriptor must not silently turn
                    // its raw pinyin into ordinary shape-code auto commits.
                    _pinyinDirectory = directory;
                }
                return _pinyinDirectory;
            }
        }
        internal bool IsFullPinyinActive() => GetFullPinyinDirectory() != null;
        internal TigerClaw.Pinyin.PinyinSpellingOptions GetPinyinSpellingOptions()
        {
            GetFullPinyinDirectory();
            if (_pinyinDouble) return TigerClaw.Pinyin.PinyinSpellingOptions.DoublePinyin;
            var options = TigerClaw.Pinyin.PinyinSpellingOptions.None;
            var names = new[] { "全拼简拼", "全拼拼写兼容", "全拼错拼纠正", "全拼模糊音n-l", "全拼模糊音z-zh", "全拼模糊音c-ch", "全拼模糊音s-sh", "全拼模糊音en-eng", "全拼模糊音in-ing", "全拼模糊音an-ang" };
            for (int i = 0; i < names.Length; i++) if (GetBool(names[i], i < 2)) options |= (TigerClaw.Pinyin.PinyinSpellingOptions)(1 << i);
            return options;
        }
        internal bool GetPinyinEnglishEnabled() => GetBool("拼音英文候选", true);
        internal bool GetPinyinEmojiEnabled() => GetBool("拼音表情候选", true);
        internal bool GetPinyinTraditionalEnabled() => GetBool("拼音繁体输出", false);
        internal bool GetFullPinyinLearningEnabled() => GetBool("全拼纠正学习", true);
    }
}
