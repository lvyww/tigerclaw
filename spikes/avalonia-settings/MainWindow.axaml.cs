using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TigerClawSettingsSpike;

public partial class MainWindow : Window
{
    private readonly TigerClawHostCli _host = new();
    private List<SchemaItem> _schemas = [];
    private List<UserDictionaryEntry> _userEntries = [];

    public MainWindow()
    {
        InitializeComponent();
        CandidateThemeComboBox.ItemsSource = CandidateThemeNames;
        Opened += async (_, _) => await RefreshAsync();
        Activated += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshUserDictionary_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RefreshAsync();
        SetStatus($"用户词已刷新：{_userEntries.Count} 条。");
    }


    private void SchemaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is SchemaItem schema)
        {
            SchemaNameInput.Text = schema.DisplayName;
            DeleteSchemaConfirmationCheckBox.IsChecked = false;
        }
    }

    private async void ImportSchema_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 TigerClaw 方案（支持 ZIP 与 Rime 词库依赖）",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("TigerClaw 方案") { Patterns = ["*.zip", "*.txt", "*.dict.yaml"] }],
            });
            if (files.Count == 0)
            {
                return;
            }

            SetStatus("正在导入码表…");
            await Task.Run(() => _host.ImportSchema(files[0].Path.LocalPath));
            SetStatus("码表已导入；请从“输入与方案”选择后保存。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"导入失败：{ex.Message}");
        }
    }

    private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在保存配置…");
            SchemaItem? schema = SchemaComboBox.SelectedItem as SchemaItem;
            if (schema is null) throw new InvalidOperationException("请选择输入方案。");

            int maxCandidates = NumericValue(CandidateCountInput, "候选总数");
            int pageSize = NumericValue(PageSizeInput, "每页候选数");
            int maxCodeLength = NumericValue(MaxCodeLengthInput, "最大码长");
            int candidateFontSize = NumericValue(CandidateFontSizeInput, "候选字体大小");
            int candidateExpandDelay = NumericValue(CandidateExpandDelayInput, "延时显示候选");
            int annotationExpandDelay = NumericValue(AnnotationExpandDelayInput, "延时展开注释和拆分");
            int keySoundVolume = NumericValue(KeySoundVolumeInput, "按键音量");
            int sentenceHighFrequencyLimit = NumericValue(SentenceHighFrequencyLimitInput, "高频字最优码范围");
            bool unlimitedMixedInput = MixedInputToggle.IsChecked == true;
            string autoCommitUnique = ToggleValue(AutoCommitUniqueToggle);
            string secondCandidateSemicolon = ToggleValue(SecondCandidateSemicolonToggle);
            string thirdCandidateQuote = ToggleValue(ThirdCandidateQuoteToggle);
            string tabClearsComposition = ToggleValue(TabClearsCompositionToggle);
            string enterClearsComposition = ToggleValue(EnterClearsCompositionToggle);
            string previousPageKeys = TextValue(PreviousPageKeysInput, "上一页键");
            string nextPageKeys = TextValue(NextPageKeysInput, "下一页键");
            string useEnglishPunctuation = ToggleValue(UseEnglishPunctuationToggle);
            string slashOutputsDunhao = ToggleValue(SlashOutputsDunhaoToggle);
            string verticalCandidates = ToggleValue(VerticalCandidatesToggle);
            string showCandidateIndex = ToggleValue(ShowCandidateIndexToggle);
            string showInputCode = ToggleValue(ShowInputCodeToggle);
            string hideCandidates = ToggleValue(HideCandidatesToggle);
            string candidateFontName = CandidateFontNameInput.Text ?? string.Empty;
            string candidateTheme = CandidateThemeValue();
            string candidateWindowAnimation = ToggleValue(CandidateWindowAnimationToggle);
            string showCandidateComment = ToggleValue(ShowCandidateCommentToggle);
            string showCandidateSplit = ToggleValue(ShowCandidateSplitToggle);
            string keySoundEnabled = ToggleValue(KeySoundEnabledToggle);
            string codeMasking = CodeMaskingInput.Text ?? string.Empty;
            string defaultChinese = ToggleValue(DefaultChineseToggle);
            string ctrlSpaceTogglesInputMode = ToggleValue(CtrlSpaceTogglesInputModeToggle);
            string ctrlEqualAddsWord = ToggleValue(CtrlEqualAddsWordToggle);
            string capsLockTogglesInputMode = ToggleValue(CapsLockTogglesInputModeToggle);
            string ctrlMTogglesRecentSchema = ToggleValue(CtrlMTogglesRecentSchemaToggle);
            string pinyinReverseEnabled = ToggleValue(PinyinReverseEnabledToggle);
            string sentenceInputEnabled = ToggleValue(SentenceInputToggle);
            string autoSentenceInput = ToggleValue(AutoSentenceInputToggle);
            string sentenceNeuralRerank = ToggleValue(SentenceNeuralRerankToggle);
            string sentenceAutoCommit = ToggleValue(SentenceAutoCommitToggle);
            string sentenceFullCodeWhitelist = SentenceFullCodeWhitelistInput.Text?.Trim() ?? string.Empty;
            string sentenceAllowDuplicateSingleCharacters = ToggleValue(SentenceAllowDuplicateSingleCharactersToggle);
            string clearOnNoCode = ToggleValue(ClearOnNoCodeToggle);

            await Task.Run(() =>
            {
                _host.SelectSchema(schema.Identifier);
                _host.SetConfiguration("max-candidates", maxCandidates.ToString());
                _host.SetConfiguration("page-size", pageSize.ToString());
                _host.SetConfiguration("max-code-length", maxCodeLength.ToString());
                _host.SetConfiguration("unlimited-mixed-input", unlimitedMixedInput ? "on" : "off");
                _host.SetConfiguration("auto-commit-unique", autoCommitUnique);
                _host.SetConfiguration("second-candidate-semicolon", secondCandidateSemicolon);
                _host.SetConfiguration("third-candidate-quote", thirdCandidateQuote);
                _host.SetConfiguration("tab-clears-composition", tabClearsComposition);
                _host.SetConfiguration("enter-clears-composition", enterClearsComposition);
                _host.SetConfiguration("previous-page-keys", previousPageKeys);
                _host.SetConfiguration("next-page-keys", nextPageKeys);
                _host.SetConfiguration("use-english-punctuation-in-chinese", useEnglishPunctuation);
                _host.SetConfiguration("slash-outputs-dunhao", slashOutputsDunhao);
                _host.SetConfiguration("vertical-candidates", verticalCandidates);
                _host.SetConfiguration("show-candidate-index", showCandidateIndex);
                _host.SetConfiguration("show-input-code", showInputCode);
                _host.SetConfiguration("hide-candidates", hideCandidates);
                _host.SetConfiguration("candidate-font-size", candidateFontSize.ToString());
                _host.SetConfiguration("candidate-font-name", candidateFontName);
                _host.SetConfiguration("candidate-theme", candidateTheme);
                _host.SetConfiguration("candidate-window-animation", candidateWindowAnimation);
                _host.SetConfiguration("show-candidate-comment", showCandidateComment);
                _host.SetConfiguration("show-candidate-split", showCandidateSplit);
                _host.SetConfiguration("candidate-expand-delay-ms", candidateExpandDelay.ToString());
                _host.SetConfiguration("annotation-expand-delay-ms", annotationExpandDelay.ToString());
                _host.SetConfiguration("key-sound-enabled", keySoundEnabled);
                _host.SetConfiguration("key-sound-volume", keySoundVolume.ToString());
                _host.SetConfiguration("code-masking", codeMasking);
                _host.SetConfiguration("default-chinese", defaultChinese);
                _host.SetConfiguration("ctrl-space-toggles-input-mode", ctrlSpaceTogglesInputMode);
                _host.SetConfiguration("ctrl-equal-adds-word", ctrlEqualAddsWord);
                _host.SetConfiguration("caps-lock-toggles-input-mode", capsLockTogglesInputMode);
                _host.SetConfiguration("ctrl-m-toggles-recent-schema", ctrlMTogglesRecentSchema);
                _host.SetConfiguration("pinyin-reverse-enabled", pinyinReverseEnabled);
                _host.SetConfiguration("sentence-input-enabled", sentenceInputEnabled);
                _host.SetConfiguration("auto-sentence-input", autoSentenceInput);
                _host.SetConfiguration("sentence-neural-rerank-enabled", sentenceNeuralRerank);
                _host.SetConfiguration("sentence-auto-commit-enabled", sentenceAutoCommit);
                _host.SetConfiguration("sentence-optimal-code-high-frequency-limit", sentenceHighFrequencyLimit.ToString());
                _host.SetConfiguration("sentence-full-code-whitelist", sentenceFullCodeWhitelist);
                _host.SetConfiguration("sentence-allow-duplicate-single-characters", sentenceAllowDuplicateSingleCharacters);
                _host.SetConfiguration("clear-on-no-code", clearOnNoCode);
            });
            SetStatus("已保存；输入法会在下一次按键前自动重载新设置。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private async void AddUserEntry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string code = UserCodeInput.Text?.Trim() ?? string.Empty;
            string text = UserTextInput.Text?.Trim() ?? string.Empty;
            await Task.Run(() => _host.AddUserEntry(code, text));
            UserCodeInput.Text = string.Empty;
            UserTextInput.Text = string.Empty;
            SetStatus("用户词已添加；输入法会在下一次按键前自动重载。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"添加失败：{ex.Message}");
        }
    }

    private async void OpenSelectionKeyConfiguration_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new SelectionKeyWindow();
        await window.ShowDialog(this);
        SetStatus("选词键配置已关闭；保存后的修改会在下一次按键前自动重载。");
    }

    private async void RemoveUserEntry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (UserDictionaryList.SelectedItem is not UserDictionaryEntry entry)
        {
            SetStatus("请先选择要删除的用户词。");
            return;
        }

        try
        {
            await Task.Run(() => _host.RemoveUserEntry(entry.Code, entry.Text));
            SetStatus("用户词已删除；输入法会在下一次按键前自动重载。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败：{ex.Message}");
        }
    }

    private async void RenameSchema_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is not SchemaItem schema)
        {
            SetStatus("请先选择要重命名的方案。");
            return;
        }
        if (schema.IsBundled)
        {
            SetStatus("内置方案不能重命名。");
            return;
        }

        try
        {
            string displayName = SchemaNameInput.Text?.Trim() ?? string.Empty;
            await Task.Run(() => _host.RenameSchema(schema.Identifier, displayName));
            SetStatus("方案已重命名。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"重命名失败：{ex.Message}");
        }
    }

    private async void OpenSchemaDirectory_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is not SchemaItem schema)
        {
            SetStatus("请先选择要打开的方案。");
            return;
        }
        if (!schema.IsEditable)
        {
            SetStatus("内置方案随应用签名保护，不能直接编辑；请先导入一份副本。");
            return;
        }

        try
        {
            string path = await Task.Run(() => _host.SchemaDirectoryPath(schema.Identifier));
            var startInfo = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            SetStatus("已打开方案文件夹；保存文件后会在下一次按键前自动重载。");
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开方案文件夹：{ex.Message}");
        }
    }

    private async void RedeploySchema_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is not SchemaItem schema)
        {
            SetStatus("请先选择要重新部署的方案。");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _host.SelectSchema(schema.Identifier);
                _host.RedeploySchema();
            });
            SetStatus("已请求重新部署；回到输入位置后，下一次按键将使用修改后的方案。");
        }
        catch (Exception ex)
        {
            SetStatus($"重新部署失败：{ex.Message}");
        }
    }

    private async void RepairInputSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await Task.Run(_host.RepairInputSourceRegistration);
            SetStatus("输入源注册已检查并修复；若菜单栏尚未刷新，请重新打开输入法菜单。");
        }
        catch (Exception ex)
        {
            SetStatus($"修复输入源注册失败：{ex.Message}");
        }
    }

    private async void ExportSchema_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is not SchemaItem schema)
        {
            SetStatus("请先选择要导出的方案。");
            return;
        }
        if (schema.IsBundled)
        {
            SetStatus("内置方案随输入法安装，不需要单独导出。");
            return;
        }

        IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 TigerClaw 方案",
            SuggestedFileName = schema.DisplayName + ".zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("ZIP 压缩包") { Patterns = ["*.zip"] }],
        });
        if (destination is null)
        {
            return;
        }

        string? archive = null;
        try
        {
            SetStatus("正在导出方案…");
            archive = await Task.Run(() => _host.CreateSchemaExport(schema.Identifier));
            await using FileStream source = File.OpenRead(archive);
            await using Stream target = await destination.OpenWriteAsync();
            await source.CopyToAsync(target);
            SetStatus("方案已导出为 ZIP。");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(archive))
            {
                try { await Task.Run(() => _host.CleanupSchemaExport(archive)); }
                catch { /* Export already completed; cleanup is best effort. */ }
            }
        }
    }

    private async void DeleteSchema_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SchemaComboBox.SelectedItem is not SchemaItem schema)
        {
            SetStatus("请先选择要删除的方案。");
            return;
        }
        if (schema.IsBundled)
        {
            SetStatus("内置方案不能删除。");
            return;
        }
        if (DeleteSchemaConfirmationCheckBox.IsChecked != true)
        {
            SetStatus("删除会移除该方案的码表、快符、用户词和方案设置；请先勾选“确认删除”。");
            return;
        }

        try
        {
            await Task.Run(() => _host.DeleteSchema(schema.Identifier));
            SetStatus("方案已删除；已自动切回内置方案。");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败：{ex.Message}");
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            SetStatus("正在读取输入法设置…");
            SettingsSnapshot snapshot = await Task.Run(_host.ReadSnapshot);
            _schemas = snapshot.Schemas;
            _userEntries = snapshot.UserEntries;
            SchemaComboBox.ItemsSource = _schemas;
            SchemaComboBox.SelectedItem = _schemas.FirstOrDefault(item => item.Identifier == snapshot.SelectedSchema)
                ?? _schemas.FirstOrDefault();
            SchemaNameInput.Text = (SchemaComboBox.SelectedItem as SchemaItem)?.DisplayName ?? string.Empty;
            DeleteSchemaConfirmationCheckBox.IsChecked = false;
            CandidateCountInput.Value = ConfigurationNumber(snapshot.Configuration, "max-candidates", 9);
            PageSizeInput.Value = ConfigurationNumber(snapshot.Configuration, "page-size", 5);
            MaxCodeLengthInput.Value = ConfigurationNumber(snapshot.Configuration, "max-code-length", 4);
            MixedInputToggle.IsChecked = string.Equals(ConfigurationValue(snapshot.Configuration, "unlimited-mixed-input", "off"), "on", StringComparison.OrdinalIgnoreCase);
            SentenceInputToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "sentence-input-enabled", false);
            AutoSentenceInputToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "auto-sentence-input", true);
            SentenceNeuralRerankToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "sentence-neural-rerank-enabled", true);
            SentenceAutoCommitToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "sentence-auto-commit-enabled", false);
            SentenceHighFrequencyLimitInput.Value = ConfigurationNumber(snapshot.Configuration, "sentence-optimal-code-high-frequency-limit", 1500);
            SentenceFullCodeWhitelistInput.Text = ConfigurationValue(snapshot.Configuration, "sentence-full-code-whitelist", "便深候整调脸照病增响剑哪微营修愿密脑续假值弹您球激游模静源副座喝富宣呼检救嘴税探脱误释跳睡减蒙镇域洞湾卖暴输缓熟庭俄韩混词授摆诺稳塔潜硬萧侵懂蒋赞赛胸偷烧墙爆操挑撤筑戴植援凭聚凌梁箭圈惨飘旗牌废缩碎挺晓桥赫凝潮掩拔播艘滚兽隆薄愤漫爹撒佩绕");
            SentenceAllowDuplicateSingleCharactersToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "sentence-allow-duplicate-single-characters", true);
            ClearOnNoCodeToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "clear-on-no-code", true);
            AutoCommitUniqueToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "auto-commit-unique", true);
            SecondCandidateSemicolonToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "second-candidate-semicolon", true);
            ThirdCandidateQuoteToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "third-candidate-quote", true);
            TabClearsCompositionToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "tab-clears-composition", true);
            EnterClearsCompositionToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "enter-clears-composition", false);
            PreviousPageKeysInput.Text = ConfigurationValue(snapshot.Configuration, "previous-page-keys", "-");
            NextPageKeysInput.Text = ConfigurationValue(snapshot.Configuration, "next-page-keys", "=");
            UseEnglishPunctuationToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "use-english-punctuation-in-chinese", false);
            SlashOutputsDunhaoToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "slash-outputs-dunhao", true);
            VerticalCandidatesToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "vertical-candidates", true);
            ShowCandidateIndexToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "show-candidate-index", true);
            ShowInputCodeToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "show-input-code", false);
            HideCandidatesToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "hide-candidates", false);
            CandidateFontSizeInput.Value = ConfigurationNumber(snapshot.Configuration, "candidate-font-size", 17);
            CandidateFontNameInput.Text = ConfigurationValue(snapshot.Configuration, "candidate-font-name", string.Empty);
            CandidateThemeComboBox.SelectedIndex = CandidateThemeIndex(ConfigurationValue(snapshot.Configuration, "candidate-theme", "system"));
            CandidateWindowAnimationToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "candidate-window-animation", true);
            ShowCandidateCommentToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "show-candidate-comment", true);
            ShowCandidateSplitToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "show-candidate-split", false);
            CandidateExpandDelayInput.Value = ConfigurationNumber(snapshot.Configuration, "candidate-expand-delay-ms", 0);
            AnnotationExpandDelayInput.Value = ConfigurationNumber(snapshot.Configuration, "annotation-expand-delay-ms", 0);
            KeySoundEnabledToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "key-sound-enabled", false);
            KeySoundVolumeInput.Value = ConfigurationNumber(snapshot.Configuration, "key-sound-volume", 30);
            CodeMaskingInput.Text = ConfigurationValue(snapshot.Configuration, "code-masking", string.Empty);
            DefaultChineseToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "default-chinese", true);
            CtrlSpaceTogglesInputModeToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "ctrl-space-toggles-input-mode", true);
            CtrlEqualAddsWordToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "ctrl-equal-adds-word", true);
            CapsLockTogglesInputModeToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "caps-lock-toggles-input-mode", true);
            CtrlMTogglesRecentSchemaToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "ctrl-m-toggles-recent-schema", true);
            PinyinReverseEnabledToggle.IsChecked = ConfigurationBoolean(snapshot.Configuration, "pinyin-reverse-enabled", true);
            UserDictionaryList.ItemsSource = _userEntries;
            DiagnosticsText.Text = snapshot.Diagnostics;
            SetStatus($"已连接输入法：{_host.ExecutablePath}");
        }
        catch (Exception ex)
        {
            SetStatus($"无法连接输入法：{ex.Message}");
            DiagnosticsText.Text = ex.ToString();
        }
    }

    private static int NumericValue(NumericUpDown control, string label)
    {
        if (control.Value is null) throw new InvalidOperationException($"{label}不能为空。");
        return Decimal.ToInt32(control.Value.Value);
    }

    private static decimal ConfigurationNumber(IReadOnlyDictionary<string, string> values, string key, decimal fallback)
    {
        return Decimal.TryParse(ConfigurationValue(values, key, string.Empty), out decimal value) ? value : fallback;
    }

    private static string ConfigurationValue(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out string? value) ? value : fallback;
    }

    private static bool ConfigurationBoolean(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        string value = ConfigurationValue(values, key, fallback ? "on" : "off");
        return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               value == "1";
    }

    private string CandidateThemeValue()
    {
        return CandidateThemeComboBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            >= 3 and <= 11 => CandidateThemeNames[CandidateThemeComboBox.SelectedIndex],
            _ => "system",
        };
    }

    private static int CandidateThemeIndex(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => Array.IndexOf(CandidateThemeNames, value) is int index && index >= 3 ? index : 0,
        };
    }

    private static readonly string[] CandidateThemeNames =
    [
        "跟随系统", "浅色", "深色", "默认", "通透", "一般通透", "迷雾", "星夜", "纸", "粉", "赛博朋克", "清晨",
    ];

    private static string TextValue(TextBox control, string label)
    {
        string value = control.Text?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new InvalidOperationException($"{label}不能为空。");
        return value;
    }

    private static string ToggleValue(ToggleSwitch control) => control.IsChecked == true ? "on" : "off";

    private void SetStatus(string message) => StatusText.Text = message;
}
