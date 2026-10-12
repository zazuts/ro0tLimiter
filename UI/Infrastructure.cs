using System;
using System.Collections.Concurrent;
using NetDivert.Core;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetDivert.UI
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    public enum RateUnit
    {
        Auto,
        KB,
        MB,
    }

    public sealed class UiSettings
    {
        private static string FilePath => System.IO.Path.Combine(Brand.UserDir, "ui.json");

        public static UiSettings Current { get; } = Create();

        public event Action? Changed;

        public RateUnit Unit { get; set; } = RateUnit.Auto;
        public string Language { get; set; } = "pt-BR";
        public bool TrayHintShown { get; set; }
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool ShowTrayTips { get; set; } = true;
        public int AppTab { get; set; }
        public List<string> Favorites { get; set; } = new();
        public List<string> Hidden { get; set; } = new();
        public List<string> Dismissed { get; set; } = new();
        public List<string> Pinned { get; set; } = new();
        public string ColorConnected { get; set; } = "#1F6B4A";
        public string ColorPinned { get; set; } = "#5C4B1F";
        public string ColorMissing { get; set; } = "#6B3030";
        public string ColorSpecial { get; set; } = "#2C4C7A";
        public string ColorSigned { get; set; } = "#1E5C4A";
        public string ColorSystem { get; set; } = "#3A4150";
        public string ColorWsl { get; set; } = "#5A3470";

        private static UiSettings Create()
        {
            Brand.MigrateLegacy();
            var settings = Load();
            Loc.SetLanguage(settings.Language);
            return settings;
        }

        public static UiSettings Load()
        {
            try
            {
                if (System.IO.File.Exists(FilePath))
                    return System.Text.Json.JsonSerializer.Deserialize<UiSettings>(System.IO.File.ReadAllText(FilePath)) ?? new UiSettings();
            }
            catch
            {
                // Corrupt settings fall back to defaults.
            }
            return new UiSettings();
        }

        public void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                System.IO.File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));
                Changed?.Invoke();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    public static class Format
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static RateUnit Unit { get; set; } = RateUnit.Auto;

        /// <summary>Unit used to type limits: MB/s only when explicitly chosen.</summary>
        public static bool LimitsInMB => Unit == RateUnit.MB;
        public static string LimitUnitLabel => LimitsInMB ? "MB/s" : "KB/s";

        public static double KBpsToLimitUnit(int kbps) => LimitsInMB ? kbps / 1024.0 : kbps;

        public static int LimitUnitToKBps(double value) =>
            (int)Math.Clamp(Math.Round(LimitsInMB ? value * 1024 : value), 1, int.MaxValue);

        public static string Bytes(double bytes)
        {
            if (bytes < 1)
                return "0 B";
            int u = 0;
            while (bytes >= 1024 && u < Units.Length - 1)
            {
                bytes /= 1024;
                u++;
            }
            return u == 0 ? $"{bytes:0} {Units[u]}" : $"{bytes:0.0} {Units[u]}";
        }

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1)
                return "";
            return Unit switch
            {
                RateUnit.KB => $"{bytesPerSecond / 1024:0.0} KB/s",
                RateUnit.MB => $"{bytesPerSecond / 1048576:0.00} MB/s",
                _ => Bytes(bytesPerSecond) + "/s",
            };
        }
    }

    /// <summary>Fixed-size history of samples used by the traffic graph.</summary>
    public sealed class History
    {
        private readonly double[] _data;
        private int _start;

        public History(int capacity) => _data = new double[capacity];

        public int Capacity => _data.Length;

        public void Push(double value)
        {
            _data[_start] = value;
            _start = (_start + 1) % _data.Length;
        }

        /// <summary>Oldest to newest.</summary>
        public double this[int i] => _data[(_start + i) % _data.Length];
    }

    public static class IconCache
    {
        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static ImageSource? _default;

        public static ImageSource? Get(string? path)
        {
            if (path == null)
                return Default;
            if (Cache.TryGetValue(path, out var img))
                return img;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                img = icon == null ? Default : ToImage(icon);
            }
            catch
            {
                img = Default;
            }
            Cache[path] = img;
            return img;
        }

        private static ImageSource? Default => _default ??= ToImage(System.Drawing.SystemIcons.Application);

        private static ImageSource ToImage(System.Drawing.Icon icon)
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
    }

    /// <summary>Asynchronous reverse-DNS cache for the connection list.</summary>
    public static class DnsCache
    {
        private static readonly ConcurrentDictionary<IPAddress, string?> Cache = new();

        public static string? Get(IPAddress ip)
        {
            if (Cache.TryGetValue(ip, out var host))
                return host;
            if (Cache.TryAdd(ip, null) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var entry = await Dns.GetHostEntryAsync(ip).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != ip.ToString())
                            Cache[ip] = entry.HostName;
                    }
                    catch
                    {
                        // No PTR record.
                    }
                });
            }
            return null;
        }
    }
}
