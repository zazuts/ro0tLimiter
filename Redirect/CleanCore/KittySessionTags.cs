using System.Text.Json;

namespace DirectIp.CleanCore;

public static class KittySessionTags
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void TagCreated(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName)) return;
        if (IsProtectedTemplate(sessionName)) return;
        lock (Gate)
        {
            var list = Read().ToList();
            if (list.Any(s => s.Equals(sessionName.Trim(), StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(sessionName.Trim());
            Save(list);
        }
    }

    public static IReadOnlyList<string> Read()
    {
        lock (Gate)
        {
            try
            {
                var path = DipPaths.CreatedKittySessions;
                if (!File.Exists(path)) return Array.Empty<string>();
                var arr = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), JsonOpts);
                return arr ?? new List<string>();
            }
            catch { return Array.Empty<string>(); }
        }
    }

    public static void RemoveTags(IEnumerable<string> names)
    {
        lock (Gate)
        {
            var remove = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var kept = Read().Where(s => !remove.Contains(s)).ToList();
            Save(kept);
        }
    }

    public static bool IsProtectedTemplate(string name)
    {
        var n = (name ?? "").Trim();
        return n.Equals("NEWYORK ro0t", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("NEW YORK ro0t", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("Default Settings", StringComparison.OrdinalIgnoreCase);
    }

    static void Save(List<string> list)
    {
        try
        {
            DipPaths.EnsureProgramData();
            File.WriteAllText(DipPaths.CreatedKittySessions, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch { }
    }
}
