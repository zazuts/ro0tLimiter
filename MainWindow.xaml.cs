using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetDivert.UI;
using Forms = System.Windows.Forms;

namespace NetDivert
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly Forms.NotifyIcon _tray;
        private readonly System.Drawing.Icon _icon;
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
                if (e.PropertyName is nameof(MainViewModel.IsRunning) or "")
                    UpdateTrayIcon();
            };
            Loc.Changed += () => Dispatcher.BeginInvoke(ApplyLanguage);
            UpdateHeaders();
            ApplyLanguage();

            _tray = CreateTrayIcon();

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
            var color = _vm.IsRunning
                ? System.Drawing.Color.FromArgb(0x2E, 0xC8, 0x6A)
                : System.Drawing.Color.FromArgb(0xE0, 0x4B, 0x4B);
            var icon = AppIcon.Create(color);
            _tray.Icon = icon;
            Icon = AppIcon.ToImageSource(icon);
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
            UpdateHeaders();
        }

        private void UpdateHeaders()
        {
            string unit = _vm.LimitUnitLabel;
            LimitDownColumn.Header = $"{Loc.Get("col.limitDown")} ({unit})";
            LimitUpColumn.Header = $"{Loc.Get("col.limitUp")} ({unit})";
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
    }
}
