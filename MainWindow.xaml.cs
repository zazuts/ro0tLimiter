using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Media;
using NetDivert.UI;
using Forms = System.Windows.Forms;

namespace NetDivert
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly Forms.NotifyIcon _tray;
        private readonly System.Drawing.Icon _icon;
        private System.Drawing.Icon? _blinkIcon;
        private System.Drawing.Icon? _phaseDownRed;
        private System.Drawing.Icon? _phaseUpRed;
        private System.Windows.Threading.DispatcherTimer? _arrowTimer;
        private bool _arrowPhase;
        private readonly EventWaitHandle _showSignal;
        private readonly RegisteredWaitHandle _showWait;
        private bool _exiting;

        public MainWindow()
        {
            InitializeComponent();
            _icon = AppIcon.Create(System.Drawing.Color.FromArgb(0xE0, 0x4B, 0x4B));
            Icon = AppIcon.ToImageSource(_icon);

            _vm = new MainViewModel();
            DataContext = _vm;
            _vm.Refreshed += OnRefreshed;
            _vm.UnitChanged += UpdateHeaders;
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.TrayText) or "")
                    _tray!.Text = Truncate(_vm.TrayText, 63);
                if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.RedirectIsRunning) or "")
                    UpdateTrayIcon();
                if (e.PropertyName is nameof(MainViewModel.ShowCoolingList) or "")
                    ApplyCoolingLayout();
            };
            Loc.Changed += () => Dispatcher.BeginInvoke(ApplyLanguage);
            UpdateHeaders();
            ApplyLanguage();
            ApplyCoolingLayout();
            Loaded += (_, _) => Dispatcher.BeginInvoke(ApplyCoolingLayout);

            _tray = CreateTrayIcon();
            UpdateTrayIcon();

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

            Application.Current.SessionEnding += (_, _) => _exiting = true;
            Loaded += async (_, _) =>
            {
                Topmost = UiSettings.Current.AlwaysOnTop;
                await _vm.InitializeAsync();
                if (UiSettings.Current.StartMinimized)
                    HideToTray();
            };
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        private Forms.NotifyIcon CreateTrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Loc.Get("tray.open"), null, (_, _) => ShowFromTray());
            menu.Items.Add(Loc.Get("tray.toggle"), null, (_, _) => _vm.ToggleEngine());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Loc.Get("tray.exit"), null, (_, _) => ExitApplication());
            ((Forms.ToolStripMenuItem)menu.Items[0]).Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);

            var tray = new Forms.NotifyIcon
            {
                Text = "ro0tLimiter",
                Icon = _icon,
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    ShowFromTray();
            };
            return tray;
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void HideToTray()
        {
            Hide();
            if (!_vm.TrayHintShown && UiSettings.Current.ShowTrayTips)
            {
                _vm.TrayHintShown = true;
                string detail = _vm.IsServiceMode ? Loc.Get("tray.hint.service") : Loc.Get("tray.hint.local");
                _tray.ShowBalloonTip(3000, Loc.Get("tray.hint.title"), detail, Forms.ToolTipIcon.Info);
            }
        }

        private void ExitApplication()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _exiting = true;
                Close();
            });
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
                HideToTray();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnClosing(e);
            _showWait.Unregister(null);
            _showSignal.Dispose();
            try { _vm.Dispose(); }
            catch (Exception) { }
            _tray.Visible = false;
            _tray.Dispose();
        }

        private void UpdateTrayIcon()
        {
            if (_tray == null)
                return;
            bool tunnel = _vm.RedirectIsRunning;
            if (tunnel && _arrowTimer == null)
            {
                _arrowTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
                _arrowTimer.Tick += (_, _) =>
                {
                    _arrowPhase = !_arrowPhase;
                    if (_tray != null)
                        _tray.Icon = _arrowPhase ? _phaseUpRed ?? _tray.Icon : _phaseDownRed ?? _tray.Icon;
                };
                _arrowTimer.Start();
            }
            else if (!tunnel && _arrowTimer != null)
            {
                _arrowTimer.Stop();
                _arrowTimer = null;
            }
            PaintTrayIcon();
        }

        private void PaintTrayIcon()
        {
            if (_tray == null)
                return;
            var accent = _vm.IsRunning
                ? System.Drawing.Color.FromArgb(0x2E, 0xC8, 0x6A)
                : System.Drawing.Color.FromArgb(0xE0, 0x4B, 0x4B);
            var down = System.Drawing.Color.FromArgb(0x7C, 0xF5, 0xB0);
            var up = System.Drawing.Color.FromArgb(0xFF, 0xC0, 0x7A);
            var red = System.Drawing.Color.FromArgb(0xFF, 0x3B, 0x3B);
            DisposeArrowIcons();
            if (_vm.RedirectIsRunning)
            {
                _phaseDownRed = AppIcon.Create(accent, downColor: red, upColor: up);
                _phaseUpRed = AppIcon.Create(accent, downColor: down, upColor: red);
                _tray.Icon = _arrowPhase ? _phaseUpRed : _phaseDownRed;
                Icon = AppIcon.ToImageSource(_tray.Icon);
                return;
            }
            var icon = AppIcon.Create(accent);
            var previous = _blinkIcon;
            _blinkIcon = icon;
            _tray.Icon = icon;
            Icon = AppIcon.ToImageSource(icon);
            if (previous != null && !ReferenceEquals(previous, _icon))
                previous.Dispose();
        }

        private void DisposeArrowIcons()
        {
            _phaseDownRed?.Dispose();
            _phaseUpRed?.Dispose();
            _phaseDownRed = null;
            _phaseUpRed = null;
        }

        private void ApplyLanguage()
        {
            AppColumn.Header = Loc.Get("col.app");
            PidColumn.Header = Loc.Get("col.pid");
            AdapterColumn.Header = Loc.Get("col.adapter");
            DownColumn.Header = Loc.Get("col.down");
            UpColumn.Header = Loc.Get("col.up");
            TotalInColumn.Header = Loc.Get("col.totalDown");
            TotalOutColumn.Header = Loc.Get("col.totalUp");
            ConnColumn.Header = Loc.Get("col.conns");
            DiscardColumn.Header = Loc.Get("col.discarded");
            BlockInColumn.Header = Loc.Get("col.blockDown");
            BlockOutColumn.Header = Loc.Get("col.blockUp");
            ConnTab.Header = Loc.Get("tab.conns");
            ListenTab.Header = Loc.Get("tab.listeners");
            GraphTab.Header = Loc.Get("tab.graph");
            ListenProcessColumn.Header = Loc.Get("listen.process");
            ListenProtoColumn.Header = Loc.Get("listen.proto");
            ListenAddrColumn.Header = Loc.Get("listen.addr");
            ListenPortColumn.Header = Loc.Get("listen.port");
            ListenPathColumn.Header = Loc.Get("listen.path");
            DetailsToggle.Content = Loc.Get(_detailsVisible ? "panel.hide" : "panel.show");
            UpdateHeaders();
        }

        private void UpdateHeaders()
        {
            string unit = _vm.LimitUnitLabel;
            LimitDownColumn.Header = $"{Loc.Get("col.limitDown")} ({unit})";
            LimitUpColumn.Header = $"{Loc.Get("col.limitUp")} ({unit})";
            if (AppsGrid.Columns.Count == CoolingGrid.Columns.Count)
            {
                for (int i = 0; i < AppsGrid.Columns.Count; i++)
                    CoolingGrid.Columns[i].Header = AppsGrid.Columns[i].Header;
            }
            FitAppColumns(AppsGrid);
            FitAppColumns(CoolingGrid);
            FitFlowColumns(ConnGrid);
        }

        private void ApplyCoolingLayout()
        {
            bool on = _vm.ShowCoolingList;
            CoolingPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            OnlineSplit.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            OnlineSplitRow.Height = on ? new GridLength(5) : new GridLength(0);
            CoolingRow.Height = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            CoolingRow.MinHeight = on ? 72 : 0;
            ActiveRow.MinHeight = on ? 72 : 0;
            ActiveRow.Height = new GridLength(1, GridUnitType.Star);
        }

        private bool _detailsVisible = true;
        private bool _syncSelection;

        private void ToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsVisible = !_detailsVisible;
            DetailRow.Height = _detailsVisible ? new GridLength(250) : new GridLength(0);
            DetailSplitterRow.Height = _detailsVisible ? new GridLength(5) : new GridLength(0);
            DetailsToggle.Content = Loc.Get(_detailsVisible ? "panel.hide" : "panel.show");
            if (_vm.ShowCoolingList)
            {
                ActiveRow.Height = new GridLength(1, GridUnitType.Star);
                CoolingRow.Height = new GridLength(1, GridUnitType.Star);
            }
        }

        private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            SyncSelection(AppsGrid, CoolingGrid);

        private void CoolingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            SyncSelection(CoolingGrid, AppsGrid);

        private void SyncSelection(DataGrid source, DataGrid other)
        {
            if (_syncSelection)
                return;
            _syncSelection = true;
            other.SelectedItem = null;
            _syncSelection = false;
            _vm.SelectedApp = source.SelectedItem as AppRowViewModel;
        }

        private void AppsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid grid && grid.SelectedItem is AppRowViewModel row)
                _vm.ShowListeners(row);
        }

        private void FitAppColumns(DataGrid grid)
        {
            if (grid.Columns.Count < 3)
                return;
            double cw = TextWidth("nnnnnnnnnn") / 10.0;
            grid.Columns[0].Width = cw * 25 + 36;
            grid.Columns[1].Width = cw * 10 + 20;
            grid.Columns[2].Width = cw * 13 + 20;
            for (int i = 3; i < grid.Columns.Count; i++)
            {
                bool limit = grid.Columns[i] is DataGridTemplateColumn && i is 9 or 10;
                bool block = grid.Columns[i] is DataGridTemplateColumn && i is 11 or 12;
                double floor = limit ? 128 : block ? 78 : 72;
                string header = grid.Columns[i].Header?.ToString() ?? "";
                grid.Columns[i].Width = Math.Max(floor, TextWidth(header) + 28);
            }
        }

        private void FitFlowColumns(DataGrid grid)
        {
            foreach (var column in grid.Columns)
            {
                if (column.Width.IsStar)
                    continue;
                string header = column.Header?.ToString() ?? "";
                column.Width = Math.Max(70, TextWidth(header) + 28);
            }
        }

        private double TextWidth(string text)
        {
            if (text.Length == 0)
                return 0;
            double dpi = PresentationSource.FromVisual(this) == null ? 1 : VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12.5,
                Brushes.Black,
                dpi);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private void AppsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null && node is not DataGridRow)
                node = VisualTreeHelper.GetParent(node);
            if (node is not DataGridRow row)
                return;
            row.IsSelected = true;
            row.Focus();
        }

        private void OnRefreshed()
        {
            if (_vm.SelectedApp is { } app)
                Graph.SetData(app.DownHistory, app.UpHistory);
            else
                Graph.SetData(_vm.TotalDownHistory, _vm.TotalUpHistory);
        }

        private void ServiceButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = (ContextMenu)Resources["ServiceMenu"];
            menu.DataContext = _vm;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _vm.RefreshServiceStatus();
            menu.IsOpen = true;
        }

        private void EditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void EditBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.SelectAll();
        }

        private void ToggleRedirect_Click(object sender, RoutedEventArgs e) => _vm.ToggleRedirect();

        private void RedirectAddFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executável|*.exe|Todos os arquivos|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true)
                return;
            foreach (string file in dialog.FileNames)
                _vm.AddRedirectPattern(System.IO.Path.GetFileName(file));
        }

        private void RedirectAddName_Click(object sender, RoutedEventArgs e)
        {
            string? text = AskRedirect(Loc.Get("redir.addName"), Loc.Get("redir.askName"));
            if (text != null)
                _vm.AddRedirectPattern(text);
        }

        private void RedirectAddWild_Click(object sender, RoutedEventArgs e)
        {
            string? text = AskRedirect(Loc.Get("redir.addWild"), Loc.Get("redir.askWild"));
            if (text != null)
                _vm.AddRedirectPattern(text);
        }

        private void RedirectAddApp_Click(object sender, RoutedEventArgs e)
        {
            var app = _vm.SelectedApp;
            if (app == null)
                return;
            string name = string.IsNullOrWhiteSpace(app.Path) ? app.Name : System.IO.Path.GetFileName(app.Path);
            _vm.AddRedirectPattern(name);
        }

        private void RedirectRemove_Click(object sender, RoutedEventArgs e) =>
            _vm.RemoveRedirectPattern(RedirectList.SelectedItem as string);

        private string? AskRedirect(string title, string label)
        {
            var box = new TextBox { Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 12, 8, 0), IsDefault = true };
            var cancel = new Button { Content = Loc.Get("set.cancel"), Width = 80, Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBrush"],
                Content = panel,
            };
            string? answer = null;
            ok.Click += (_, _) => { answer = box.Text; dialog.DialogResult = true; };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            return dialog.ShowDialog() == true ? answer : null;
        }
    }
}
