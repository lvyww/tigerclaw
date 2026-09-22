using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TigerClaw.Pinyin;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        internal string ListPinyinPreferences()
        {
            lock (_lock)
            {
                ConfigureFullPinyin();
                if (_pinyinLoading == null) return "";
                var resources = _pinyinLoading.GetAwaiter().GetResult();
                string Row(string kind, string code, string text) => kind + "\t" + code + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                return string.Join("\n", resources.Preferences.Phrases.Select(x => Row("phrase", x.Code, x.Text))
                    .Concat(resources.Preferences.Pins.Select(x => Row("pin", x.Code, x.Text)))
                    .Concat(PinyinUserWords.Load(resources.Directory).Select(x => Row("word", string.Join(" ", x.Readings), x.Text)))
                    .Concat((_pinyinLearning?.Entries() ?? Array.Empty<SentenceLearningEvent>())
                        .Where(x => (x.Mode == PinyinLearningMode || x.Mode == SentenceLearning.PinyinCharacterMode)).DistinctBy(x => (x.Code, x.Text)).Select(x => Row("learned", x.Code, x.Text))));
            }
        }
        internal bool TryManagePinyin(string action, string code, string text, out string error)
        {
            lock (_lock)
            {
                error = "";
                try
                {
                    ConfigureFullPinyin();
                    if (_pinyinLoading == null) throw new InvalidOperationException("当前不是拼音方案");
                    var resources = _pinyinLoading.GetAwaiter().GetResult();
                    var item = PinyinPreferences.Validate(code, text);
                    if (action == "forget")
                    {
                        var store = _pinyinLearning;
                        if (!store.ForgetAsync(PinyinLearningMode, item.Code, item.Text)) throw new InvalidOperationException("学习队列繁忙，请稍后重试");
                        _ = store.FlushAsync().ContinueWith(_ =>
                        {
                            Action publish = null;
                            lock (_lock)
                            {
                                if (_engineDisposed || store != _pinyinLearning) return;
                                if (_compositionState == CompositionState.FullPinyin)
                                { _pinyinSession.Invalidate(); RebuildFullPinyin(); }
                                publish = _sentenceDecodeCompletedCallback;
                            }
                            publish?.Invoke();
                        }, TaskScheduler.Default);
                    }
                    else
                    {
                        var updated = resources.Preferences.Edit(action, item.Code, item.Text);
                        updated.Save(resources.Directory);
                        resources.ReplacePreferences(updated);
                        if (_compositionState == CompositionState.FullPinyin)
                        { _pinyinSession.Invalidate(); RebuildFullPinyin(); }
                    }
                    return true;
                }
                catch (Exception e) { error = e.Message; return false; }
            }
        }
        private KeyEngineResult ManagePinyinCandidate(string action, int index)
        {
            if (!EnsureFullPinyinCurrent() || index < 0 || index >= _pinyinSession.Choices.Length) return FullPinyinResult();
            var selected = _pinyinSession.Choices[index];
            if (!TryManagePinyin(action, _pinyinSession.Raw[..selected.Consumed], selected.Path.Text, out string error))
                _pinyinError = error;
            return FullPinyinResult();
        }
    }
}
