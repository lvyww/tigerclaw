using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TigerClaw.Overlay
{
    public partial class StatusWindow : Window
    {
        private const int VkLButton = 0x01;
        private const int VkRButton = 0x02;
        private bool _isOff;
        private bool _isChinese = true;
        private string _displayText = "\u4e2d";
        private bool _hideStatusBar;
        private readonly DispatcherTimer _contextMenuDismissTimer;
        private bool _lastLeftButtonDown;
        private bool _lastRightButtonDown;
        private static readonly string[] ThemeNames =
        {
            "\u9ed8\u8ba4",
            "\u901a\u900f",
            "\u4e00\u822c\u901a\u900f",
            "\u8ff7\u96fe",
            "\u661f\u591c",
            "\u7eb8",
            "\u7c89",
            "\u8d5b\u535a\u670b\u514b",
            "\u6e05\u6668"
        };

        public StatusWindow()
        {
            InitializeComponent();
            _contextMenuDismissTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _contextMenuDismissTimer.Tick += ContextMenuDismissTimer_Tick;
        }

        public void ApplyStatus(bool isOff, bool isChinese, string displayText, bool hideStatusBar)
        {
            _isOff = isOff;
            _isChinese = isChinese;
            _displayText = string.IsNullOrWhiteSpace(displayText) ? (_isChinese ? "\u4e2d" : "EN") : displayText;
            _hideStatusBar = hideStatusBar;
            RefreshVisual();
        }

        private void RefreshVisual()
        {
            if (_isOff)
            {
                Disp.Text = "\u7981";
                Disp.FontSize = 14;
                Disp.Foreground = CreateFrozenBrush(0x6A, 0x4A, 0x3A);
                Disp.FontFamily = new FontFamily("\u7b49\u7ebf");
                Bd.Background = CreateFrozenBrush(0xF3, 0xE8, 0xE2);
                Bd.BorderBrush = CreateFrozenBrush(0xB9, 0x8A, 0x74);
                AccentMark.Background = CreateFrozenBrush(0xB9, 0x8A, 0x74);
                Width = 26;
                Height = 50;
            }
            else if (_isChinese)
            {
                Disp.Text = string.IsNullOrWhiteSpace(_displayText) ? "\u4e2d" : _displayText;
                Disp.FontFamily = new FontFamily("\u7b49\u7ebf");
                Disp.Foreground = CreateFrozenBrush(0x3A, 0x2A, 0x20);
                Disp.FontSize = 15;
                Bd.Background = CreateFrozenBrush(0xFF, 0xF8, 0xF0);
                Bd.BorderBrush = CreateFrozenBrush(0xD9, 0x6A, 0x1B);
                AccentMark.Background = CreateFrozenBrush(0xD9, 0x6A, 0x1B);
                Width = 26;
                Height = 50;
            }
            else
            {
                Disp.Text = string.IsNullOrWhiteSpace(_displayText) ? "EN" : _displayText;
                Disp.FontSize = 14;
                Disp.Foreground = CreateFrozenBrush(0x5A, 0x41, 0x30);
                Bd.Background = CreateFrozenBrush(0xFF, 0xFD, 0xF8);
                Bd.BorderBrush = CreateFrozenBrush(0xD9, 0xC7, 0xB3);
                AccentMark.Background = CreateFrozenBrush(0xD9, 0xC7, 0xB3);
                Disp.FontFamily = new FontFamily("\u7b49\u7ebf");
                Width = 26;
                Height = 50;
            }

            Opacity = _hideStatusBar ? 0 : 1;
        }

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void Disp_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
                e.Handled = true;
            }
            catch
            {
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int exStyle = (int)NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
            RefreshVisual();
        }

        private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ShowContextMenuAtCursor();
            e.Handled = true;
        }

        private async void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            BeginContextMenuDismissMonitor();
            await RefreshDynamicMenusAsync();
        }

        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            StopContextMenuDismissMonitor();
        }

        private async Task RefreshDynamicMenusAsync()
        {
            if (MenuSchema == null || MenuTheme == null)
            {
                return;
            }

            CoreSchemaListResult schemaResult = await CorePipeClient.GetSchemaListAsync(1200);
            if (schemaResult.Success)
            {
                MenuSchema.Items.Clear();
                foreach (string schema in schemaResult.SchemaList ?? Array.Empty<string>())
                {
                    string schemaName = schema;
                    var item = new MenuItem
                    {
                        Header = schemaName,
                        IsCheckable = true,
                        IsChecked = string.Equals(schemaName, schemaResult.CurrentSchema, StringComparison.Ordinal)
                    };
                    item.Click += async (sender, args) =>
                    {
                        await CorePipeClient.SetConfigValueAsync("\u5f53\u524d\u7801\u8868", schemaName, 1200);
                    };
                    MenuSchema.Items.Add(item);
                }
            }

            CoreThemeResult themeResult = await CorePipeClient.GetCurrentThemeAsync(1200);
            string currentTheme = themeResult.Success ? themeResult.Theme : string.Empty;
            MenuTheme.Items.Clear();
            foreach (string theme in ThemeNames)
            {
                string themeName = theme;
                var item = new MenuItem
                {
                    Header = themeName,
                    IsCheckable = true,
                    IsChecked = string.Equals(themeName, currentTheme, StringComparison.Ordinal)
                };
                item.Click += async (sender, args) =>
                {
                    await CorePipeClient.SetConfigValueAsync("\u4e3b\u9898", themeName, 1200);
                };
                MenuTheme.Items.Add(item);
            }
        }

        private async void MenuConfig_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.ShowConfigWinAsync(1500);
        }

        private async void MenuOfficial_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.OpenOfficialAsync(1500);
        }

        private async void MenuFolder_Click(object sender, RoutedEventArgs e)
        {
            CorePathResult result = await CorePipeClient.GetMbFolderPathAsync(1500);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Path))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.Path,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.ExportMbAsync(3000);
        }

        private async void MenuReload_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.ReloadMbAsync(1500);
        }

        private async void MenuAddCi_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.ShowAddCiAsync(1500);
        }

        private async void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            await CorePipeClient.ExitCoreAsync(1500);
            Application.Current.Shutdown();
        }

        public void ShowContextMenuAtCursor()
        {
            if (ContextMenu == null)
            {
                return;
            }

            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos))
            {
                return;
            }

            Point menuPoint = new Point(cursorPos.X, cursorPos.Y);
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                menuPoint = source.CompositionTarget.TransformFromDevice.Transform(menuPoint);
            }

            ContextMenu.Placement = PlacementMode.AbsolutePoint;
            ContextMenu.HorizontalOffset = menuPoint.X;
            ContextMenu.VerticalOffset = menuPoint.Y;
            ContextMenu.IsOpen = true;
        }

        private void BeginContextMenuDismissMonitor()
        {
            _lastLeftButtonDown = IsMouseButtonDown(VkLButton);
            _lastRightButtonDown = IsMouseButtonDown(VkRButton);
            _contextMenuDismissTimer.Start();
        }

        private void StopContextMenuDismissMonitor()
        {
            _contextMenuDismissTimer.Stop();
        }

        private void ContextMenuDismissTimer_Tick(object sender, EventArgs e)
        {
            if (ContextMenu == null || !ContextMenu.IsOpen)
            {
                StopContextMenuDismissMonitor();
                return;
            }

            bool leftDown = IsMouseButtonDown(VkLButton);
            bool rightDown = IsMouseButtonDown(VkRButton);
            bool pressedOutside = (!IsCursorInsideContextMenu()) &&
                                  ((leftDown && !_lastLeftButtonDown) || (rightDown && !_lastRightButtonDown));

            _lastLeftButtonDown = leftDown;
            _lastRightButtonDown = rightDown;

            if (pressedOutside)
            {
                ContextMenu.IsOpen = false;
            }
        }

        private bool IsCursorInsideContextMenu()
        {
            if (ContextMenu == null || !ContextMenu.IsOpen)
            {
                return false;
            }

            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos))
            {
                return false;
            }

            if (IsPointInsideFrameworkElementScreenBounds(ContextMenu, cursorPos.X, cursorPos.Y))
            {
                return true;
            }

            return IsPointInsideOpenSubmenus(ContextMenu.Items, cursorPos.X, cursorPos.Y);
        }

        private static bool IsMouseButtonDown(int vk)
        {
            return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool IsPointInsideOpenSubmenus(ItemCollection items, double x, double y)
        {
            foreach (object itemObject in items)
            {
                if (!(itemObject is MenuItem item) || !item.IsSubmenuOpen)
                {
                    continue;
                }

                item.ApplyTemplate();
                Popup popup = item.Template?.FindName("PART_Popup", item) as Popup;
                if (popup?.Child is FrameworkElement popupChild &&
                    IsPointInsideFrameworkElementScreenBounds(popupChild, x, y))
                {
                    return true;
                }

                if (IsPointInsideOpenSubmenus(item.Items, x, y))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointInsideFrameworkElementScreenBounds(FrameworkElement element, double x, double y)
        {
            if (element == null)
            {
                return false;
            }

            double width = Math.Max(element.ActualWidth, element.RenderSize.Width);
            double height = Math.Max(element.ActualHeight, element.RenderSize.Height);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            Point topLeft = element.PointToScreen(new Point(0, 0));
            Point bottomRight = element.PointToScreen(new Point(width, height));
            double left = Math.Min(topLeft.X, bottomRight.X);
            double top = Math.Min(topLeft.Y, bottomRight.Y);
            double right = Math.Max(topLeft.X, bottomRight.X);
            double bottom = Math.Max(topLeft.Y, bottomRight.Y);

            return x >= left && x < right && y >= top && y < bottom;
        }
    }
}
