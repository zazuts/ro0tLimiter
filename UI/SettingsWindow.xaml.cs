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
            LanguageBox.SelectedIndex = _ui.Language == "en-US" ? 1 : 0;
            AutoStartBox.IsChecked = _ui.StartWithWindows;
            MinimizedBox.IsChecked = _ui.StartMinimized;
            TopmostBox.IsChecked = _ui.AlwaysOnTop;
            TipsBox.IsChecked = _ui.ShowTrayTips;
            FiltersBox.IsChecked = _policy.FiltersOnStart;
            SignatureBox.IsChecked = _policy.VerifySignature;
            HashBox.IsChecked = _policy.ComputeHash;
            ResolveBox.IsChecked = _policy.ResolveAddresses;
            UacBox.IsChecked = _policy.IgnoreUacNoise;
            BlockOutBox.IsChecked = _policy.BlockAllOutbound;
            BlockInBox.IsChecked = _policy.BlockAllInbound;
            LoopbackBox.IsChecked = _policy.AllowLoopback;
            SixBox.IsChecked = _policy.Allow6to4;
            StealthBox.IsChecked = _policy.Stealth;
            SelectTri(TelOff, TelAllow, TelBlock, _policy.Telemetry);
            SelectTri(UpOff, UpAllow, UpBlock, _policy.Updates);
            SelectTri(AppOff, AppAllow, AppBlock, _policy.MicrosoftApps);
            PortsBox.Text = _policy.BlockedPorts;
            IpsBox.Text = _policy.BlockedIps;
            ExesBox.Text = _policy.BlockedExes;
            BlockDomainsBox.Text = _policy.BlockedDomains;
            AllowDomainsBox.Text = _policy.AllowedDomains;
            RegexBox.Text = _policy.RegexRules;
            UrlsBox.Text = _policy.ListUrls;
            LogDropBox.IsChecked = _policy.LogDrops;
            LogConnBox.IsChecked = _policy.LogConnections;
            LogDebugBox.IsChecked = _policy.LogDebug;
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
            _ui.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt-BR";
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

            _policy.FiltersOnStart = FiltersBox.IsChecked == true;
            _policy.VerifySignature = SignatureBox.IsChecked == true;
            _policy.ComputeHash = HashBox.IsChecked == true;
            _policy.ResolveAddresses = ResolveBox.IsChecked == true;
            _policy.IgnoreUacNoise = UacBox.IsChecked == true;
            _policy.BlockAllOutbound = BlockOutBox.IsChecked == true;
            _policy.BlockAllInbound = BlockInBox.IsChecked == true;
            _policy.AllowLoopback = LoopbackBox.IsChecked == true;
            _policy.Allow6to4 = SixBox.IsChecked == true;
            _policy.Stealth = StealthBox.IsChecked == true;
            _policy.Telemetry = ReadTri(TelAllow, TelBlock);
            _policy.Updates = ReadTri(UpAllow, UpBlock);
            _policy.MicrosoftApps = ReadTri(AppAllow, AppBlock);
            _policy.BlockedPorts = PortsBox.Text;
            _policy.BlockedIps = IpsBox.Text;
            _policy.BlockedExes = ExesBox.Text;
            _policy.BlockedDomains = BlockDomainsBox.Text;
            _policy.AllowedDomains = AllowDomainsBox.Text;
            _policy.RegexRules = RegexBox.Text;
            _policy.ListUrls = UrlsBox.Text;
            _policy.LogDrops = LogDropBox.IsChecked == true;
            _policy.LogConnections = LogConnBox.IsChecked == true;
            _policy.LogDebug = LogDebugBox.IsChecked == true;
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
            DialogResult = true;
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

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
