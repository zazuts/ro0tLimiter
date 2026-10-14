using Microsoft.Win32;

namespace DirectIp.CleanCore;

/// <summary>
/// Removes DirectIP ChromeAssist-style SOCKS policies from browsers.
/// Safe: only deletes when ProxyServer looks like socks5:// loopback assist.
/// </summary>
public static class BrowserPolicyCleaner
{
    static readonly string[] ValueNames =
        { "ProxyMode", "ProxyServer", "ProxyBypassList", "QuicAllowed" };

    static readonly (string Hive, string Label)[] Hives =
    {
        (@"Software\Policies\Google\Chrome", "Chrome"),
        (@"Software\Policies\Microsoft\Edge", "Edge"),
        (@"Software\Policies\BraveSoftware\Brave", "Brave"),
        (@"Software\Policies\Mozilla\Firefox", "Firefox"),
    };

    public static IReadOnlyList<string> CleanupStale(Action<string>? log = null)
    {
        var done = new List<string>();
        foreach (var (hive, label) in Hives)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(hive, writable: true);
                if (key is null) continue;
                if (!LooksLikeAssist(key) && !FirefoxLooksLikeAssist(key))
                    continue;
                foreach (var name in ValueNames)
                {
                    try { key.DeleteValue(name, throwOnMissingValue: false); }
                    catch { }
                }
                // Firefox may use different value names
                foreach (var extra in new[] { "Proxy", "NetworkPrediction", "DisableFirefoxStudies" })
                {
                    try
                    {
                        var v = key.GetValue(extra)?.ToString() ?? "";
                        if (v.Contains("socks", StringComparison.OrdinalIgnoreCase) ||
                            v.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                            key.DeleteValue(extra, false);
                    }
                    catch { }
                }
                done.Add(label);
                log?.Invoke("browser policy cleaned: " + label + " (" + hive + ")");
            }
            catch (Exception ex)
            {
                log?.Invoke("browser clean fail " + label + ": " + ex.Message);
            }
        }
        return done;
    }

    public static bool LooksLikeAssistProxyServer(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;
        var s = proxyServer.Trim();
        if (!s.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
            return false;
        return s.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeAssist(RegistryKey key)
    {
        var proxy = key.GetValue("ProxyServer")?.ToString();
        if (LooksLikeAssistProxyServer(proxy)) return true;
        var mode = key.GetValue("ProxyMode")?.ToString() ?? "";
        if (mode.Equals("fixed_servers", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeAssistProxyServer(proxy))
            return true;
        return false;
    }

    static bool FirefoxLooksLikeAssist(RegistryKey key)
    {
        foreach (var name in key.GetValueNames())
        {
            var v = key.GetValue(name)?.ToString() ?? "";
            if (LooksLikeAssistProxyServer(v)) return true;
            if (v.Contains("socks5", StringComparison.OrdinalIgnoreCase) &&
                (v.Contains("127.0.0.1") || v.Contains("localhost")))
                return true;
        }
        return false;
    }
}
