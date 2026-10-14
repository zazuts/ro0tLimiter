using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DirectIp.CleanCore;

public sealed class CleanReport
{
    public List<string> Lines { get; } = new();
    public void Add(string line) => Lines.Add(line);
    public override string ToString() => string.Join(Environment.NewLine, Lines);
}

public static class SystemCleaner
{
    static readonly string[] ProcessNames =
    {
        "DirectIp", "dip-wd", "dip-npcap", "dip-tcp", "CLEAN"
    };

    public static CleanReport Run(bool killSelfDirectIp = true, TimeSpan? ledgerWindow = null)
    {
        var window = ledgerWindow ?? TimeSpan.FromDays(90);
        var report = new CleanReport();
        report.Add("DirectIP CLEAN " + DateTime.Now.ToString("u"));
        report.Add("ProgramData=" + DipPaths.ProgramDataDir);

        var recent = ProxyAppLedger.ReadRecent(window);
        report.Add("ledger apps (last " + (int)window.TotalDays + "d): " + recent.Count);
        foreach (var e in recent.Take(40))
            report.Add("  - " + e.PathOrPattern + " @ " + e.AddedUtc.ToString("u"));

        TryLoadFilterFromSettings(report);

        KillEngines(report, killSelfDirectIp);
        TryStopWinDivert(report);

        var browsers = BrowserPolicyCleaner.CleanupStale(s => report.Add(s));
        report.Add("browsers cleaned: " + (browsers.Count == 0 ? "(none)" : string.Join(", ", browsers)));

        CleanHooks(report);
        CleanTaggedKittySessions(report);

        ProxyAppLedger.MarkCleaned(recent.Select(e => e.PathOrPattern));
        report.Add("ledger marked cleaned for " + recent.Count + " patterns");

        WriteCleanLog(report);
        return report;
    }

    static void TryLoadFilterFromSettings(CleanReport report)
    {
        try
        {
            var path = DipPaths.SettingsJson;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("exeFilter", out var ef))
            {
                var raw = ef.GetString() ?? "";
                var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                ProxyAppLedger.RecordPatterns(parts, "settings-fallback");
                report.Add("settings.exeFilter recorded: " + raw);
            }
        }
        catch (Exception ex)
        {
            report.Add("settings read: " + ex.Message);
        }
    }

    static void KillEngines(CleanReport report, bool killDirectIp)
    {
        var self = Environment.ProcessId;
        foreach (var name in ProcessNames)
        {
            if (!killDirectIp && name.Equals("DirectIp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("CLEAN", StringComparison.OrdinalIgnoreCase))
                continue;
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.Id == self) continue;
                    report.Add("kill " + name + " pid=" + p.Id);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    report.Add("kill fail " + name + ": " + ex.Message);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    static void TryStopWinDivert(CleanReport report)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop WinDivert",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            report.Add("sc stop WinDivert exit=" + (p?.ExitCode.ToString() ?? "?"));
        }
        catch (Exception ex)
        {
            report.Add("WinDivert stop: " + ex.Message);
        }
    }

    static void CleanHooks(CleanReport report)
    {
        try
        {
            DipPaths.EnsureProgramData();
            var hooks = DipPaths.HooksDir;
            if (Directory.Exists(hooks))
            {
                Directory.Delete(hooks, recursive: true);
                report.Add("deleted hooks dir: " + hooks);
            }
            if (!Directory.Exists(DipPaths.ProgramDataDir)) return;
            foreach (var pattern in new[] { "dip-hook*", "dip-tcp*", "dip-npcap*" })
            {
                foreach (var f in Directory.EnumerateFiles(DipPaths.ProgramDataDir, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(f);
                        report.Add("deleted " + f);
                    }
                    catch (Exception ex) { report.Add("delete fail " + f + ": " + ex.Message); }
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("hooks clean: " + ex.Message);
        }
    }

    static void CleanTaggedKittySessions(CleanReport report)
    {
        const string sessions = @"Software\9bis.com\KiTTY\Sessions";
        var tagged = KittySessionTags.Read().ToList();
        if (tagged.Count == 0)
        {
            report.Add("kitty tagged sessions: none");
            return;
        }

        var removed = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(sessions, writable: true);
            if (root is null)
            {
                report.Add("kitty sessions key missing");
                return;
            }
            foreach (var name in tagged)
            {
                if (KittySessionTags.IsProtectedTemplate(name))
                {
                    report.Add("skip protected kitty session: " + name);
                    continue;
                }
                var munged = Munge(name);
                try
                {
                    root.DeleteSubKeyTree(munged, throwOnMissingSubKey: false);
                    removed.Add(name);
                    report.Add("removed kitty session: " + name);
                }
                catch (Exception ex)
                {
                    report.Add("kitty remove fail " + name + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("kitty clean: " + ex.Message);
        }

        if (removed.Count > 0)
            KittySessionTags.RemoveTags(removed);
    }

    static void WriteCleanLog(CleanReport report)
    {
        try
        {
            DipPaths.EnsureProgramData();
            var path = Path.Combine(DipPaths.ProgramDataDir,
                "clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            report.Add("log: " + path);
        }
        catch { }
    }

    // Same munge as KiTTY (kitty_commun.c)
    public static string Munge(string name)
    {
        var hex = "0123456789ABCDEF";
        var sb = new StringBuilder(name.Length * 3);
        var candot = false;
        foreach (var ch in name)
        {
            var c = (int)ch;
            if (ch is ' ' or '\\' or '*' or '?' or ':' or '/' or '"' or '<' or '>' or '|' or '%'
                || c < 32 || c > 126 || (ch == '.' && !candot))
            {
                sb.Append('%');
                sb.Append(hex[c >> 4]);
                sb.Append(hex[c & 15]);
            }
            else sb.Append(ch);
            candot = true;
        }
        return sb.ToString();
    }
}
