using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NetDivert.Core;

namespace NetDivert.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly UiSettings _ui;
        private readonly SecurityPolicy _policy;
        private readonly (string Key, TextBlock Label, TextBox Box)[] _colors;

        public SettingsWindow()
        {
            InitializeComponent();
            _ui = UiSettings.Current;
            _policy = PolicyStore.Load();
            _colors = new[]
            {
                Row("set.color.connected", _ui.ColorConnected),
                Row("set.color.pinned", _ui.ColorPinned),
                Row("set.color.missing", _ui.ColorMissing),
                Row("set.color.special", _ui.ColorSpecial),
                Row("set.color.signed", _ui.ColorSigned),
                Row("set.color.system", _ui.ColorSystem),
                Row("set.color.wsl", _ui.ColorWsl),
            };
            foreach (var row in _colors)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                row.Label.Width = 280;
                row.Label.VerticalAlignment = VerticalAlignment.Center;
                row.Box.Width = 90;
                var preview = new Border { Width = 28, Height = 18, Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(3) };
                preview.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Text") { Source = row.Box, Converter = new HexBrushConverter() });
                line.Children.Add(row.Label);
                line.Children.Add(row.Box);
                line.Children.Add(preview);
                ColorList.Children.Add(line);
            }
            LoadFields();
            Loc.Changed += RefreshPreview;
            Closed += (_, _) => Loc.Changed -= RefreshPreview;
            RefreshPreview();
        }

        private (string Key, TextBlock Label, TextBox Box) Row(string key, string hex) =>
            (key, new TextBlock { Text = Loc.Get(key) }, new TextBox { Text = hex });

        private void LoadFields()
        {
            LanguageBox.SelectedIndex = _ui.Language == "pt-BR" ? 0 : 1;
            ThemeBox.SelectedIndex = _ui.Theme == "light" ? 1 : 0;
            AutoStartBox.IsChecked = _ui.StartWithWindows;
            MinimizedBox.IsChecked = _ui.StartMinimized;
            TopmostBox.IsChecked = _ui.AlwaysOnTop;
            TipsBox.IsChecked = _ui.ShowTrayTips;
            FiltersBox.IsChecked = _policy.FiltersOnStart;
            SignatureBox.IsChecked = _policy.VerifySignature;
            HashBox.IsChecked = _policy.ComputeHash;
            ResolveBox.IsChecked = _policy.ResolveAddresses;
            UacBox.IsChecked = _policy.IgnoreUacNoise;
            NetModeBox.SelectedIndex = _policy.NetMode is >= 0 and <= 2 ? _policy.NetMode : 0;
            LoopbackBox.IsChecked = _policy.AllowLoopback;
            SixBox.IsChecked = _policy.Allow6to4;
            StealthBox.IsChecked = _policy.Stealth;
            SelectTri(TelOff, TelAllow, TelBlock, _policy.Telemetry);
            SelectTri(UpOff, UpAllow, UpBlock, _policy.Updates);
            SelectTri(AppOff, AppAllow, AppBlock, _policy.MicrosoftApps);
            PortsBox.Text = _policy.BlockedPorts;
            LocalPortsBox.Text = _policy.BlockedLocalPorts;
            IpsBox.Text = _policy.BlockedIps;
            LocalIpsBox.Text = _policy.BlockedLocalIps;
            OneDriveBox.IsChecked = _policy.AllowOneDrive;
            ExesBox.Text = _policy.BlockedExes;
            BlockDomainsBox.Text = _policy.BlockedDomains;
            AllowDomainsBox.Text = _policy.AllowedDomains;
            RegexBox.Text = _policy.RegexRules;
            UrlsBox.Text = _policy.ListUrls;
            LogDropBox.IsChecked = _policy.LogDrops;
            LogConnBox.IsChecked = _policy.LogConnections;
            LogDebugBox.IsChecked = _policy.LogDebug;
            LogRotateBox.SelectedIndex = _policy.LogRotate is >= 0 and <= 5 ? _policy.LogRotate : 0;
            KeepBox.IsChecked = _policy.UninstallMode == 0;
            WipeBox.IsChecked = _policy.UninstallMode == 1;
        }

        private static void SelectTri(RadioButton off, RadioButton allow, RadioButton block, int value)
        {
            off.IsChecked = value == 0;
            allow.IsChecked = value == 1;
            block.IsChecked = value == 2;
        }

        private static int ReadTri(RadioButton allow, RadioButton block) =>
            block.IsChecked == true ? 2 : allow.IsChecked == true ? 1 : 0;

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _ui.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US";
            _ui.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "dark";
            _ui.StartWithWindows = AutoStartBox.IsChecked == true;
            _ui.StartMinimized = MinimizedBox.IsChecked == true;
            _ui.AlwaysOnTop = TopmostBox.IsChecked == true;
            _ui.ShowTrayTips = TipsBox.IsChecked == true;
            _ui.ColorConnected = ColorOf(0);
            _ui.ColorPinned = ColorOf(1);
            _ui.ColorMissing = ColorOf(2);
            _ui.ColorSpecial = ColorOf(3);
            _ui.ColorSigned = ColorOf(4);
            _ui.ColorSystem = ColorOf(5);
            _ui.ColorWsl = ColorOf(6);
            _ui.Save();
            ApplyAutostart(_ui.StartWithWindows);
            Loc.SetLanguage(_ui.Language);
            Theme.Apply(_ui.Theme);

            _policy.FiltersOnStart = FiltersBox.IsChecked == true;
            _policy.VerifySignature = SignatureBox.IsChecked == true;
            _policy.ComputeHash = HashBox.IsChecked == true;
            _policy.ResolveAddresses = ResolveBox.IsChecked == true;
            _policy.IgnoreUacNoise = UacBox.IsChecked == true;
            _policy.NetMode = NetModeBox.SelectedIndex is >= 0 and <= 2 ? NetModeBox.SelectedIndex : 0;
            _policy.BlockAllOutbound = _policy.NetMode == 1;
            _policy.BlockAllInbound = _policy.NetMode == 1;
            _policy.AllowLoopback = LoopbackBox.IsChecked == true;
            _policy.Allow6to4 = SixBox.IsChecked == true;
            _policy.Stealth = StealthBox.IsChecked == true;
            _policy.Telemetry = ReadTri(TelAllow, TelBlock);
            _policy.Updates = ReadTri(UpAllow, UpBlock);
            _policy.MicrosoftApps = ReadTri(AppAllow, AppBlock);
            _policy.BlockedPorts = PortsBox.Text;
            _policy.BlockedLocalPorts = LocalPortsBox.Text;
            _policy.BlockedIps = IpsBox.Text;
            _policy.BlockedLocalIps = LocalIpsBox.Text;
            _policy.AllowOneDrive = OneDriveBox.IsChecked == true;
            _policy.BlockedExes = ExesBox.Text;
            _policy.BlockedDomains = BlockDomainsBox.Text;
            _policy.AllowedDomains = AllowDomainsBox.Text;
            _policy.RegexRules = RegexBox.Text;
            _policy.ListUrls = UrlsBox.Text;
            _policy.LogDrops = LogDropBox.IsChecked == true;
            _policy.LogConnections = LogConnBox.IsChecked == true;
            _policy.LogDebug = LogDebugBox.IsChecked == true;
            _policy.LogRotate = LogRotateBox.SelectedIndex is >= 0 and <= 5 ? LogRotateBox.SelectedIndex : 0;
            _policy.UninstallMode = WipeBox.IsChecked == true ? 1 : 0;
            try
            {
                PolicyStore.Save(_policy);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.Get("app"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (Owner != null)
                Owner.Topmost = _ui.AlwaysOnTop;
            Close();
        }

        private string ColorOf(int index)
        {
            string text = _colors[index].Box.Text.Trim();
            try
            {
                ColorConverter.ConvertFromString(text);
                return text;
            }
            catch
            {
                return new[] { _ui.ColorConnected, _ui.ColorPinned, _ui.ColorMissing, _ui.ColorSpecial, _ui.ColorSigned, _ui.ColorSystem, _ui.ColorWsl }[index];
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Theme.Apply(_ui.Theme);
            Close();
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;
            string theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "dark";
            Theme.Apply(theme);
        }

        private void RemoveFirewall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int removed = WindowsFirewall.RemoveCreated();
                MessageBox.Show(string.Format(Loc.Get("fw.cleared"), removed), Loc.Get("fw.title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.Get("fw.title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (Owner?.DataContext is not MainViewModel vm)
                return;
            var dialog = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "ro0tLimiter-backup.json",
            };
            if (dialog.ShowDialog(this) != true)
                return;
            File.WriteAllText(dialog.FileName, vm.ExportBackupJson());
            MessageBox.Show(Loc.Get("set.backup.exported"), Loc.Get("set.backup"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ImportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (Owner?.DataContext is not MainViewModel vm)
                return;
            if (MessageBox.Show(Loc.Get("set.backup.ask"), Loc.Get("set.backup"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                vm.ImportBackup(File.ReadAllText(dialog.FileName));
                LoadFields();
                MessageBox.Show(Loc.Get("set.backup.imported"), Loc.Get("set.backup"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.Get("set.backup"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveAll_Click(object sender, RoutedEventArgs e)
        {
            var box = new TextBox { Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Width = 88, Margin = new Thickness(0, 12, 8, 0), IsDefault = true };
            var cancel = new Button { Content = Loc.Get("set.cancel"), Width = 88, Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = Loc.Get("set.removePrompt"), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["FgBrush"] });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            var dialog = new Window
            {
                Title = Loc.Get("set.removeAll"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)Application.Current.Resources["PanelBrush"],
                Content = panel,
            };
            ok.Click += (_, _) => { dialog.DialogResult = true; };
            cancel.Click += (_, _) => { dialog.DialogResult = false; };
            if (dialog.ShowDialog() != true)
                return;
            string typed = box.Text.Trim();
            if (!string.Equals(typed, "remover tudo", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(typed, "remove all", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(Loc.Get("set.removePrompt"), Loc.Get("set.removeAll"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _policy.UninstallMode = WipeBox.IsChecked == true ? 1 : 0;
            try { PolicyStore.Save(_policy); } catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.Get("app"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (Owner?.DataContext is MainViewModel vm)
                vm.StopRedirect();
            try { NetDivert.Service.ServiceManager.Uninstall(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.Get("app"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Application.Current.Shutdown();
        }

        private void ReplicateFirewall_Click(object sender, RoutedEventArgs e)
        {
            var result = WindowsFirewall.ApplySaved(PortsBox.Text, IpsBox.Text, ExesBox.Text);
            MessageBox.Show(FirewallMessage.FormatAll(result), Loc.Get("fw.title"),
                MessageBoxButton.OK,
                result.Error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Brand.LogDir);
            Process.Start("explorer.exe", Brand.LogDir);
        }

        private void DeleteLogs_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.Get("set.deleteLogs.ask"), Loc.Get("set.logs"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var (deleted, busy) = TrafficLog.DeleteAll();
            RefreshPreview();
            string text = busy > 0
                ? string.Format(Loc.Get("set.deleteLogs.busy"), deleted, busy)
                : string.Format(Loc.Get("set.deleteLogs.done"), deleted);
            MessageBox.Show(text, Loc.Get("set.logs"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RefreshPreview()
        {
            LogPreview.Text = string.Join("\n\n", new[]
            {
                TrafficLog.Tail("dropped.log"),
                TrafficLog.Tail("connections.log"),
                TrafficLog.Tail("debug.log"),
            }).Trim();
            foreach (var row in _colors)
                row.Label.Text = Loc.Get(row.Key);
        }

        private static void ApplyAutostart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null)
                    return;
                if (enabled)
                    key.SetValue(Brand.Product, $"\"{Environment.ProcessPath}\"");
                else
                    key.DeleteValue(Brand.Product, throwOnMissingValue: false);
            }
            catch
            {
                // The user can still start the program by hand.
            }
        }
    }

    public sealed class HexBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try { return Theme.Brush(value as string ?? ""); }
            catch { return Theme.Brush("#3A3F4B"); }
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new System.NotSupportedException();
    }
}
