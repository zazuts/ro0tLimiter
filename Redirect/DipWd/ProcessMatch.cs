using System.Collections.Concurrent;
using System.Net;

namespace DipWd;

sealed class ProcessMatch
{
    readonly IReadOnlyList<string> _patterns;
    readonly SocketTable _sockets = new();
    readonly ConcurrentDictionary<uint, bool> _pidCache = new();
    readonly AppLog _log;
    readonly HashSet<string> _skipNames;
    readonly uint _selfPid;
    SocketWatch? _watch;

    public ProcessMatch(IReadOnlyList<string> patterns, AppLog log)
    {
        _patterns = patterns;
        _log = log;
        _selfPid = (uint)Environment.ProcessId;
        _skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dip-wd", "dip-wd.exe",
            "dip-npcap", "dip-npcap.exe",
            "dip-tcp", "dip-tcp.exe",
            "kitty", "kitty.exe", "putty", "putty.exe", "klink", "klink.exe",
            "system", "idle", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "lsaiso", "fontdrvhost", "dwm",
            "svchost", "svchost.exe",
            "conhost", "conhost.exe",
        };
    }

    public void AttachWatch(SocketWatch watch) => _watch = watch;

    public bool ShouldProxy(bool udp, IPAddress localIp, int localPort, IPAddress remoteIp, int remotePort,
        out uint pid)
    {
        pid = 0;

        if (_watch is not null &&
            _watch.TryGetPid(udp, localIp, localPort, remoteIp, remotePort, out pid) &&
            pid != 0 && pid != _selfPid && pid != 4)
            return MatchesPid(pid);

        if (!_sockets.TryFind(udp, localIp, localPort, remoteIp, remotePort, out pid) || pid == 0)
            return false;
        if (pid == _selfPid || pid == 4)
            return false;
        return MatchesPid(pid);
    }

    public bool MatchesPid(uint pid)
    {
        if (_pidCache.TryGetValue(pid, out var cached))
            return cached;

        var path = ProcessPath.Of(pid);
        var name = Wildcard.FileNameOf(path);
        if (string.IsNullOrEmpty(name))
        {
            _pidCache[pid] = false;
            return false;
        }

        if (_skipNames.Contains(name) || _skipNames.Contains(Path.GetFileNameWithoutExtension(name)))
        {
            _pidCache[pid] = false;
            return false;
        }

        var star = _patterns.Any(p => p == "*" || p == "*.*");
        var ok = star || Wildcard.MatchesAny(path, _patterns);
        _pidCache[pid] = ok;
        if (ok)
            _log.Verbose("match pid=" + pid + " " + name);
        return ok;
    }
}
