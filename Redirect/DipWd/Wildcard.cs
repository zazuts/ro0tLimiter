using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DipWd;

static class Wildcard
{
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ';', ',', '|', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim().Trim('"'))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var s = path.Trim().Replace('/', '\\').Trim('"');
        var i = s.LastIndexOf('\\');
        return i >= 0 && i < s.Length - 1 ? s[(i + 1)..] : s;
    }

    public static bool MatchesAny(string processPathOrName, IReadOnlyList<string> patterns)
    {
        var name = FileNameOf(processPathOrName);
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var raw in patterns)
        {
            var pat = FileNameOf(raw);
            if (string.IsNullOrEmpty(pat)) continue;
            if (name.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
            if (HasWildcard(pat) && Match(name, pat)) return true;
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrEmpty(Path.GetExtension(name)) && !string.IsNullOrEmpty(stem))
            {
                if (HasWildcard(pat) && (Match(stem + ".exe", pat) || Match(stem + ".bin", pat)))
                    return true;
            }
        }
        return false;
    }

    public static bool HasWildcard(string pattern) =>
        !string.IsNullOrEmpty(pattern) && (pattern.Contains('*') || pattern.Contains('?'));

    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.OrdinalIgnoreCase);

    public static bool Match(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var rx = Patterns.GetOrAdd(pattern, static p => new Regex(
            "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        return rx.IsMatch(value);
    }
}
