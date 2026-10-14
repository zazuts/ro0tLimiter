using DirectIp.CleanCore;
using Microsoft.Win32;

namespace DipWd;

/// <summary>
/// Chrome/Edge/Brave: HKCU SOCKS → KiTTY. Cleanup stale policies on start via CleanCore.
/// </summary>
sealed class ChromeAssist : IDisposable
{
    static readonly (string SubKey, string Label)[] Hives =
    {
        (@"Software\Policies\Google\Chrome", "Chrome"),
        (@"Software\Policies\Microsoft\Edge", "Edge"),
        (@"Software\Policies\BraveSoftware\Brave", "Brave"),
    };

    readonly AppLog _log;
    readonly string _proxyServer;
    readonly List<RestoreEntry> _restore = new();
    bool _applied;

    public ChromeAssist(string socksHost, int socksPort, AppLog log)
    {
        _log = log;
        _proxyServer = "socks5://" + socksHost + ":" + socksPort;
    }

    public static bool WantsAssist(IReadOnlyList<string> patterns) =>
        patterns.Any(p =>
            p.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("*.*", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("chrom", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("firefox", StringComparison.OrdinalIgnoreCase));

    public static bool IsBrowserName(string processPathOrName)
    {
        var n = Wildcard.FileNameOf(processPathOrName);
        if (string.IsNullOrEmpty(n)) return false;
        return n.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("chromium", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("firefox", StringComparison.OrdinalIgnoreCase);
    }

    public static void CleanupStaleBeforeSession(AppLog log)
    {
        var cleaned = BrowserPolicyCleaner.CleanupStale(s => log.Info(s));
        if (cleaned.Count == 0)
            log.Info("browser cleanup: nenhuma policy SOCKS residual");
    }

    public void Apply()
    {
        foreach (var (sub, label) in Hives)
            ApplyHive(sub, label);
        _applied = _restore.Count > 0;
        if (_applied)
        {
            _log.Info("Chrome/Edge/Brave assist: ProxyServer=" + _proxyServer + " QuicAllowed=0");
            _log.Info("REINICIE o browser (feche todas as janelas). Forge WinDivert NAO toca o browser.");
        }
    }

    void ApplyHive(string subKey, string label)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey, true);
            if (key is null) return;
            SaveThenSetString(key, subKey, "ProxyMode", "fixed_servers");
            SaveThenSetString(key, subKey, "ProxyServer", _proxyServer);
            SaveThenSetString(key, subKey, "ProxyBypassList", "<-loopback>;localhost;127.0.0.1");
            SaveThenSetDword(key, subKey, "QuicAllowed", 0);
            _log.Verbose("assist " + label + " OK");
        }
        catch (Exception ex)
        {
            _log.Info("assist " + label + " falhou: " + ex.Message);
        }
    }

    void SaveThenSetString(RegistryKey key, string subKey, string name, string value)
    {
        object? prev = null;
        var had = false;
        try
        {
            prev = key.GetValue(name);
            had = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        _restore.Add(new RestoreEntry(subKey, name, had, prev, RegistryValueKind.String));
        key.SetValue(name, value, RegistryValueKind.String);
    }

    void SaveThenSetDword(RegistryKey key, string subKey, string name, int value)
    {
        object? prev = null;
        var had = false;
        try
        {
            prev = key.GetValue(name);
            had = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        _restore.Add(new RestoreEntry(subKey, name, had, prev, RegistryValueKind.DWord));
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void Dispose()
    {
        if (!_applied) return;
        foreach (var e in Enumerable.Reverse(_restore))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(e.SubKey, true);
                if (key is null) continue;
                if (!e.HadValue)
                    key.DeleteValue(e.Name, false);
                else if (e.Previous is not null)
                    key.SetValue(e.Name, e.Previous, e.Kind);
            }
            catch { }
        }
        _log.Info("Chrome/Edge/Brave assist: politicas restauradas");
    }

    readonly record struct RestoreEntry(
        string SubKey, string Name, bool HadValue, object? Previous, RegistryValueKind Kind);
}
