using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Markup;
using TigerClaw.Shared;

namespace TigerClaw.Dialog
{
    public partial class ConfigWindow : Window
    {
        private const string WindowTitleText = "虎爪输入法设置";
        private const string KeyFont = "字体";
        private const string KeyPageKey = "翻页键";
        private const string KeySelectionKeys = "自定义选重键";
        private const string KeyTheme = "主题";
        private const string KeyCurrentSchema = "当前码表";
        private const string KeyKeySoundVolume = "按键音量0~100";
        private const string Yes = "是";
        private const string No = "否";

        private const string SectionSystem = "system";
        private const string SectionCodeInput = "codeInput";
        private const string SectionSentence = "sentence";
        private const string SectionKeys = "keys";
        private const string SectionCandidate = "candidate";
        private const string SectionNative = "native";
        private const string SectionAbout = "about";
        private const string StatesFileName = "states.json";

        private static readonly string[] ThemeNames =
        {
            "默认",
            "通透",
            "一般通透",
            "迷雾",
            "星夜",
            "纸",
            "粉",
            "赛博朋克",
            "清晨"
        };

        private readonly List<EditorEntry> _dynamicEntries = new List<EditorEntry>();
        private readonly List<FilterEntry> _filterEntries = new List<FilterEntry>();
        private readonly Dictionary<string, string> _original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _activeSection = SectionSystem;
        private bool _frontRaised;
        private bool _loaded;
        private bool _suppressChangeTracking;
        private bool _windowStateRestored;
        private double? _pendingRestoreVerticalOffset;

        public ConfigWindow()
        {
            InitializeComponent();
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            LoadPersistedWindowState();
            BringToFrontOnce();
            LoadConfig(updateStatus: true);
        }

        private void LoadConfig(bool updateStatus)
        {
            if (!CorePipeClient.TryGetConfigText(1500, out string configText, out string error))
            {
                StatusText.Text = "状态：读取配置失败 - " + error;
                return;
            }

            Dictionary<string, string> config = ParseConfigText(configText);

            _suppressChangeTracking = true;
            try
            {
                BuildEditors(config);
                UpdateInlineSummaries();
                UpdateAboutSection();
                ApplySearchFilter();
                RestoreWindowStateAfterInitialLoad();
            }
            finally
            {
                _suppressChangeTracking = false;
            }

            UpdateDirtyState();
            if (updateStatus)
            {
                StatusText.Text = "状态：已读取配置。";
            }
        }

        private void BuildEditors(Dictionary<string, string> config)
        {
            _dynamicEntries.Clear();
            _filterEntries.Clear();
            _original.Clear();

            SystemDynamicPanel.Children.Clear();
            CodeInputDynamicPanel.Children.Clear();
            SentenceDynamicPanel.Children.Clear();
            KeysDynamicPanel.Children.Clear();
            CandidateDynamicPanel.Children.Clear();
            NativeDynamicPanel.Children.Clear();

            foreach (KeyValuePair<string, string> kv in config)
            {
                if (string.Equals(kv.Key, "任务栏显示", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, "最近码表对", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _original[kv.Key] = kv.Value ?? string.Empty;
            }

            SetupFontEditor(config);
            SetupPageKeyEditor(config);
            SetupThemeEditor(config);
            SetupSchemaEditor(config);
            RegisterFixedRows();
            SetupDynamicEditors(config);
        }

        private void RegisterFixedRows()
        {
            RegisterFilterEntry(SectionCandidate, KeyFont, FontRow, DescribeSetting(KeyFont));
            RegisterFilterEntry(SectionSystem, KeyTheme, ThemeRow, DescribeSetting(KeyTheme));
            RegisterFilterEntry(SectionCodeInput, KeyCurrentSchema, SchemaRow, DescribeSetting(KeyCurrentSchema));
            RegisterFilterEntry(SectionKeys, KeyPageKey, PageKeyRow, DescribeSetting(KeyPageKey));
            RegisterFilterEntry(SectionKeys, KeySelectionKeys, SelectionKeysRow, DescribeSetting(KeySelectionKeys));
            RegisterFilterEntry(SectionAbout, "关于", AboutCard, "版本、提交、构建时间和支持入口。");
        }

        private void SetupFontEditor(Dictionary<string, string> config)
        {
            CbFonts.Items.Clear();

            double previewSize = GetConfigDouble(config, "字体大小", 14);
            if (previewSize < 3)
            {
                previewSize = 3;
            }
            else if (previewSize > 200)
            {
                previewSize = 200;
            }

            string fontDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "字体");
            if (Directory.Exists(fontDir))
            {
                CultureInfo zhCn = CultureInfo.GetCultureInfo("zh-CN");
                CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");
                foreach (string file in Directory.GetFiles(fontDir, "*.ttf"))
                {
                    try
                    {
                        var glyph = new GlyphTypeface(new Uri(file, UriKind.Absolute));
                        string fontName = null;
                        if (!glyph.FamilyNames.TryGetValue(zhCn, out fontName))
                        {
                            glyph.FamilyNames.TryGetValue(enUs, out fontName);
                        }

                        if (string.IsNullOrWhiteSpace(fontName))
                        {
                            continue;
                        }

                        string displayName = "#" + fontName;
                        FontFamily family = new FontFamily(new Uri(fontDir + "\\"), "./#" + fontName);
                        AddFontItem(displayName, family, previewSize);
                    }
                    catch
                    {
                    }
                }
            }

            foreach (FontFamily family in Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase))
            {
                string displayName = GetPreferredFontDisplayName(family);
                AddFontItem(displayName, new FontFamily(family.Source), previewSize, family.Source);
            }

            string fontValue = config.TryGetValue(KeyFont, out string raw) ? (raw ?? string.Empty).Trim() : string.Empty;
            if (!string.IsNullOrEmpty(fontValue))
            {
                SelectFontComboByValue(fontValue);
            }

            if (CbFonts.SelectedIndex < 0 && CbFonts.Items.Count > 0)
            {
                CbFonts.SelectedIndex = 0;
            }

            SyncFontComboPreview();
        }

        private void SetupPageKeyEditor(Dictionary<string, string> config)
        {
            string value = config.TryGetValue(KeyPageKey, out string raw) ? (raw ?? string.Empty).Trim() : "- =";
            bool matched = false;
            foreach (RadioButton radio in FindAllRadioButtons(PageKeyRow))
            {
                radio.IsEnabled = true;
                bool isMatch = string.Equals(radio.Content as string, value, StringComparison.Ordinal);
                radio.IsChecked = isMatch;
                matched |= isMatch;
            }

            if (!matched)
            {
                RadioButton first = FindFirstRadioButton(PageKeyRow);
                if (first != null)
                {
                    first.IsChecked = true;
                }
            }
        }

        private void SetupThemeEditor(Dictionary<string, string> config)
        {
            CbTheme.Items.Clear();
            foreach (string theme in ThemeNames)
            {
                CbTheme.Items.Add(new ComboBoxItem { Content = theme });
            }

            string currentTheme = config.TryGetValue(KeyTheme, out string raw) ? (raw ?? string.Empty).Trim() : ThemeNames[0];
            if (!SelectComboByContent(CbTheme, currentTheme))
            {
                CbTheme.Items.Insert(0, new ComboBoxItem { Content = currentTheme });
                SelectComboByContent(CbTheme, currentTheme);
            }

            if (CbTheme.SelectedIndex < 0 && CbTheme.Items.Count > 0)
            {
                CbTheme.SelectedIndex = 0;
            }
        }

        private void SetupSchemaEditor(Dictionary<string, string> config)
        {
            CbSchema.Items.Clear();

            string configured = config.TryGetValue(KeyCurrentSchema, out string c) ? (c ?? string.Empty).Trim() : string.Empty;
            if (!CorePipeClient.TryGetSchemaList(1500, out string[] schemas, out string currentSchema, out _))
            {
                schemas = Array.Empty<string>();
                currentSchema = configured;
            }

            foreach (string schema in schemas)
            {
                if (!string.IsNullOrWhiteSpace(schema))
                {
                    CbSchema.Items.Add(new ComboBoxItem { Content = schema });
                }
            }

            string target = !string.IsNullOrWhiteSpace(currentSchema) ? currentSchema : configured;
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (!SelectComboByContent(CbSchema, target))
                {
                    CbSchema.Items.Insert(0, new ComboBoxItem { Content = target });
                    SelectComboByContent(CbSchema, target);
                }
            }

            if (CbSchema.SelectedIndex < 0 && CbSchema.Items.Count > 0)
            {
                CbSchema.SelectedIndex = 0;
            }
        }

        private void SetupDynamicEditors(Dictionary<string, string> config)
        {
            foreach (KeyValuePair<string, string> kv in config)
            {
                if (string.Equals(kv.Key, KeyFont, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, KeyPageKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, KeyTheme, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, KeyCurrentSchema, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, "任务栏显示", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, "最近码表对", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string description = DescribeSetting(kv.Key);
                string section = ResolveSection(kv.Key);
                FrameworkElement editor = CreateDynamicEditor(kv.Key, kv.Value);
                Border container = CreateDynamicRow(kv.Key, description, editor);

                GetSectionPanel(section).Children.Add(container);
                _dynamicEntries.Add(new EditorEntry(kv.Key, editor));
                RegisterFilterEntry(section, kv.Key, container, description);
            }
        }
        private FrameworkElement CreateDynamicEditor(string key, string value)
        {
            if (string.Equals(value, Yes, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, No, StringComparison.OrdinalIgnoreCase))
            {
                var checkBox = new CheckBox
                {
                    IsChecked = string.Equals(value, Yes, StringComparison.OrdinalIgnoreCase),
                    Content = "启用",
                    Foreground = Foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                checkBox.Checked += OnAnyEditorChanged;
                checkBox.Unchecked += OnAnyEditorChanged;
                return checkBox;
            }

            if (string.Equals(key, KeyKeySoundVolume, StringComparison.OrdinalIgnoreCase))
            {
                int volume = 30;
                if (!int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out volume))
                {
                    volume = 30;
                }

                if (volume < 0)
                {
                    volume = 0;
                }
                else if (volume > 100)
                {
                    volume = 100;
                }

                var valueText = new TextBlock
                {
                    Width = 44,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Foreground,
                    Text = volume.ToString(CultureInfo.InvariantCulture)
                };

                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    TickFrequency = 5,
                    IsSnapToTickEnabled = false,
                    SmallChange = 1,
                    LargeChange = 10,
                    Width = 260,
                    Value = volume,
                    VerticalAlignment = VerticalAlignment.Center
                };
                slider.ValueChanged += (sender, args) =>
                {
                    valueText.Text = ((int)Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture);
                    OnAnyEditorChanged(sender, args);
                };

                var host = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = slider
                };
                host.Children.Add(slider);
                host.Children.Add(valueText);
                return host;
            }

            if (string.Equals(key, "码表存储位置", StringComparison.Ordinal))
            {
                var pathTextBox = new TextBox
                {
                    Text = value ?? string.Empty,
                    Style = (Style)FindResource("FieldTextStyle"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 180,
                    MaxWidth = double.PositiveInfinity
                };
                pathTextBox.TextChanged += OnAnyEditorChanged;

                var button = new Button
                {
                    Content = "打开",
                    Width = 72,
                    Height = 28,
                    Margin = new Thickness(8, 0, 0, 0),
                    Style = (Style)FindResource("ActionButtonStyle")
                };
                button.Click += (sender, args) => OpenFolderPath(pathTextBox.Text);

                var grid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = pathTextBox
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(pathTextBox, 0);
                grid.Children.Add(pathTextBox);
                Grid.SetColumn(button, 1);
                grid.Children.Add(button);
                return grid;
            }

            var textBox = new TextBox
            {
                Text = value ?? string.Empty,
                Style = (Style)FindResource("FieldTextStyle"),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (LooksNumeric(value))
            {
                textBox.MinWidth = 120;
                textBox.MaxWidth = 180;
            }

            if (LooksLikePath(key))
            {
                textBox.MinWidth = 280;
                textBox.MaxWidth = 1200;
                textBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            textBox.TextChanged += OnAnyEditorChanged;
            return textBox;
        }

        private Border CreateDynamicRow(string key, string description, FrameworkElement editor)
        {
            var border = new Border
            {
                Style = (Style)FindResource("SettingRowStyle")
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftPanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 14, 0)
            };
            leftPanel.Children.Add(new TextBlock
            {
                Text = key,
                Style = (Style)FindResource("RowTitleStyle")
            });
            leftPanel.Children.Add(new TextBlock
            {
                Text = description,
                Style = (Style)FindResource("RowDescriptionStyle")
            });

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            border.Child = grid;
            return border;
        }

        private Panel GetSectionPanel(string section)
        {
            switch (section)
            {
                case SectionSystem:
                    return SystemDynamicPanel;
                case SectionCodeInput:
                    return CodeInputDynamicPanel;
                case SectionSentence:
                    return SentenceDynamicPanel;
                case SectionKeys:
                    return KeysDynamicPanel;
                case SectionCandidate:
                    return CandidateDynamicPanel;
                case SectionNative:
                    return NativeDynamicPanel;
                default:
                    return CandidateDynamicPanel;
            }
        }

        private void RegisterFilterEntry(string section, string key, FrameworkElement container, string description)
        {
            string searchText = NormalizeText(key + " " + (description ?? string.Empty));
            _filterEntries.Add(new FilterEntry(section, container, searchText));
        }

        private void ApplySearchFilter()
        {
            string query = NormalizeText(SearchBox.Text);
            int systemVisible = 0;
            int codeInputVisible = 0;
            int sentenceVisible = 0;
            int keysVisible = 0;
            int candidateVisible = 0;
            int nativeVisible = 0;
            int aboutVisible = 0;

            foreach (FilterEntry entry in _filterEntries)
            {
                bool visible = query.Length == 0 || entry.SearchText.Contains(query);
                entry.Container.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible)
                {
                    continue;
                }

                switch (entry.Section)
                {
                    case SectionSystem:
                        systemVisible++;
                        break;
                    case SectionCodeInput:
                        codeInputVisible++;
                        break;
                    case SectionSentence:
                        sentenceVisible++;
                        break;
                    case SectionKeys:
                        keysVisible++;
                        break;
                    case SectionCandidate:
                        candidateVisible++;
                        break;
                    case SectionNative:
                        nativeVisible++;
                        break;
                    case SectionAbout:
                        aboutVisible++;
                        break;
                }
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [SectionSystem] = systemVisible,
                [SectionCodeInput] = codeInputVisible,
                [SectionSentence] = sentenceVisible,
                [SectionKeys] = keysVisible,
                [SectionCandidate] = candidateVisible,
                [SectionNative] = nativeVisible,
                [SectionAbout] = aboutVisible
            };

            if (query.Length > 0 && counts[_activeSection] == 0)
            {
                _activeSection = counts.FirstOrDefault(kv => kv.Value > 0).Key ?? SectionSystem;
            }

            bool hasAnyResult = counts.Values.Any(value => value > 0);
            SetSectionVisibility(SystemSection, SectionSystem, counts, query);
            SetSectionVisibility(CodeInputSection, SectionCodeInput, counts, query);
            SetSectionVisibility(SentenceSection, SectionSentence, counts, query);
            SetSectionVisibility(KeysSection, SectionKeys, counts, query);
            SetSectionVisibility(CandidateSection, SectionCandidate, counts, query);
            SetSectionVisibility(NativeSection, SectionNative, counts, query);
            SetSectionVisibility(AboutSection, SectionAbout, counts, query);
            EmptySearchText.Visibility = hasAnyResult ? Visibility.Collapsed : Visibility.Visible;
            UpdateNavigationState(counts, query.Length > 0);
        }

        private void UpdateInlineSummaries()
        {
            SyncFontComboPreview();
        }

        private void UpdateDirtyState()
        {
            if (!_loaded)
            {
                Save.IsEnabled = false;
                return;
            }

            bool dirty = HasUnsavedChanges(ignoreEmptyTextChanges: false);
            Save.IsEnabled = dirty;
            DirtyHintText.Text = dirty ? "有未保存更改" : "所有更改已保存";
            DirtyHintText.Foreground = dirty
                ? new SolidColorBrush(Color.FromRgb(217, 106, 27))
                : new SolidColorBrush(Color.FromRgb(125, 107, 93));
            Title = dirty ? WindowTitleText + " · 未保存" : WindowTitleText;
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchFilter();
        }

        private void OnSectionNavClick(object sender, RoutedEventArgs e)
        {
            if (sender == SystemNavButton)
            {
                _activeSection = SectionSystem;
            }
            else if (sender == CodeInputNavButton)
            {
                _activeSection = SectionCodeInput;
            }
            else if (sender == SentenceNavButton)
            {
                _activeSection = SectionSentence;
            }
            else if (sender == KeysNavButton)
            {
                _activeSection = SectionKeys;
            }
            else if (sender == CandidateNavButton)
            {
                _activeSection = SectionCandidate;
            }
            else if (sender == NativeNavButton)
            {
                _activeSection = SectionNative;
            }
            else if (sender == AboutNavButton)
            {
                _activeSection = SectionAbout;
            }

            ApplySearchFilter();
        }

        private void OnAnyEditorChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressChangeTracking)
            {
                return;
            }

            UpdateInlineSummaries();
            UpdateDirtyState();
        }

        private void SyncFontComboPreview()
        {
            ComboBoxItem selected = CbFonts.SelectedItem as ComboBoxItem;
            if (selected == null)
            {
                return;
            }

            if (selected.FontFamily != null)
            {
                CbFonts.FontFamily = selected.FontFamily;
            }

            if (selected.FontSize > 0)
            {
                CbFonts.FontSize = selected.FontSize;
            }
        }

        private void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (HasUnsavedChanges(ignoreEmptyTextChanges: false))
            {
                MessageBoxResult result = MessageBox.Show(
                    "重新载入会丢弃当前未保存的修改，是否继续？",
                    WindowTitleText,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            LoadConfig(updateStatus: false);
            StatusText.Text = "状态：已重新载入配置。";
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            _ = TrySaveChanges(showNoChangeStatus: true, ignoreEmptyTextChanges: false, out _);
            UpdateDirtyState();
        }

        private bool TrySaveChanges(bool showNoChangeStatus, bool ignoreEmptyTextChanges, out bool changedAny)
        {
            Dictionary<string, string> current = CollectCurrentValues();
            List<KeyValuePair<string, string>> changedPairs = new List<KeyValuePair<string, string>>();

            foreach (KeyValuePair<string, string> kv in current)
            {
                string oldValue = _original.TryGetValue(kv.Key, out string old) ? old ?? string.Empty : string.Empty;
                string newValue = kv.Value ?? string.Empty;
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    if (ignoreEmptyTextChanges && newValue.Length == 0)
                    {
                        continue;
                    }

                    changedPairs.Add(new KeyValuePair<string, string>(kv.Key, newValue));
                }
            }

            changedAny = changedPairs.Count > 0;
            if (changedPairs.Count == 0)
            {
                if (showNoChangeStatus)
                {
                    StatusText.Text = "状态：没有改动。";
                }

                return true;
            }

            foreach (KeyValuePair<string, string> kv in changedPairs)
            {
                if (!CorePipeClient.TrySetConfigValue(kv.Key, kv.Value, 2000, out _, out string setError))
                {
                    StatusText.Text = "状态：保存失败 - " + kv.Key + " - " + setError;
                    return false;
                }
            }

            if (!CorePipeClient.TrySendReloadConfig(2500, out string reloadError))
            {
                StatusText.Text = "状态：配置已写入，但重载失败 - " + reloadError;
                return false;
            }

            foreach (KeyValuePair<string, string> kv in changedPairs)
            {
                _original[kv.Key] = kv.Value ?? string.Empty;
            }

            UpdateInlineSummaries();
            StatusText.Text = "状态：已保存并重载。";
            return true;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UpdateAboutSection()
        {
            AboutProductText.Text = RuntimeConstants.ProductName;
            AboutVersionText.Text = string.IsNullOrWhiteSpace(BuildInfo.VersionLabel) ? "dev" : BuildInfo.VersionLabel;
            AboutCommitText.Text = string.IsNullOrWhiteSpace(BuildInfo.Commit) ? "dev" : BuildInfo.Commit;
            AboutBuildTimeText.Text = FormatUtcText(BuildInfo.BuildUtc);

            string trialText = FormatUtcText(BuildInfo.TrialExpireUtc);
            bool hasTrial = !string.IsNullOrWhiteSpace(trialText);
            AboutTrialLabel.Visibility = hasTrial ? Visibility.Visible : Visibility.Collapsed;
            AboutTrialText.Visibility = hasTrial ? Visibility.Visible : Visibility.Collapsed;
            AboutTrialText.Text = trialText;
        }

        private void OnAboutCopyClick(object sender, RoutedEventArgs e)
        {
            string summary = BuildAboutSummary();
            try
            {
                Clipboard.SetText(summary);
                StatusText.Text = "状态：已复制版本信息。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "状态：复制失败 - " + ex.Message;
            }
        }

        private void OnAboutOfficialClick(object sender, RoutedEventArgs e)
        {
            if (CorePipeClient.TryOpenOfficial(1500, out string error))
            {
                StatusText.Text = "状态：已打开 Github 页面。";
                return;
            }

            StatusText.Text = "状态：打开 Github 页面失败 - " + error;
        }

        private void OpenFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "状态：码表目录为空。";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                StatusText.Text = "状态：已打开码表目录。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "状态：打开码表目录失败 - " + ex.Message;
            }
        }

        private void OnSelectionKeysClick(object sender, RoutedEventArgs e)
        {
            var window = new SelectionKeyWindow
            {
                Owner = this
            };
            window.ShowDialog();
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!HasUnsavedChanges(ignoreEmptyTextChanges: true))
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "设置已修改，是否先保存？",
                "保存设置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                return;
            }

            if (!TrySaveChanges(showNoChangeStatus: false, ignoreEmptyTextChanges: true, out _))
            {
                e.Cancel = true;
                UpdateDirtyState();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SavePersistedWindowState();
            base.OnClosed(e);
        }
        private bool HasUnsavedChanges(bool ignoreEmptyTextChanges)
        {
            Dictionary<string, string> current = CollectCurrentValues();
            foreach (KeyValuePair<string, string> kv in current)
            {
                string oldValue = _original.TryGetValue(kv.Key, out string old) ? old ?? string.Empty : string.Empty;
                string newValue = kv.Value ?? string.Empty;
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    if (ignoreEmptyTextChanges && newValue.Length == 0)
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        private Dictionary<string, string> CollectCurrentValues()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (CbFonts.SelectedItem is ComboBoxItem fontItem)
            {
                map[KeyFont] = Convert.ToString(fontItem.Content) ?? string.Empty;
            }
            else if (_original.TryGetValue(KeyFont, out string oldFont))
            {
                map[KeyFont] = oldFont ?? string.Empty;
            }

            map[KeyPageKey] = GetSelectedPageKey();
            map[KeyTheme] = (CbTheme.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? (_original.TryGetValue(KeyTheme, out string oldTheme) ? oldTheme : string.Empty);
            map[KeyCurrentSchema] = (CbSchema.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? (_original.TryGetValue(KeyCurrentSchema, out string oldSchema) ? oldSchema : string.Empty);

            foreach (EditorEntry entry in _dynamicEntries)
            {
                if (entry.Editor is CheckBox cb)
                {
                    map[entry.Key] = cb.IsChecked == true ? Yes : No;
                }
                else if (entry.Editor.Tag is Slider slider)
                {
                    map[entry.Key] = ((int)Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture);
                }
                else if (entry.Editor.Tag is TextBox taggedTextBox)
                {
                    map[entry.Key] = taggedTextBox.Text ?? string.Empty;
                }
                else if (entry.Editor is TextBox tb)
                {
                    map[entry.Key] = tb.Text ?? string.Empty;
                }
            }

            return map;
        }

        private string GetSelectedPageKey()
        {
            RadioButton selected = FindAllRadioButtons(PageKeyRow).FirstOrDefault(r => r.IsChecked == true);
            return selected?.Content as string ?? "- =";
        }

        private void BringToFrontOnce()
        {
            if (_frontRaised)
            {
                return;
            }

            _frontRaised = true;
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (WindowState == WindowState.Minimized)
                        {
                            WindowState = WindowState.Normal;
                        }

                        Activate();
                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            ForceToForeground(hwnd);
                        }

                        if (IsActive)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    await Task.Delay(80).ConfigureAwait(true);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void ForceToForeground(IntPtr hwnd)
        {
            DialogNativeMethods.ShowWindow(hwnd, DialogNativeMethods.SW_SHOWNORMAL);

            IntPtr foreground = DialogNativeMethods.GetForegroundWindow();
            uint foregroundThread = 0;
            if (foreground != IntPtr.Zero)
            {
                foregroundThread = DialogNativeMethods.GetWindowThreadProcessId(foreground, out _);
            }

            uint currentThread = DialogNativeMethods.GetCurrentThreadId();
            bool attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = DialogNativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            try
            {
                Topmost = true;
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_TOP,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                DialogNativeMethods.BringWindowToTop(hwnd);
                DialogNativeMethods.SetForegroundWindow(hwnd);
                DialogNativeMethods.SetFocus(hwnd);
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                Activate();
                Focus();
            }
            finally
            {
                Topmost = false;
                if (attached)
                {
                    DialogNativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }

        private void AddFontItem(string content, FontFamily family, double size, params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            for (int i = 0; i < CbFonts.Items.Count; i++)
            {
                ComboBoxItem existing = CbFonts.Items[i] as ComboBoxItem;
                if (existing != null && string.Equals(Convert.ToString(existing.Content), content, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            CbFonts.Items.Add(new ComboBoxItem
            {
                Content = content,
                FontFamily = family,
                FontSize = size,
                Tag = aliases
            });
        }

        private bool SelectFontComboByValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (SelectComboByContent(CbFonts, value))
            {
                return true;
            }

            for (int i = 0; i < CbFonts.Items.Count; i++)
            {
                ComboBoxItem item = CbFonts.Items[i] as ComboBoxItem;
                if (item?.Tag is string[] aliases && aliases.Any(alias => string.Equals(alias, value, StringComparison.OrdinalIgnoreCase)))
                {
                    CbFonts.SelectedIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static string GetPreferredFontDisplayName(FontFamily family)
        {
            if (family == null)
            {
                return string.Empty;
            }

            LanguageSpecificStringDictionary names = family.FamilyNames;
            if (names != null)
            {
                if (names.TryGetValue(XmlLanguage.GetLanguage("zh-cn"), out string zhName) && !string.IsNullOrWhiteSpace(zhName))
                {
                    return zhName;
                }

                if (names.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string enName) && !string.IsNullOrWhiteSpace(enName))
                {
                    return enName;
                }
            }

            return family.Source ?? string.Empty;
        }

        private static bool SelectComboByContent(ComboBox comboBox, string value)
        {
            if (comboBox == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                ComboBoxItem item = comboBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(Convert.ToString(item.Content), value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static string DescribeSetting(string key)
        {
            switch (key)
            {
                case "开机自动启动":
                    return "登录 Windows 后自动启动虎爪输入法。";
                case "隐藏状态栏":
                    return "隐藏右下角状态窗，仅保留候选窗。";
                case "码表存储位置":
                    return "码表和自定义资源所在目录。";
                case KeyCurrentSchema:
                    return "切换当前正在使用的码表方案。";
                case KeyTheme:
                    return "控制候选窗的配色风格。";
                case "默认中文":
                    return "启动后默认进入中文输入状态。";
                case "中文状态下使用英文标点":
                    return "中文模式下直接上屏英文标点。";
                case "shift切换中英文":
                    return "允许使用 Shift 切换中英文状态。";
                case "Ctrl+空格切换中英文":
                    return "允许使用 Ctrl+空格 切换中英文状态。";
                case "Ctrl+等号手动加词":
                    return "快捷触发手动加词流程。";
                case "Ctrl+m切换最近码表":
                    return "记录最近使用的两个码表，按 Ctrl+m 在这两个码表之间切换。";
                case "回车清屏":
                    return "回车键是否立即清掉编码串。";
                case "中英文不限长混合输入":
                    return "允许超过最大码长，暂存顶字上屏的候选字词，最后一起上屏。";
                case "整句输入":
                    return "连续输入整句编码，由本地语言模型自动切分并生成整句候选。";
                case "自动启用整句模式":
                    return "方案名含“整句”时，自动启用整句模式。";
                case "整句神经重排":
                    return "使用独立 Qwen 推理进程重排前 5 个整句候选；不可用时自动保留三元模型结果。";
                case "整句自动提前上屏":
                    return "整句模式下高置信度且连续稳定的前缀自动提前上屏；默认关闭。";
                case "整句空码自动顶屏":
                    return "唯一候选或置信度至少 0.99999 的强首选后，输入变为空码且无法继续补全当前码段时，上屏原候选并保留新增编码；默认开启。";
                case "保留最少编码数量":
                    return "自动上屏后至少留在编码里的码数。0 表示不额外限制；大于 0 时，概率提前上屏和空码顶屏都必须留下这么多未上屏编码。";
                case "TAB清屏":
                    return "Tab 键在对应场景下是否直接清屏。整句有候选时，Tab / Shift+Tab 仍遍历整句候选，不会清屏。";
                case "竖排候选":
                    return "候选窗改为竖排布局。";
                case "显示候选序号":
                    return "在候选项前显示序号。";
                case "显示注释":
                    return "在候选旁显示注释文本。";
                case "显示拆分":
                    return "显示拆分或辅助提示信息。";
                case "每页候选个数":
                    return "控制每页显示的候选数量，通常建议 5 到 9。";
                case KeyPageKey:
                    return "配置候选上下翻页所使用的按键。";
                case KeySelectionKeys:
                    return "编辑 1 到 10 选的自定义按键绑定。";
                case "分号次选":
                    return "允许用分号选择第二候选；整句模式下分号仅在开启时写入编码。";
                case "引号三选":
                    return "允许用单引号选择第三候选；整句模式下单引号仅在开启时写入编码。";
                case "/输出顿号":
                    return "配置 / 键是否直接输出顿号。";
                case "隐藏候选":
                    return "隐藏候选窗口，只保留输入中的文字变化。";
                case "候选窗动效":
                    return "控制候选窗弹出时的轻微上浮位移动画。";
                case "编码伪装":
                    return "对展示编码做轻度伪装处理。";
                case "空码自动清屏":
                    return "输入无命中后自动清理编码。";
                case "最大码长":
                    return "限制单次可继续输入的最大码长。";
                case "最大码长无重自动上屏":
                    return "达到最大码长且无重码时自动上屏。";
                case KeyFont:
                    return "设置候选窗口的显示字体。";
                case "字体大小":
                    return "控制候选窗、状态栏的显示尺寸。";
                case "开启打字音效(娱乐)":
                    return "输入时播放按键音效。";
                case "按键音量0~100":
                    return "调整打字音效音量，0 表示静音。";
                case "Alt+\\启用或禁用外挂版":
                    return "使用 Alt+\\ 临时开关外挂版。";
                case "使用剪贴板上屏":
                    return "全局使用剪贴板上屏文字，兼容性更好，但会更改剪贴板内容。";
                case "使用剪贴板上屏白名单":
                    return "指定程序名（进程名）使用剪贴板上屏文字，多个程序间用英文逗号分隔。";
            }

            if (key.IndexOf("拼音反查", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "启用拼音反查或反向辅助输入能力。";
            }

            if (key.IndexOf("候选", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "调整候选展示相关行为。";
            }

            if (key.IndexOf("码表", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "码表读取或切换相关设置。";
            }

            return "修改后保存生效。";
        }
        private static string ResolveSection(string key)
        {
            switch (key)
            {
                case "开机自动启动":
                case KeyTheme:
                case "隐藏状态栏":
                case "默认中文":
                case "中文状态下使用英文标点":
                case "自动切换系统语言":
                case "开启打字音效(娱乐)":
                case KeyKeySoundVolume:
                    return SectionSystem;

                case "码表存储位置":
                case KeyCurrentSchema:
                case "最大码长":
                case "最大码长无重自动上屏":
                case "中英文不限长混合输入":
                case "空码自动清屏":
                    return SectionCodeInput;

                case "整句输入":
                case "自动启用整句模式":
                case "整句神经重排":
                case "整句自动提前上屏":
                case "整句空码自动顶屏":
                case "保留最少编码数量":
                    return SectionSentence;

                case KeySelectionKeys:
                case "shift切换中英文":
                case "Ctrl+空格切换中英文":
                case "Ctrl+等号手动加词":
                case "Ctrl+m切换最近码表":
                case "分号次选":
                case "引号三选":
                case "回车清屏":
                case "TAB清屏":
                case KeyPageKey:
                case "/输出顿号":
                case "`键拼音反查":
                    return SectionKeys;

                case KeyFont:
                case "字体大小":
                case "竖排候选":
                case "每页候选个数":
                case "候选窗显示编码":
                case "显示候选序号":
                case "显示注释":
                case "显示拆分":
                case "隐藏候选":
                case "延时显示候选(毫秒)":
                case "延时展开注释和拆分(毫秒)":
                case "编码伪装":
                    return SectionCandidate;

                case "Alt+\\启用或禁用外挂版":
                case "使用剪贴板上屏":
                case "使用剪贴板上屏白名单":
                    return SectionNative;
            }

            if (ContainsAny(key, "Hook", "外挂", "Native"))
            {
                return SectionNative;
            }

            if (ContainsAny(key, "候选", "注释", "拆分", "字体", "竖排", "显示编码", "伪装"))
            {
                return SectionCandidate;
            }

            if (ContainsAny(key, "整句"))
            {
                return SectionSentence;
            }

            if (ContainsAny(key, "码表", "码长", "空码"))
            {
                return SectionCodeInput;
            }

            if (ContainsAny(key, "翻页", "Ctrl", "shift", "Shift", "TAB", "清屏", "二选", "三选", "顿号", "反查"))
            {
                return SectionKeys;
            }

            return SectionSystem;
        }

        private static string BuildAboutSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine(RuntimeConstants.ProductName);
            sb.Append("版本：");
            sb.AppendLine(string.IsNullOrWhiteSpace(BuildInfo.VersionLabel) ? "dev" : BuildInfo.VersionLabel);
            sb.Append("提交：");
            sb.AppendLine(string.IsNullOrWhiteSpace(BuildInfo.Commit) ? "dev" : BuildInfo.Commit);

            string buildText = FormatUtcText(BuildInfo.BuildUtc);
            if (!string.IsNullOrWhiteSpace(buildText))
            {
                sb.Append("构建时间：");
                sb.AppendLine(buildText);
            }

            string trialText = FormatUtcText(BuildInfo.TrialExpireUtc);
            if (!string.IsNullOrWhiteSpace(trialText))
            {
                sb.Append("试用到期：");
                sb.AppendLine(trialText);
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatUtcText(string utcText)
        {
            if (string.IsNullOrWhiteSpace(utcText))
            {
                return string.Empty;
            }

            if (!DateTime.TryParseExact(
                    utcText,
                    "yyyy-MM-ddTHH:mm:ssZ",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime utc))
            {
                return utcText;
            }

            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void LoadPersistedWindowState()
        {
            try
            {
                string path = GetStatesFilePath();
                if (!File.Exists(path))
                {
                    return;
                }

                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(PersistedUiStateRoot));
                    var root = serializer.ReadObject(stream) as PersistedUiStateRoot;
                    var state = root?.ConfigWindow;
                    if (state == null)
                    {
                        return;
                    }

                    if (IsKnownSection(state.SelectedSection))
                    {
                        _activeSection = state.SelectedSection;
                    }

                    if (state.VerticalOffset > 0)
                    {
                        _pendingRestoreVerticalOffset = state.VerticalOffset;
                    }
                }
            }
            catch
            {
            }
        }

        private void RestoreWindowStateAfterInitialLoad()
        {
            if (_windowStateRestored)
            {
                return;
            }

            _windowStateRestored = true;

            if (!_pendingRestoreVerticalOffset.HasValue)
            {
                return;
            }

            double targetOffset = _pendingRestoreVerticalOffset.Value;
            _pendingRestoreVerticalOffset = null;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    UpdateLayout();
                    ContentScrollViewer.ScrollToVerticalOffset(targetOffset);
                }
                catch
                {
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SavePersistedWindowState()
        {
            try
            {
                string path = GetStatesFilePath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var root = new PersistedUiStateRoot
                {
                    ConfigWindow = new PersistedConfigWindowState
                    {
                        SelectedSection = IsKnownSection(_activeSection) ? _activeSection : SectionSystem,
                        VerticalOffset = ContentScrollViewer?.VerticalOffset ?? 0
                    }
                };

                using (var stream = File.Create(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(PersistedUiStateRoot));
                    serializer.WriteObject(stream, root);
                }
            }
            catch
            {
            }
        }

        private static string GetStatesFilePath()
        {
            return Path.Combine(GetAppDataDirectory(), StatesFileName);
        }

        private static string GetAppDataDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TigerClaw");
        }

        private static bool IsKnownSection(string section)
        {
            return string.Equals(section, SectionSystem, StringComparison.Ordinal) ||
                   string.Equals(section, SectionCodeInput, StringComparison.Ordinal) ||
                   string.Equals(section, SectionSentence, StringComparison.Ordinal) ||
                   string.Equals(section, SectionKeys, StringComparison.Ordinal) ||
                   string.Equals(section, SectionCandidate, StringComparison.Ordinal) ||
                   string.Equals(section, SectionNative, StringComparison.Ordinal) ||
                   string.Equals(section, SectionAbout, StringComparison.Ordinal);
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (string token in tokens)
            {
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksNumeric(string value)
        {
            return double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private static bool LooksLikePath(string key)
        {
            return key.IndexOf("路径", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("位置", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   key.IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeText(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string GetSectionDisplayName(string section)
        {
            switch (section)
            {
                case SectionSystem:
                    return "系统";
                case SectionCodeInput:
                    return "码表及输入行为";
                case SectionSentence:
                    return "整句";
                case SectionKeys:
                    return "按键";
                case SectionCandidate:
                    return "候选窗";
                case SectionNative:
                    return "外挂版";
                case SectionAbout:
                    return "关于";
                default:
                    return "系统";
            }
        }

        private void SetSectionVisibility(FrameworkElement sectionElement, string section, Dictionary<string, int> counts, string query)
        {
            bool visible;
            if (query.Length == 0)
            {
                visible = string.Equals(_activeSection, section, StringComparison.Ordinal);
            }
            else
            {
                visible = string.Equals(_activeSection, section, StringComparison.Ordinal) && counts[section] > 0;
            }

            sectionElement.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateNavigationState(Dictionary<string, int> counts, bool hasQuery)
        {
            UpdateNavigationButton(SystemNavButton, SectionSystem, counts, hasQuery);
            UpdateNavigationButton(CodeInputNavButton, SectionCodeInput, counts, hasQuery);
            UpdateNavigationButton(SentenceNavButton, SectionSentence, counts, hasQuery);
            UpdateNavigationButton(KeysNavButton, SectionKeys, counts, hasQuery);
            UpdateNavigationButton(CandidateNavButton, SectionCandidate, counts, hasQuery);
            UpdateNavigationButton(NativeNavButton, SectionNative, counts, hasQuery);
            UpdateNavigationButton(AboutNavButton, SectionAbout, counts, hasQuery);
        }

        private void UpdateNavigationButton(Button button, string section, Dictionary<string, int> counts, bool hasQuery)
        {
            bool enabled = !hasQuery || counts[section] > 0;
            bool selected = string.Equals(_activeSection, section, StringComparison.Ordinal);
            var activeBrush = (Brush)FindResource("AccentBrushSoft");
            var inactiveBrush = (Brush)FindResource("PanelBrushAlt");
            var activeBorder = (Brush)FindResource("AccentBrush");
            var inactiveBorder = (Brush)FindResource("BorderBrushSoft");
            var activeForeground = Foreground;
            var inactiveForeground = (Brush)FindResource("MutedBrush");
            string title = GetSectionDisplayName(section);

            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1.0 : 0.45;
            button.Background = selected ? activeBrush : inactiveBrush;
            button.BorderBrush = selected ? activeBorder : inactiveBorder;
            button.Foreground = selected ? activeForeground : inactiveForeground;
            button.BorderThickness = selected ? new Thickness(4, 1, 1, 1) : new Thickness(1);
            button.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
            button.Content = hasQuery ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, counts[section]) : title;
        }

        private static string GetSelectedComboContent(ComboBox comboBox)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        }

        private static IEnumerable<RadioButton> FindAllRadioButtons(DependencyObject root)
        {
            if (root == null)
            {
                yield break;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                RadioButton radio = child as RadioButton;
                if (radio != null)
                {
                    yield return radio;
                }

                foreach (RadioButton nested in FindAllRadioButtons(child))
                {
                    yield return nested;
                }
            }
        }

        private static RadioButton FindFirstRadioButton(DependencyObject root)
        {
            return FindAllRadioButtons(root).FirstOrDefault();
        }

        private static double GetConfigDouble(Dictionary<string, string> config, string key, double fallback)
        {
            if (config != null &&
                config.TryGetValue(key, out string raw) &&
                !string.IsNullOrWhiteSpace(raw) &&
                double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static Dictionary<string, string> ParseConfigText(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                return map;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw ?? string.Empty;
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int sep = line.IndexOf('\t');
                if (sep < 0)
                {
                    int whitespaceRun = FindConfigWhitespaceSeparator(line);
                    if (whitespaceRun > 0)
                    {
                        sep = whitespaceRun;
                    }
                }

                if (sep <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, sep).Trim();
                string value = sep + 1 < line.Length ? line.Substring(sep + 1).Trim() : string.Empty;
                if (key.Length == 0)
                {
                    continue;
                }

                map[key] = value;
            }

            return map;
        }

        private static int FindConfigWhitespaceSeparator(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return -1;
            }

            for (int i = 0; i < line.Length - 1; i++)
            {
                if (!char.IsWhiteSpace(line[i]))
                {
                    continue;
                }

                if (char.IsWhiteSpace(line[i + 1]))
                {
                    return i;
                }
            }

            return -1;
        }

        private sealed class EditorEntry
        {
            public EditorEntry(string key, FrameworkElement editor)
            {
                Key = key;
                Editor = editor;
            }

            public string Key { get; }
            public FrameworkElement Editor { get; }
        }

        private sealed class FilterEntry
        {
            public FilterEntry(string section, FrameworkElement container, string searchText)
            {
                Section = section;
                Container = container;
                SearchText = searchText;
            }

            public string Section { get; }
            public FrameworkElement Container { get; }
            public string SearchText { get; }
        }

        [DataContract]
        private sealed class PersistedUiStateRoot
        {
            [DataMember(Name = "configWindow", EmitDefaultValue = false)]
            public PersistedConfigWindowState ConfigWindow { get; set; }
        }

        [DataContract]
        private sealed class PersistedConfigWindowState
        {
            [DataMember(Name = "selectedSection", EmitDefaultValue = false)]
            public string SelectedSection { get; set; }

            [DataMember(Name = "verticalOffset", EmitDefaultValue = false)]
            public double VerticalOffset { get; set; }
        }
    }
}


