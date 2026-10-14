using System.Text;
using System.Text.Json;

namespace DirectIp.CleanCore;

public sealed class ProxyAppEntry
{
    public string PathOrPattern { get; set; } = "";
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "filter";
    public DateTimeOffset? CleanedUtc { get; set; }
}

/// <summary>
/// Append-only journal of proxied apps/patterns. Survives ALT+F4 / crash.
/// </summary>
public static class ProxyAppLedger
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void RecordPatterns(IEnumerable<string> patterns, string source = "filter")
    {
        foreach (var p in patterns)
        {
            var t = (p ?? "").Trim();
            if (t.Length == 0) continue;
            Record(t, source);
        }
    }

    public static void Record(string pathOrPattern, string source = "filter")
    {
        if (string.IsNullOrWhiteSpace(pathOrPattern)) return;
        var entry = new ProxyAppEntry
        {
            PathOrPattern = pathOrPattern.Trim(),
            AddedUtc = DateTimeOffset.UtcNow,
            Source = source
        };
        lock (Gate)
        {
            try
            {
                DipPaths.EnsureProgramData();
                var line = JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine;
                File.AppendAllText(DipPaths.ProxyAppsLedger, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static IReadOnlyList<ProxyAppEntry> ReadAll()
    {
        lock (Gate) return ReadAllUnlocked();
    }

    static List<ProxyAppEntry> ReadAllUnlocked()
    {
        var path = DipPaths.ProxyAppsLedger;
        if (!File.Exists(path)) return new List<ProxyAppEntry>();
        var list = new List<ProxyAppEntry>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<ProxyAppEntry>(line, JsonOpts);
                if (e is not null && !string.IsNullOrWhiteSpace(e.PathOrPattern))
                    list.Add(e);
            }
            catch { }
        }
        return list;
    }

    public static IReadOnlyList<ProxyAppEntry> ReadRecent(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        return ReadAll()
            .Where(e => e.AddedUtc >= cutoff)
            .GroupBy(e => e.PathOrPattern, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.AddedUtc).First())
            .OrderByDescending(e => e.AddedUtc)
            .ToList();
    }

    public static void MarkCleaned(IEnumerable<string> patterns)
    {
        var set = new HashSet<string>(patterns.Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        lock (Gate)
        {
            try
            {
                DipPaths.EnsureProgramData();
                var all = ReadAllUnlocked();
                var now = DateTimeOffset.UtcNow;
                foreach (var e in all)
                {
                    if (set.Contains(e.PathOrPattern))
                        e.CleanedUtc = now;
                }
                var sb = new StringBuilder();
                foreach (var e in all)
                    sb.AppendLine(JsonSerializer.Serialize(e, JsonOpts));
                File.WriteAllText(DipPaths.ProxyAppsLedger, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
